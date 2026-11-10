using Encore.App.Engine;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Settings;

/// <summary>Keeps the engine metering the layout an open Audio Tracks page shows, and stops it when the page closes.</summary>
internal sealed class MeterWatch
{
    private readonly IEngineSession _engine;
    private readonly Func<bool> _isOpen;
    private readonly Func<TrackLayout> _watched;
    private MeterLevels _levels = MeterLevels.Empty;
    private string? _engineInstanceId;
    private string? _watchedJson;
    private string? _watchedInstanceId;
    private bool _unwatchPending;

    /// <summary>isOpen says whether the page is on screen; watched is the layout to meter, with its probes.</summary>
    public MeterWatch(IEngineSession engine, Func<bool> isOpen, Func<TrackLayout> watched)
    {
        _engine = engine;
        _isOpen = isOpen;
        _watched = watched;
    }

    /// <summary>The levels to show: the last ones while the page is open, none while it is closed.</summary>
    public MeterLevels Levels => _isOpen() ? _levels : MeterLevels.Empty;

    /// <summary>The page came on screen: watch its layout.</summary>
    public void PageOpened()
    {
        _unwatchPending = false;
        Watch();
    }

    /// <summary>The page left the screen: stop the engine's watch.</summary>
    public void PageClosed()
    {
        _watchedJson = null;
        _watchedInstanceId = null;
        _ = UnwatchAsync();
    }

    /// <summary>Takes the snapshot's meter levels; watches again when the engine is a new run or the link was lost.</summary>
    public void Show(MeterLevels levels, string engineInstanceId)
    {
        _levels = levels;
        _engineInstanceId = engineInstanceId;

        // The engine stops a watch when its last client leaves, so a reconnect watches again.
        if (_engine.Status != EngineStatus.Connected)
        {
            _watchedInstanceId = null;
            _watchedJson = null;
        }
        else if (_isOpen() && _watchedInstanceId != engineInstanceId)
        {
            Watch();
        }
        else if (!_isOpen() && _unwatchPending)
        {
            // A reconnect to the same engine run: its watch outlived the link, so stop it now.
            _unwatchPending = false;
            _ = UnwatchAsync();
        }
    }

    /// <summary>Re-watches when the metered layout (names aside) or the picker's probes changed.</summary>
    public void LayoutMayHaveChanged()
    {
        if (_isOpen() && MeteredJson() != _watchedJson)
        {
            Watch();
        }
    }

    // Names don't change what the engine meters, and the name box updates per keystroke.
    private string MeteredJson()
    {
        var layout = _watched();
        return Keys.TrackLayout.ToJson(layout with { Tracks = [.. layout.Tracks.Select(track => track with { Name = "" })] }).GetRawText();
    }

    private void Watch()
    {
        _watchedJson = MeteredJson();
        _watchedInstanceId = _engineInstanceId;
        _ = WatchAsync(_watchedJson, new MetersWatch(_watched()));
    }

    private async Task WatchAsync(string json, MetersWatch watch)
    {
        try
        {
            await _engine.SendAsync(AudioMessages.Watch, watch);
        }
        catch (ErrorCodeException e)
        {
            // Refused: the next draft change tries again. Disconnected: so does the next snapshot.
            if (_watchedJson == json)
            {
                _watchedJson = null;
                if (e.Code == ErrorCodes.EngineDisconnected)
                {
                    _watchedInstanceId = null;
                }
            }
        }
    }

    private async Task UnwatchAsync()
    {
        try
        {
            await _engine.SendAsync(AudioMessages.Unwatch, Empty.Value);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
        {
            // The engine may still be metering; the next snapshot sends this again while the page stays closed.
            _unwatchPending = !_isOpen();
        }
        catch (ErrorCodeException)
        {
            // Refused: there is nothing to stop.
        }
    }
}
