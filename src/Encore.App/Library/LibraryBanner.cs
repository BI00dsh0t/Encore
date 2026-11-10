using System.Windows.Input;
using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Library;

/// <summary>The Library's banner: the index was rebuilt, and Cleanup is off until that is acknowledged.</summary>
internal sealed class LibraryBanner : Observable
{
    private readonly IEngineSession _engine;
    private bool _isCleanupBlocked;
    private string? _corruptWarningId;

    public LibraryBanner(IEngineSession engine)
    {
        _engine = engine;
        AcknowledgeCommand = new RelayCommand(() => _ = AcknowledgeAsync());
    }

    /// <summary>Whether the banner shows.</summary>
    public bool IsCleanupBlocked
    {
        get => _isCleanupBlocked;
        private set => Set(ref _isCleanupBlocked, value);
    }

    /// <summary>Sends <c>warning.dismiss</c> for the active <c>library-corrupt</c> warning.</summary>
    public ICommand AcknowledgeCommand { get; }

    public void Show(StateSnapshot state)
    {
        IsCleanupBlocked = state.CleanupBlocked;
        _corruptWarningId = state.Warnings.FirstOrDefault(warning => warning.Code == ErrorCodes.LibraryCorrupt)?.Id;
    }

    private async Task AcknowledgeAsync()
    {
        if (_corruptWarningId is not { } id)
        {
            return;
        }

        try
        {
            await _engine.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(id));
        }
        catch (ErrorCodeException)
        {
            // Already dismissed; the next snapshot clears the banner.
        }
    }
}
