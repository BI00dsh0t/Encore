using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

[Collection(nameof(WpfCollection))]
public sealed class FeedbackSoundsPageTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "Realtek", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{in-1}", "Microphone", "USB Mic", AudioFlow.Input, true);

    private readonly FakeEngineSession _engine = new();
    private readonly List<SettingsUpdate> _updates = [];

    public FeedbackSoundsPageTests()
    {
        _engine.Reply = (_, args) => args switch
        {
            SettingsUpdate update => Record(update),
            _ => Empty.Value,
        };
    }

    [Fact]
    public void SwitchingASoundOffSendsOneGlobalOpWithThatFlagOff()
    {
        SetState(revision: 3);
        var page = Create().FeedbackSounds;
        Assert.Equal(["Clip saved", "Save failed", "Recording started", "Recording stopped", "Disk full"], page.Sounds.Select(s => s.Label));

        page.Sounds[1].IsOn = true;
        Assert.Empty(_updates);

        page.Sounds[1].IsOn = false;

        var update = Assert.Single(_updates);
        Assert.Equal(3, update.ExpectedRevision);
        var op = Assert.Single(update.Ops);
        Assert.Equal(SettingsScope.Global, op.Scope);
        Assert.Equal(Keys.FeedbackSounds.Name, op.Key);
        Assert.Equal(Keys.FeedbackSounds.ToJson(Keys.FeedbackSounds.Default with { SaveFailed = false }).GetRawText(), op.Value.GetRawText());
        Assert.False(page.Sounds[1].IsOn);
    }

    [Fact]
    public void TheDeviceListOffersTheDefaultThenOutputsAndAMissingDeviceShowsAsNotConnected()
    {
        SetState(revision: 1, device: null, Speakers, Mic);
        var model = Create();
        var page = model.FeedbackSounds;
        Assert.Equal(["Default communications device", "Speakers"], page.Devices.Select(d => d.Label));
        Assert.Null(page.SelectedDevice!.EndpointId);

        page.SelectedDevice = page.Devices[1];
        Assert.Equal("\"{out-1}\"", Assert.Single(Assert.Single(_updates).Ops).Value.GetRawText());

        SetState(revision: 2, device: "{OUT-1}", Speakers, Mic);
        model.Refresh();
        Assert.Equal("{out-1}", page.SelectedDevice!.EndpointId);
        page.SelectedDevice = page.Devices[0];
        Assert.Equal("null", Assert.Single(_updates[^1].Ops).Value.GetRawText());

        SetState(revision: 3, device: "{gone}", Speakers);
        model.Refresh();
        Assert.Equal(["Default communications device", "Speakers", "{gone} (not connected)"], page.Devices.Select(d => d.Label));
        Assert.Equal("{gone}", page.SelectedDevice!.EndpointId);
    }

    [Fact]
    public void PlaySendsThePreviewForASwitchedOffSoundAndShowsAnError()
    {
        SetState(revision: 1, sounds: Keys.FeedbackSounds.Default with { ClipSaved = false });
        var page = Create().FeedbackSounds;

        page.Sounds[0].PlayCommand.Execute(null);
        Assert.Equal(new FeedbackPreview(FeedbackSound.ClipSaved), _engine.Sent[^1]);

        _engine.Reply = (_, _) => throw new ErrorCodeException(ErrorCodes.FeedbackDeviceCaptured, "The device is in use.");
        page.Sounds[4].PlayCommand.Execute(null);
        Assert.Equal($"Couldn't play ({ErrorCodes.FeedbackDeviceCaptured}): The device is in use.", page.Message);
    }

    [Fact]
    public void TheCapturedWarningShowsOnThePageAndItsViewRendersWithoutBindingErrors() => Sta.Run(() =>
    {
        SetState(revision: 1, device: "{gone}", Speakers, Mic);
        _engine.State = _engine.State! with
        {
            Warnings =
            [
                new Warning("w1", ErrorCodes.FeedbackDeviceCaptured, WarningSeverity.Warning, "feedback", "Another app has the sound device to itself.", DateTimeOffset.UtcNow, false, null, true),
            ],
        };
        var model = Create();
        Assert.Contains(model.Pages, p => p.Title == "Sounds");
        model.SelectedPage = model.FeedbackSounds;
        Assert.Equal("Another app has the sound device to itself.", model.FeedbackSounds.CapturedWarning);

        var errors = new BindingErrors();
        var level = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        try
        {
            var view = model.SelectedPage.View;
            view.Measure(new Size(600, 400));
            view.Arrange(new Rect(0, 0, 600, 400));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            var white = new DrawingVisual();
            using (var context = white.RenderOpen())
            {
                context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 600, 400));
            }

            bitmap.Render(white);
            bitmap.Render(view);

            // The HITL record's picture of the page.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "feedback-sounds.png")))
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

        Assert.DoesNotContain(errors.Messages, m => m.Contains("FeedbackSound", StringComparison.Ordinal) || m.Contains("FeedbackDeviceItem", StringComparison.Ordinal));
    });

    private SettingsUpdateResult Record(SettingsUpdate update)
    {
        _updates.Add(update);
        return new SettingsUpdateResult(update.ExpectedRevision + 1);
    }

    private void SetState(long revision, string? device = null, params AudioDevice[] devices) =>
        SetState(revision, Keys.FeedbackSounds.Default, device, devices);

    private void SetState(long revision, FeedbackSounds sounds, string? device = null, params AudioDevice[] devices)
    {
        var global = SettingsDocument.Empty.Global
            .Add(Keys.FeedbackSounds.Name, Keys.FeedbackSounds.ToJson(sounds))
            .Add(Keys.FeedbackSoundDevice.Name, Keys.FeedbackSoundDevice.ToJson(device));
        _engine.State = _engine.State! with
        {
            Settings = SettingsDocument.Empty with { Revision = revision, Global = global },
            Devices = new AudioDeviceList(devices),
        };
    }

    private MainViewModel Create() => new(_engine, action => action());
}
