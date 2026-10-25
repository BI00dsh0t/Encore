namespace ReplayBuffer.Contracts;

/// <summary>A failure that carries one of the <see cref="ErrorCodes"/>.</summary>
public sealed class ErrorCodeException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
