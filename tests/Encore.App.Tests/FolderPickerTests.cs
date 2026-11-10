using System.IO;
using Encore.App.Settings;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>Story 10.9's folder picker: set on a pick, remove on Use default, nothing on a cancel, refusals and Open.</summary>
public sealed class FolderPickerTests
{
    private readonly List<SettingsOp> _sent = [];
    private string? _refusal;

    [Fact]
    public void APickSendsSetUseDefaultSendsRemoveAndACancelSendsNothing()
    {
        var picker = Create();
        picker.Show(null, @"C:\Users\sam\Videos\Encore");
        Assert.Equal(@"Default (C:\Users\sam\Videos\Encore)", picker.PathText);
        Assert.False(picker.CanUseDefault);

        picker.Dialog = _ => null;
        picker.BrowseCommand.Execute(null);
        Assert.Empty(_sent);

        picker.Dialog = initial =>
        {
            Assert.Equal(@"C:\Users\sam\Videos\Encore", initial);
            return @"D:\Clips\";
        };
        picker.BrowseCommand.Execute(null);
        var set = Assert.Single(_sent);
        Assert.Equal((SettingsScope.Global, Keys.OutputFolder.Name, SettingsOpKind.Set, Keys.OutputFolder.ToJson(@"D:\Clips").GetRawText()), (set.Scope, set.Key, set.Op, set.Value.GetRawText()));

        picker.Show(@"D:\Clips", @"C:\Users\sam\Videos\Encore");
        Assert.Equal(@"D:\Clips", picker.PathText);
        Assert.True(picker.CanUseDefault);
        picker.UseDefaultCommand.Execute(null);
        var remove = _sent[^1];
        Assert.Equal((Keys.OutputFolder.Name, SettingsOpKind.Remove), (remove.Key, remove.Op));
    }

    [Fact]
    public void ARefusedPickShowsItsTextAndKeepsTheOldPath()
    {
        var picker = Create();
        picker.Show(@"D:\Clips", "");
        picker.Validate = _ => "Encore can't write to that folder.";
        picker.Dialog = _ => @"C:\Windows\System32";

        picker.BrowseCommand.Execute(null);

        Assert.Empty(_sent);
        Assert.Equal("Encore can't write to that folder.", picker.Problem);
        Assert.Equal(@"D:\Clips", picker.PathText);

        // The next good pick clears it.
        picker.Validate = _ => null;
        picker.Dialog = _ => @"E:\Clips";
        picker.BrowseCommand.Execute(null);
        Assert.Null(picker.Problem);
        Assert.Single(_sent);
    }

    [Fact]
    public void AnEngineRefusalTheHostWordsShowsOnThePicker()
    {
        var picker = Create();
        picker.Show(@"D:\Clips", "");
        picker.RefusalText = code => code == "ring-path-invalid" ? "The Clips folder can't contain the cache folder." : null;
        _refusal = "ring-path-invalid";
        picker.Dialog = _ => @"D:\";

        picker.BrowseCommand.Execute(null);

        Assert.Single(_sent);
        Assert.Equal("The Clips folder can't contain the cache folder.", picker.Problem);
        Assert.Equal(@"D:\Clips", picker.PathText);
    }

    [Fact]
    public void OpenInExplorerIsOffForAMissingFolderAndTheFreeSpaceShows()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rb-picker-" + Guid.NewGuid().ToString("N"));
        var opened = new List<string>();
        var picker = Create();
        picker.OpenFolder = opened.Add;
        picker.FreeBytes = _ => 212_000_000_000;

        picker.Show(folder, "");
        Assert.False(picker.OpenCommand.CanExecute(null));
        Assert.Equal("The folder doesn't exist yet.", picker.OpenTip);

        Directory.CreateDirectory(folder);
        try
        {
            picker.Refresh();
            Assert.True(picker.OpenCommand.CanExecute(null));
            picker.OpenCommand.Execute(null);
            Assert.Equal([folder], opened);
            Assert.Equal("212 GB free on " + Path.GetPathRoot(folder)![..2], picker.FreeSpaceText);
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    private FolderPicker Create() => new(Keys.OutputFolder, ops =>
    {
        _sent.AddRange(ops);
        return Task.FromResult(_refusal);
    });
}
