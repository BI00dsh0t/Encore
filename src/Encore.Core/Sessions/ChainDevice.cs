using Encore.Contracts.Settings;

namespace Encore.Core.Sessions;

/// <summary>One audio device of a <see cref="ChainKey"/>: its endpoint, compared ignoring case, and flow.</summary>
public readonly record struct ChainDevice
{
    public ChainDevice(string endpointId, AudioFlow flow)
    {
        ArgumentNullException.ThrowIfNull(endpointId);
        EndpointId = endpointId.ToUpperInvariant();
        Flow = flow;
    }

    public string EndpointId { get; }

    public AudioFlow Flow { get; }
}
