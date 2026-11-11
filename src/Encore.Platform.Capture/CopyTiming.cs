using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.Com;

namespace Encore.Platform.Capture;

/// <summary>Measures what one frame copy costs the GPU, with timestamp queries.</summary>
public static unsafe class CopyTiming
{
    /// <summary>The average GPU time of one <see cref="FrameCopies.CopyFrameTo"/> in µs, or null if unmeasurable. Blocks.</summary>
    public static double? TimeCopies(this D3D11Device device, nint source, nint target, int x, int y, int count)
    {
        ((ID3D11Texture2D*)source)->GetDesc(out var description);
        var size = new VideoSize((int)description.Width, (int)description.Height);
        ID3D11Query* disjoint = null, begin = null, end = null;
        try
        {
            var disjointQuery = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT };
            var stampQuery = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP };
            device.Device->CreateQuery(&disjointQuery, &disjoint);
            device.Device->CreateQuery(&stampQuery, &begin);
            device.Device->CreateQuery(&stampQuery, &end);

            device.Context->Begin((ID3D11Asynchronous*)disjoint);
            device.Context->End((ID3D11Asynchronous*)begin);
            for (var i = 0; i < count; i++)
            {
                device.CopyFrameTo(source, size, target, x, y, size);
            }

            device.Context->End((ID3D11Asynchronous*)end);
            device.Context->End((ID3D11Asynchronous*)disjoint);

            TimestampDisjoint data;
            ulong started, ended;
            WaitForData(device, (ID3D11Asynchronous*)disjoint, &data, (uint)sizeof(TimestampDisjoint));
            WaitForData(device, (ID3D11Asynchronous*)begin, &started, sizeof(ulong));
            WaitForData(device, (ID3D11Asynchronous*)end, &ended, sizeof(ulong));
            return data.Disjoint != 0 ? null : (ended - started) * 1e6 / data.Frequency / count;
        }
        finally
        {
            if (end is not null) ((IUnknown*)end)->Release();
            if (begin is not null) ((IUnknown*)begin)->Release();
            if (disjoint is not null) ((IUnknown*)disjoint)->Release();
        }
    }

    /// <summary>Spins, yielding, until the query's result is ready and read into data.</summary>
    private static void WaitForData(D3D11Device device, ID3D11Asynchronous* query, void* data, uint size)
    {
        while (GetData(device, query, data, size) != HRESULT.S_OK)
        {
            Thread.Yield();
        }
    }

    /// <summary>Calls GetData by vtable slot to see its HRESULT: S_FALSE means not ready, which CsWin32's wrapper hides.</summary>
    private static HRESULT GetData(D3D11Device device, ID3D11Asynchronous* query, void* data, uint size)
    {
        const int getDataSlot = 29; // IUnknown 3, ID3D11DeviceChild 4, then ID3D11DeviceContext's 22 before it
        var context = device.Context;
        return ((delegate* unmanaged[Stdcall]<ID3D11DeviceContext*, ID3D11Asynchronous*, void*, uint, uint, HRESULT>)
            (*(void***)context)[getDataSlot])(context, query, data, size, 0);
    }

    /// <summary>D3D11_QUERY_DATA_TIMESTAMP_DISJOINT.</summary>
    private struct TimestampDisjoint
    {
        public ulong Frequency;
        public int Disjoint;
    }
}
