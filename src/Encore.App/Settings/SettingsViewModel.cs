using System.Runtime.CompilerServices;
using System.Windows;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>One page of the Settings area; it changes the document only through <see cref="SendAsync"/>. UI thread.</summary>
internal abstract class SettingsViewModel(SettingsSender sender) : Observable
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

    /// <summary>The page's drill-in, when it opens item pages; null for a plain page.</summary>
    public virtual DrillIn? DrillIn => null;

    /// <summary>Whether the page is on screen; a page that watches live data starts and stops with it.</summary>
    public virtual bool IsOpen { get; set; }

    /// <summary>Whether the page holds an unsaved draft: the nav item shows a dot. Raised as a property change.</summary>
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

    /// <summary>Sends the ops and returns a refusal's error code, or null; a conflict reloads the current document.</summary>
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

    /// <summary>A setter's work for a setting sent on every change: keeps the value, raises the property and sends one op.</summary>
    protected void SetAndSend<T>(ref T field, T value, SettingKey<T> key, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name))
        {
            _ = SendAsync([SettingsValues.SetGlobal(key, value)]);
        }
    }

    /// <summary>A render's work for one Global setting: takes the document's value and raises the property.</summary>
    protected void ShowGlobal<T>(ref T field, SettingKey<T> key, string name)
    {
        field = SettingsValues.Global(Document, key);
        OnPropertyChanged(name);
    }

    /// <summary>Shows the rendered value again, with why the entry wasn't sent.</summary>
    protected void Refuse(string message, string property)
    {
        Message = message;
        OnPropertyChanged(property);
    }

    /// <summary>A folder picker's send: a refusal the picker words itself shows on the picker, not the page's error bar.</summary>
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
