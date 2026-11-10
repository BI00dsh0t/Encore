using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Encore.Core.Sessions;

namespace Encore.Platform.Windows;

/// <summary>The PC's sleep and resume; the system calls back on a thread of its own, with no window needed.</summary>
public sealed unsafe partial class SuspendResumeNotifications(ILogger logger) : IPowerNotifications
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeSuspend = 0x7;
    private const uint PbtApmResumeAutomatic = 0x12;

    /// <summary>Registers for suspend and resume; a failed registration is logged at Warning and returns a no-op.</summary>
    public IDisposable Subscribe(Action<PowerChange> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var subscription = new Subscription(logger, changed);
        var context = GCHandle.Alloc(subscription);
        var parameters = (DeviceNotifySubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(DeviceNotifySubscribeParameters));
        parameters->Callback = &OnPowerChange;
        parameters->Context = (void*)GCHandle.ToIntPtr(context);
        var handle = RegisterSuspendResumeNotification(parameters, DeviceNotifyCallback);
        if (handle == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            context.Free();
            NativeMemory.Free(parameters);
            logger.LogWarning("Sleep and resume can't be watched: RegisterSuspendResumeNotification failed (error {Error})", error);
            return subscription;
        }

        subscription.Registered(handle, context, parameters);
        return subscription;
    }

    /// <summary>The power change a <c>PBT_*</c> broadcast type is: suspend, either resume, else null.</summary>
    internal static PowerChange? Map(uint type) => type switch
    {
        PbtApmSuspend => PowerChange.Suspending,
        PbtApmResumeAutomatic or PbtApmResumeSuspend => PowerChange.Resumed,
        _ => null,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerChange(void* context, uint type, void* setting)
    {
        try
        {
            if (Map(type) is { } change && GCHandle.FromIntPtr((nint)context).Target is Subscription subscription)
            {
                subscription.Raise(change);
            }
        }
        catch (Exception)
        {
            // Never into the system's thread.
        }

        return 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint RegisterSuspendResumeNotification(DeviceNotifySubscribeParameters* recipient, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterSuspendResumeNotification(nint handle);

    /// <summary><c>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<void*, uint, void*, uint> Callback;
        public void* Context;
    }

    /// <summary>One registration: unregistered, its handle and parameters freed, on dispose.</summary>
    private sealed class Subscription(ILogger logger, Action<PowerChange> changed) : IDisposable
    {
        private readonly Lock _lock = new();
        private nint _handle;
        private GCHandle _context;
        private DeviceNotifySubscribeParameters* _parameters;
        private bool _disposed;

        public void Registered(nint handle, GCHandle context, DeviceNotifySubscribeParameters* parameters)
        {
            _handle = handle;
            _context = context;
            _parameters = parameters;
        }

        public void Raise(PowerChange change)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
            }

            logger.LogInformation("Power: {Change}", change == PowerChange.Suspending ? "the PC is going to sleep" : "the PC woke from sleep");
            changed(change);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            if (_handle == 0)
            {
                return;
            }

            // Once unregistered no callback runs, so the context is freed; after a failed unregistration it is leaked instead.
            if (!UnregisterSuspendResumeNotification(_handle))
            {
                logger.LogWarning("UnregisterSuspendResumeNotification failed (error {Error})", Marshal.GetLastPInvokeError());
                return;
            }

            _context.Free();
            NativeMemory.Free(_parameters);
            _handle = 0;
        }
    }
}
