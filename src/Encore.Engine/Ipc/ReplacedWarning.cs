using Encore.Contracts.Messages;

namespace Encore.Engine.Ipc;

/// <summary>One check's warning: raising a new one withdraws the last, so they never stack. The caller serializes calls.</summary>
internal sealed class ReplacedWarning(Warnings warnings, string code, string source)
{
    private string? _id;

    public void Raise(string message)
    {
        Withdraw();
        _id = warnings.Raise(code, WarningSeverity.Warning, source, message, overlay: false).Id;
    }

    public void Withdraw()
    {
        if (_id is { } id)
        {
            _id = null;
            warnings.Withdraw(id);
        }
    }
}
