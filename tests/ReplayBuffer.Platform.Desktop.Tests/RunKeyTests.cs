using Microsoft.Win32;

namespace ReplayBuffer.Platform.Desktop.Tests;

/// <summary>Runs against a throwaway HKCU key, never the real Run key.</summary>
public sealed class RunKeyTests : IDisposable
{
    private readonly string _root = $@"Software\ReplayBuffer-Test-{Guid.NewGuid():N}";
    private readonly RunKey _key;

    public RunKeyTests() => _key = new RunKey($@"{_root}\Run", "ReplayBuffer");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void AMissingKeyReadsAsNull() => Assert.Null(_key.Read());

    [Fact]
    public void WriteThenReadReturnsTheCommand()
    {
        _key.Write(@"""C:\Install\ReplayBuffer.App.exe"" --tray");

        Assert.Equal(@"""C:\Install\ReplayBuffer.App.exe"" --tray", _key.Read());

        _key.Write("other");
        Assert.Equal("other", _key.Read());
    }

    [Fact]
    public void DeleteRemovesTheValue()
    {
        _key.Write("command");

        _key.Delete();

        Assert.Null(_key.Read());
    }

    [Fact]
    public void DeletingAMissingValueOrKeyIsFine()
    {
        _key.Delete();

        _key.Write("command");
        _key.Delete();
        _key.Delete();

        Assert.Null(_key.Read());
    }

    [Fact]
    public void StartupNamesTheRealRunValue()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RunKey.Startup.SubKey);
        Assert.Equal("ReplayBuffer", RunKey.Startup.ValueName);
    }
}
