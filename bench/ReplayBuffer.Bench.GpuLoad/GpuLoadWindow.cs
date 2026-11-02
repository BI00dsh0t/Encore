using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ReplayBuffer.Bench.GpuLoad;

/// <summary>
/// The load: a borderless, unowned popup over the primary monitor (or <c>--size</c>), with a D3D11
/// flip-model swap chain (FLIP_DISCARD; ALLOW_TEARING when the system supports it, presented with sync
/// interval 0) that draws one fullscreen triangle. Its pixel shader runs a <c>[loop]</c> of N trig
/// iterations animated by time, so every frame is new to WGC and the GPU does the work a game would.
/// </summary>
internal sealed unsafe class GpuLoadWindow : IDisposable
{
    public const string Title = "ReplayBuffer Bench GPU Load";
    private const string ClassName = "ReplayBuffer.Bench.GpuLoad";

    /// <summary>Presents measured with N frozen, after the warm-up, for the calibrated rate.</summary>
    private const double VerifySeconds = 3;

    private const string Shader =
        """
        cbuffer Params : register(b0)
        {
            float time;
            uint iterations;
            float2 size;
        };

        float4 VS(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float4 PS(float4 position : SV_Position) : SV_Target
        {
            float2 p = position.xy / size;
            float a = p.x * 6.2831853 + time;
            float b = p.y * 6.2831853 - time * 0.7;
            [loop]
            for (uint i = 0; i < iterations; i++)
            {
                a = sin(a + b * 0.5) * 1.7 + cos(b);
                b = cos(b - a * 0.3) * 1.3 + sin(a);
            }

            return float4(0.5 + 0.5 * sin(a), 0.5 + 0.5 * sin(b), 0.5 + 0.5 * sin(a + b + time), 1);
        }
        """;

    private readonly LoadOptions _options;
    private HWND _hwnd;
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _context;
    private IDXGISwapChain1* _swapChain;
    private ID3D11RenderTargetView* _target;
    private ID3D11VertexShader* _vertexShader;
    private ID3D11PixelShader* _pixelShader;
    private ID3D11Buffer* _constants;
    private bool _tearing;
    private string _adapter = "";
    private int _width;
    private int _height;

    private GpuLoadWindow(LoadOptions options) => _options = options;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float Time;
        public uint Iterations;
        public float Width;
        public float Height;
    }

    /// <exception cref="NoHardwareGpuException">Only a software adapter is present.</exception>
    public static GpuLoadWindow Create(LoadOptions options)
    {
        var load = new GpuLoadWindow(options);
        try
        {
            load._width = options.Width ?? PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
            load._height = options.Height ?? PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
            load.CreateWindow();
            load.CreateDevice();
            return load;
        }
        catch
        {
            load.Dispose();
            throw;
        }
    }

    /// <summary>Calibrates, prints the ready line, then draws until closed. Returns the exit code.</summary>
    public int Run()
    {
        WatchParent();
        var calibrator = new Calibrator(_options.TargetFps);
        var clock = Stopwatch.StartNew();
        var frequency = (double)Stopwatch.Frequency;
        var phase = Phase.Calibrating;
        var settle = Calibrator.SettleFrames;
        var windowStart = Stopwatch.GetTimestamp();
        var windowFrames = 0;
        var parameters = new Params { Width = _width, Height = _height, Iterations = calibrator.Iterations };

        while (true)
        {
            MSG message;
            while (PInvoke.PeekMessage(&message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (message.message == PInvoke.WM_QUIT)
                {
                    return LoadExit.Ok;
                }

                PInvoke.TranslateMessage(&message);
                PInvoke.DispatchMessage(&message);
            }

            parameters.Time = (float)clock.Elapsed.TotalSeconds;
            Draw(ref parameters);
            var presented = _swapChain->Present(0, _tearing ? DXGI_PRESENT.DXGI_PRESENT_ALLOW_TEARING : 0);
            if (presented == HRESULT.DXGI_ERROR_DEVICE_REMOVED || presented == HRESULT.DXGI_ERROR_DEVICE_RESET)
            {
                Console.Error.WriteLine($"error: the GPU was removed or reset (0x{RemovedReason():X8}).");
                return LoadExit.DeviceRemoved;
            }

            if (presented.Failed)
            {
                Console.Error.WriteLine($"error: Present failed (0x{(uint)presented.Value:X8}).");
                return LoadExit.Failed;
            }

            if (phase == Phase.Running)
            {
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            if (settle > 0)
            {
                // The frames queued before N changed are still draining: start the window after them.
                if (--settle == 0)
                {
                    windowStart = now;
                    windowFrames = 0;
                }

                continue;
            }

            windowFrames++;
            var elapsed = (now - windowStart) / frequency;
            if (phase == Phase.Calibrating)
            {
                if (elapsed >= Calibrator.WindowSeconds && windowFrames >= Calibrator.MinWindowFrames)
                {
                    var before = calibrator.Iterations;
                    parameters.Iterations = calibrator.Next(windowFrames / elapsed);
                    windowStart = now;
                    windowFrames = 0;
                    if (parameters.Iterations != before)
                    {
                        settle = Calibrator.SettleFrames;
                    }
                }

                if (clock.Elapsed.TotalSeconds >= _options.WarmupSeconds)
                {
                    phase = Phase.Verifying;
                    settle = Calibrator.SettleFrames;
                }
            }
            else if (elapsed >= VerifySeconds && windowFrames >= Calibrator.MinWindowFrames)
            {
                var line = ReadyLine.Format((nint)_hwnd.Value, _width, _height, _options.TargetFps, windowFrames / elapsed, parameters.Iterations, _adapter, _tearing);
                Console.Out.WriteLine(line);
                Console.Out.Flush();
                phase = Phase.Running;
            }
        }
    }

    public void Dispose()
    {
        Release(ref _constants);
        Release(ref _pixelShader);
        Release(ref _vertexShader);
        Release(ref _target);
        Release(ref _swapChain);
        Release(ref _context);
        Release(ref _device);
        if (!_hwnd.IsNull)
        {
            PInvoke.DestroyWindow(_hwnd);
            _hwnd = HWND.Null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_KEYDOWN && wParam.Value == (nuint)VIRTUAL_KEY.VK_ESCAPE)
        {
            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
            return new LRESULT(0);
        }

        if (message == PInvoke.WM_DESTROY)
        {
            PInvoke.PostQuitMessage(0);
            return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private uint RemovedReason()
    {
        try
        {
            _device->GetDeviceRemovedReason();
            return 0;
        }
        catch (Exception e)
        {
            return (uint)e.HResult;
        }
    }

    private static void Release<T>(ref T* pointer)
        where T : unmanaged
    {
        if (pointer is not null)
        {
            ((IUnknown*)pointer)->Release();
            pointer = null;
        }
    }

    private void CreateWindow()
    {
        var module = PInvoke.GetModuleHandle((PCWSTR)null);
        var instance = new HINSTANCE(module.Value);
        fixed (char* className = ClassName)
        fixed (char* title = Title)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProc,
                hInstance = instance,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(&windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed with error {Marshal.GetLastPInvokeError()}.");
            }

            // Unowned and visible, so Get-Process finds it as the MainWindowHandle, like a borderless game; topmost,
            // because SetForegroundWindow is refused to a process started from a background shell.
            _hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_APPWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST,
                className,
                title,
                WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_VISIBLE,
                0,
                0,
                _width,
                _height,
                HWND.Null,
                HMENU.Null,
                instance,
                null);
        }

        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException($"CreateWindowEx failed with error {Marshal.GetLastPInvokeError()}.");
        }

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_hwnd);
    }

    private void CreateDevice()
    {
        IDXGIFactory2* factory = null;
        IDXGIFactory5* factory5 = null;
        IDXGIAdapter1* adapter = null;
        ID3D11Texture2D* backBuffer = null;
        try
        {
            var factoryIid = IDXGIFactory2.IID_Guid;
            PInvoke.CreateDXGIFactory2(0, &factoryIid, (void**)&factory).ThrowOnFailure();

            // Adapter 0 drives the primary monitor (C1: one GPU).
            var enumerated = factory->EnumAdapters1(0, &adapter);
            if (enumerated == HRESULT.DXGI_ERROR_NOT_FOUND)
            {
                throw new NoHardwareGpuException("No DXGI adapter is present.");
            }

            enumerated.ThrowOnFailure();

            var desc = adapter->GetDesc1();
            _adapter = desc.Description.AsReadOnlySpan().SliceAtNull().ToString();
            const uint MicrosoftVendor = 0x1414;
            if ((desc.Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || desc.VendorId == MicrosoftVendor)
            {
                throw new NoHardwareGpuException($"Only a software adapter is present ({_adapter}).");
            }

            ReadOnlySpan<D3D_FEATURE_LEVEL> levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
            fixed (ID3D11Device** device = &_device)
            fixed (ID3D11DeviceContext** context = &_context)
            {
                PInvoke.D3D11CreateDevice(
                    (IDXGIAdapter*)adapter,
                    D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                    HMODULE.Null,
                    0,
                    levels,
                    PInvoke.D3D11_SDK_VERSION,
                    device,
                    out _,
                    context).ThrowOnFailure();
            }

            var factory5Iid = IDXGIFactory5.IID_Guid;
            if (((IUnknown*)factory)->QueryInterface(&factory5Iid, (void**)&factory5).Succeeded)
            {
                BOOL allow = false;
                try
                {
                    factory5->CheckFeatureSupport(DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow, (uint)sizeof(BOOL));
                    _tearing = allow;
                }
                catch (COMException)
                {
                    // No tearing support: present with sync interval 0, without the flag.
                }
            }

            var swapDesc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = _tearing ? DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0,
            };
            fixed (IDXGISwapChain1** swapChain = &_swapChain)
            {
                factory->CreateSwapChainForHwnd((IUnknown*)_device, _hwnd, &swapDesc, null, null, swapChain);
            }

            factory->MakeWindowAssociation(_hwnd, DXGI_MWA_FLAGS.DXGI_MWA_NO_ALT_ENTER);

            var textureIid = ID3D11Texture2D.IID_Guid;
            _swapChain->GetBuffer(0, &textureIid, (void**)&backBuffer);
            fixed (ID3D11RenderTargetView** target = &_target)
            {
                _device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, target);
            }

            CreateShaders();
        }
        finally
        {
            if (backBuffer is not null) backBuffer->Release();
            if (adapter is not null) adapter->Release();
            if (factory5 is not null) factory5->Release();
            if (factory is not null) factory->Release();
        }
    }

    private void CreateShaders()
    {
        ID3DBlob* vertex = null;
        ID3DBlob* pixel = null;
        try
        {
            vertex = Compile("VS", "vs_5_0");
            pixel = Compile("PS", "ps_5_0");
            fixed (ID3D11VertexShader** vs = &_vertexShader)
            {
                _device->CreateVertexShader(vertex->GetBufferPointer(), vertex->GetBufferSize(), null, vs);
            }

            fixed (ID3D11PixelShader** ps = &_pixelShader)
            {
                _device->CreatePixelShader(pixel->GetBufferPointer(), pixel->GetBufferSize(), null, ps);
            }

            var bufferDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(Params),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
            };
            fixed (ID3D11Buffer** constants = &_constants)
            {
                _device->CreateBuffer(&bufferDesc, null, constants);
            }
        }
        finally
        {
            if (pixel is not null) pixel->Release();
            if (vertex is not null) vertex->Release();
        }
    }

    private static ID3DBlob* Compile(string entry, string profile)
    {
        var source = Encoding.UTF8.GetBytes(Shader);
        var entryBytes = Encoding.ASCII.GetBytes(entry + "\0");
        var profileBytes = Encoding.ASCII.GetBytes(profile + "\0");
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        fixed (byte* src = source)
        fixed (byte* entryName = entryBytes)
        fixed (byte* target = profileBytes)
        {
            var hr = PInvoke.D3DCompile(src, (nuint)source.Length, default, null, null, new PCSTR(entryName), new PCSTR(target), PInvoke.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
            try
            {
                if (hr.Failed)
                {
                    var text = errors is null ? "" : Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer(), (int)errors->GetBufferSize());
                    throw new InvalidOperationException($"D3DCompile {entry} failed (0x{(uint)hr.Value:X8}): {text}");
                }
            }
            finally
            {
                if (errors is not null) errors->Release();
            }
        }

        return code;
    }

    private void Draw(ref Params parameters)
    {
        fixed (Params* data = &parameters)
        {
            _context->UpdateSubresource((ID3D11Resource*)_constants, 0, null, data, 0, 0);
        }

        // FLIP_DISCARD unbinds the back buffer on Present: bind it again each frame.
        var target = _target;
        _context->OMSetRenderTargets(1, &target, null);
        var viewport = new D3D11_VIEWPORT { Width = _width, Height = _height, MaxDepth = 1 };
        _context->RSSetViewports(1, &viewport);
        _context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        _context->VSSetShader(_vertexShader, null, 0);
        _context->PSSetShader(_pixelShader, null, 0);
        var constants = _constants;
        _context->PSSetConstantBuffers(0, 1, &constants);
        _context->Draw(3, 0);
    }

    /// <summary>Closes the window when the <c>--parent</c> process exits.</summary>
    private void WatchParent()
    {
        if (_options.ParentPid is not { } pid)
        {
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"The --parent process {pid} is not running.");
        }

        var hwnd = _hwnd;
        var watcher = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The parent can't be waited on: treat it as gone.
            }

            PInvoke.PostMessage(hwnd, PInvoke.WM_CLOSE, default, default);
        })
        { IsBackground = true, Name = "GpuLoad parent watch" };
        watcher.Start();
    }

    private enum Phase
    {
        Calibrating,
        Verifying,
        Running,
    }
}
