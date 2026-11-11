using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.App.Settings;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class HotkeysViewModelTests
{
    private readonly FakeEngineSession _engine = new();

    [Fact]
    public void ACapturedComboSendsTheHotkeysWithOneChangeADuplicateSendsNothingAndSeparateSendsTheMode()
    {
        var defaults = Keys.Hotkeys.Default;
        var global = SettingsDocument.Empty.Global.Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(defaults));
        _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = 3, Global = global } };
        var updates = new List<SettingsUpdate>();
        _engine.Reply = (_, args) =>
        {
            updates.Add((SettingsUpdate)args);
            return new SettingsUpdateResult(4);
        };
        var viewModel = Create().Hotkeys;
        Assert.Equal("Alt+F10", viewModel.Rows[0].BindingText);

        viewModel.Rows[0].SetCommand.Execute(null);
        Assert.Same(viewModel.Rows[0], viewModel.CapturingRow);
        var combo = HotkeyBindings.FromKey(Key.F5, Key.None, ModifierKeys.Control | ModifierKeys.Shift)!;
        Assert.Equal(new HotkeyBinding(0x74, HotkeyModifiers.Control | HotkeyModifiers.Shift), combo);
        viewModel.Capture(HotkeyAction.Save, combo);
        Assert.Null(viewModel.CapturingRow);
        Assert.Equal("Ctrl+Shift+F5", viewModel.Rows[0].BindingText);

        viewModel.Capture(HotkeyAction.MicMute, combo);
        Assert.Single(updates);
        Assert.Equal("Ctrl+Shift+F5 is already the Save Clip Hotkey.", viewModel.Message);

        viewModel.Capture(HotkeyAction.MicMute, HotkeyBindings.FromKey(Key.A, Key.None, ModifierKeys.None)!);
        Assert.Single(updates);
        Assert.Equal("Add Ctrl, Alt, Shift or Win; only F1 to F24 work alone.", viewModel.Message);

        viewModel.IsSeparateMode = true;

        var saved = defaults with { Save = combo };
        Assert.All(updates, u => Assert.Equal(3, u.ExpectedRevision));
        Assert.Equal(
            [
                (Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(saved).GetRawText()),
                (Keys.RecordHotkeyMode.Name, "\"separate\""),
            ],
            updates.Select(u => Assert.Single(u.Ops)).Select(o => (o.Key, o.Value.GetRawText())));
        Assert.All(updates, u => Assert.Equal(SettingsScope.Global, u.Ops[0].Scope));

        Assert.Null(HotkeyBindings.FromKey(Key.System, Key.LeftAlt, ModifierKeys.Alt));
        Assert.Equal(new HotkeyBinding(0x79, HotkeyModifiers.Alt), HotkeyBindings.FromKey(Key.System, Key.F10, ModifierKeys.Alt));
    }

    [Fact]
    public void ClearingRecordStopSendsItNull()
    {
        var bound = Keys.Hotkeys.Default with { RecordStop = new HotkeyBinding(0x7A, HotkeyModifiers.Alt) };
        var global = SettingsDocument.Empty.Global.Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(bound));
        _engine.State = _engine.State! with { Settings = SettingsDocument.Empty with { Revision = 1, Global = global } };
        SettingsUpdate? update = null;
        _engine.Reply = (_, args) =>
        {
            update = (SettingsUpdate)args;
            return new SettingsUpdateResult(2);
        };

        Create().Hotkeys.Clear(HotkeyAction.RecordStop);

        Assert.Equal(Keys.Hotkeys.ToJson(bound with { RecordStop = null }).GetRawText(), Assert.Single(update!.Ops).Value.GetRawText());
    }

    [Fact]
    public void ATakenHotkeyShowsFailedAndTheOthersShowTheirOwnStatus()
    {
        _engine.State = _engine.State! with { Hotkeys = Status() };
        var viewModel = Create().Hotkeys;

        Assert.Equal("Failed: another app holds this combination", viewModel.Rows[0].StatusText);
        Assert.True(viewModel.Rows[0].IsFailed);
        Assert.Equal("Working", viewModel.Rows[1].StatusText);
        Assert.False(viewModel.Rows[1].IsFailed);
        Assert.Equal("Not used in this Record mode", viewModel.Rows[3].StatusText);
        Assert.False(viewModel.Rows[3].IsFailed);

        // No document value: the engine's effective bindings are shown.
        Assert.Equal("Alt+F10", viewModel.Rows[0].BindingText);
        Assert.Equal("Alt+F11", viewModel.Rows[3].BindingText);
        Assert.Equal("Not set", viewModel.Rows[4].BindingText);
        Assert.Equal("Not set", viewModel.Rows[4].StatusText);
    }

    [Fact]
    public void EachRecordModeShowsOnlyTheRowsItUses()
    {
        _engine.Reply = (_, _) => new SettingsUpdateResult(1);
        var viewModel = Create().Hotkeys;
        string[] Shown() => [.. viewModel.Rows.Where(r => r.IsShown).Select(r => r.Label)];

        Assert.Equal(["Save Clip", "Mic mute", "Record", "Monitor Recording"], Shown());
        viewModel.StartCapture(HotkeyAction.RecordToggle);

        viewModel.IsSeparateMode = true;

        Assert.Null(viewModel.CapturingRow);

        Assert.Equal(["Save Clip", "Mic mute", "Record start", "Record stop", "Monitor Recording start", "Monitor Recording stop"], Shown());
    }

    [Fact]
    public void TheViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        _engine.State = _engine.State! with { Hotkeys = Status() };
        var model = Create();
        model.Settings.Select(model.Hotkeys);
        model.Hotkeys.StartCapture(HotkeyAction.MicMute);

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.Settings.SelectedPage.View;
            view.Measure(new Size(600, 500));
            view.Arrange(new Rect(0, 0, 600, 500));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 500));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "hotkeys-settings.png")))
            {
                encoder.Save(file);
            }

            Assert.True(view.ActualHeight > 0);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = level;
        }

        Assert.DoesNotContain(errors.Messages, m => m.Contains("Hotkey", StringComparison.Ordinal));
    });

    private static readonly HotkeyBinding RecordStart = new(0x7A, HotkeyModifiers.Alt);

    // The defaults, Save taken by another app, Record start bound but inactive in Toggle mode, Record stop unbound.
    private static HotkeyStatus Status()
    {
        var d = Keys.Hotkeys.Default;
        return new HotkeyStatus(
        [
            new HotkeyBindingStatus(HotkeyAction.Save, d.Save, false, ErrorCodes.HotkeyTaken),
            new HotkeyBindingStatus(HotkeyAction.MicMute, d.MicMute, true, null),
            new HotkeyBindingStatus(HotkeyAction.RecordToggle, d.RecordToggle, true, null),
            new HotkeyBindingStatus(HotkeyAction.RecordStart, RecordStart, false, ErrorCodes.HotkeyInactive),
            new HotkeyBindingStatus(HotkeyAction.RecordStop, null, false, ErrorCodes.HotkeyUnbound),
            new HotkeyBindingStatus(HotkeyAction.MonitorRecordToggle, d.MonitorRecordToggle, true, null),
        ]);
    }

    private MainViewModel Create() => new(_engine, action => action());
}
