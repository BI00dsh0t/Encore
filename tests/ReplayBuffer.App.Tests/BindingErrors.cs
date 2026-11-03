using System.Diagnostics;

namespace ReplayBuffer.App.Tests;

/// <summary>Collects WPF data-binding trace messages.</summary>
internal sealed class BindingErrors : TraceListener
{
    public List<string> Messages { get; } = [];

    public override void Write(string? message)
    {
        lock (Messages)
        {
            Messages.Add(message ?? "");
        }
    }

    public override void WriteLine(string? message) => Write(message);
}
