namespace Encore.Platform.Audio;

/// <summary>The result of matching a device name: the endpoint when exactly one matched, otherwise the candidates to list.</summary>
public sealed record EndpointMatch(EndpointMatchOutcome Outcome, AudioEndpoint? Endpoint, IReadOnlyList<AudioEndpoint> Candidates);
