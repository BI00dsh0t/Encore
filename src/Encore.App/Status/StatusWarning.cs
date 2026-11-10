using System.Globalization;
using System.Windows.Input;
using Encore.Contracts.Messages;

namespace Encore.App.Status;

/// <summary>One warning row of the status line.</summary>
internal sealed class StatusWarning
{
    public StatusWarning(Warning warning, Func<string, Task> dismiss)
    {
        Id = warning.Id;
        Time = warning.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Severity = warning.Severity.ToString();
        Message = warning.Message;
        CanDismiss = warning.Dismissable;
        DismissCommand = new RelayCommand(() => _ = dismiss(Id));
    }

    public string Id { get; }

    /// <summary>When it was raised, local time, as HH:mm:ss.</summary>
    public string Time { get; }

    public string Severity { get; }

    public string Message { get; }

    public bool CanDismiss { get; }

    /// <summary>Sends <c>warning.dismiss</c>; the row stays until the snapshot drops it.</summary>
    public ICommand DismissCommand { get; }
}
