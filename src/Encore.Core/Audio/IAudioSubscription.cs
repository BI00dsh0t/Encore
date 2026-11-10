namespace Encore.Core.Audio;

/// <summary>One subscriber's tap on one device; once Dispose returns, its subscriber gets no further call.</summary>
public interface IAudioSubscription : IDisposable
{
    /// <summary>The device's first failure while capturing, if any; its stream was held silent after it.</summary>
    Exception? DeviceFault { get; }

    /// <summary>What the subscriber threw, if it did; it got no call after that.</summary>
    Exception? SubscriberFault { get; }
}
