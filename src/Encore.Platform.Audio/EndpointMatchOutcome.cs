namespace Encore.Platform.Audio;

/// <summary>How a device name matched the endpoints.</summary>
public enum EndpointMatchOutcome
{
    Found,
    NotFound,
    Ambiguous,
}
