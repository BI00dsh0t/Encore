namespace Encore.App.Library;

/// <summary>The file couldn't be opened for playback.</summary>
internal sealed class PlayerException : Exception
{
    public PlayerException()
    {
    }

    public PlayerException(string message)
        : base(message)
    {
    }

    public PlayerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
