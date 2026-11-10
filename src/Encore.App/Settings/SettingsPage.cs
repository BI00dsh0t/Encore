using System.Windows;
using Encore.App.Engine;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>
/// One page of the Settings area. It renders the settings document it is given and changes it only
/// through <see cref="SendAsync"/>, against the revision it rendered (AD-4). Its members run on the UI thread.
/// </summary>
internal abstract class SettingsPage(SettingsSender sender) : Observable
{
    private FrameworkElement? _view;
    private string? _message;
    private bool _rendered;

    /// <summary>The page's name in the Settings navigation.</summary>
    public abstract string Title { get; }

    /// <summary>A note about the last change: why it wasn't saved, or that the page reloaded.</summary>
    public string? Message
    {
        get => _message;
        protected set
        {
            // Raised on every set, so a page hosting this one mirrors a repeated note too.
            _message = value;
            OnPropertyChanged();
        }
    }

    public FrameworkElement View => _view ??= CreateView();

    /// <summary>The page's drill-in, when it opens item pages (story 10.10's Games); null for a plain page.</summary>
    public virtual DrillIn? DrillIn => null;

    /// <summary>Whether the page holds an unsaved draft: the nav item shows a dot (story 10.5). Raised as a property change.</summary>
    public virtual bool HasUnsavedChanges => false;

    /// <summary>The document the page shows, and the basis of its updates.</summary>
    protected SettingsDocument Document { get; private set; } = SettingsDocument.Empty;

    /// <summary>Shows <paramref name="document"/> if it is a different revision from the one shown.</summary>
    public void Render(SettingsDocument document)
    {
        if (_rendered && document.Revision == Document.Revision)
        {
            return;
        }

        Show(document);
    }

    protected abstract FrameworkElement CreateView();

    /// <summary>Copies the document's values into the page's properties.</summary>
    protected abstract void OnRender(SettingsDocument document);

    /// <summary>
    /// Sends <paramref name="ops"/>. On success the <c>settings.changed</c> event renders the result; on a
    /// conflict the page reloads the current document, and on a refusal it shows its document again.
    /// </summary>
    /// <returns>The refusal's error code, or null when the update committed.</returns>
    protected async Task<string?> SendAsync(IReadOnlyList<SettingsOp> ops)
    {
        switch (await sender.UpdateAsync(Document, ops))
        {
            case SettingsSendResult.Committed:
                Message = null;
                return null;

            case SettingsSendResult.Conflict conflict:
                Message = "The settings changed elsewhere, so this page reloaded. Make the change again if you still want it.";
                Show(conflict.Current);
                return Contracts.ErrorCodes.SettingsConflict;

            case SettingsSendResult.Failed failed:
                Message = $"Not saved ({failed.Code}): {failed.Message}";
                Show(Document);
                return failed.Code;

            default:
                return null;
        }
    }

    /// <summary>
    /// A folder picker's send: as <see cref="SendAsync"/>, but a refusal the picker words itself
    /// (<paramref name="pickerCode"/>) shows on the picker instead of the page's error bar.
    /// </summary>
    protected async Task<string?> SendForPickerAsync(IReadOnlyList<SettingsOp> ops, string pickerCode)
    {
        var code = await SendAsync(ops);
        if (code == pickerCode)
        {
            Message = null;
        }

        return code;
    }

    private void Show(SettingsDocument document)
    {
        _rendered = true;
        Document = document;
        OnRender(document);
    }
}

/// <summary>The pages of the Settings area, in navigation order.</summary>
internal static class SettingsPages
{
    /// <summary>
    /// Every page, in story 10.9's order: General, Clips, Hotkeys, Audio Tracks, Games, Sounds, and
    /// Storage and Cleanup. The Recordings page is gone; its screen picker is on Hotkeys.
    /// </summary>
    /// <param name="defaultCacheFolder">The engine's default cache folder, shown as the cache folder's default.</param>
    /// <param name="gameShell">The shell the Games pages use; null for Windows'.</param>
    public static IReadOnlyList<SettingsPage> Create(SettingsSender sender, IEngineSession engine, string? defaultCacheFolder = null, IGameShell? gameShell = null) =>
    [
        new GeneralSettingsPage(sender),
        new RewindSettingsPage(sender, defaultCacheFolder: defaultCacheFolder),
        new HotkeysPage(sender, engine),
        new TrackLayoutPage(sender, engine),
        new GameProfilesPage(sender, engine, gameShell),
        new FeedbackSoundsPage(sender, engine),
        new LibrarySettingsPage(sender, engine, defaultCacheFolder),
    ];
}
