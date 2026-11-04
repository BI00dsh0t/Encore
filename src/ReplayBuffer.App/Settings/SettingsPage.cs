using System.Windows;
using ReplayBuffer.App.Engine;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Settings;

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
        protected set => Set(ref _message, value);
    }

    public FrameworkElement View => _view ??= CreateView();

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
    protected async Task SendAsync(IReadOnlyList<SettingsOp> ops)
    {
        switch (await sender.UpdateAsync(Document, ops))
        {
            case SettingsSendResult.Committed:
                Message = null;
                break;

            case SettingsSendResult.Conflict conflict:
                Message = "The settings changed elsewhere, so this page reloaded. Make the change again if you still want it.";
                Show(conflict.Current);
                break;

            case SettingsSendResult.Failed failed:
                Message = $"Not saved ({failed.Code}): {failed.Message}";
                Show(Document);
                break;
        }
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
    /// Every page. An epic adds its page here, one line each: epic 2 the Track Layout editor,
    /// epic 3 Game Profiles.
    /// </summary>
    public static IReadOnlyList<SettingsPage> Create(SettingsSender sender, IEngineSession engine) =>
    [
        new GeneralSettingsPage(sender),
        new ReplayBufferSettingsPage(sender),
        new HotkeysPage(sender),
        new TrackLayoutPage(sender, engine),
        new GameProfilesPage(sender, engine),
    ];
}
