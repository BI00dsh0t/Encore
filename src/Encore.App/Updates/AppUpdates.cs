using System.IO;
using Encore.Contracts;

namespace Encore.App.Updates;

/// <summary>The Settings page's Updates section: checks in the background and installs only when the App restarts.</summary>
internal sealed class AppUpdates : Observable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly IUpdater _updater;
    private readonly string _preferencesPath;
    private readonly Func<bool> _isBusy;
    private readonly Func<Task> _quit;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private UpdatePreferences _preferences;
    private string _status;
    private string? _readyVersion;
    private bool _isChecking;

    public AppUpdates(IUpdater updater, string preferencesPath, Func<bool> isBusy, Func<Task> quit, Action<string>? log = null)
    {
        _updater = updater;
        _preferencesPath = preferencesPath;
        _isBusy = isBusy;
        _quit = quit;
        _log = log ?? (_ => { });
        _preferences = UpdatePreferences.Read(preferencesPath);
        _status = updater.IsInstalled ? "" : "Only the installed copy of Encore updates itself.";
        CheckNowCommand = new RelayCommand(() => _ = CheckAsync(), () => CanUpdate && !_isChecking);
        RestartToUpdateCommand = new RelayCommand(() => _ = RestartToUpdateAsync(), () => IsReady);
    }

    /// <summary>The version of this build, as the page shows it.</summary>
    public string VersionText { get; } = $"Version {AppVersion.Short}";

    /// <summary>Whether this copy can update: only the one the installer put in place.</summary>
    public bool CanUpdate => _updater.IsInstalled;

    /// <summary>Whether to check on start and once a day.</summary>
    public bool CheckAutomatically
    {
        get => _preferences.CheckAutomatically;
        set => SavePreferences(_preferences with { CheckAutomatically = value });
    }

    /// <summary>Whether to take beta versions as well as stable ones.</summary>
    public bool Beta
    {
        get => _preferences.Beta;
        set => SavePreferences(_preferences with { Beta = value });
    }

    /// <summary>What the last check found, in words.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Whether a downloaded version waits for a restart.</summary>
    public bool IsReady => _readyVersion is not null;

    public RelayCommand CheckNowCommand { get; }

    public RelayCommand RestartToUpdateCommand { get; }

    /// <summary>A section that never checks, for a copy that can't update.</summary>
    public static AppUpdates Disabled(string preferencesPath) =>
        new(new NoUpdater(), preferencesPath, () => false, () => Task.CompletedTask);

    /// <summary>Checks a minute after start, then once a day, while automatic checks are on. Runs on the UI thread.</summary>
    public async Task RunAsync()
    {
        if (!CanUpdate)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstCheckDelay, _stop.Token);
            while (true)
            {
                if (CheckAutomatically && !IsReady)
                {
                    await CheckAsync();
                }

                await Task.Delay(CheckInterval, _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The App is quitting.
        }
    }

    /// <summary>Stops the background checks.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Checks the channel once and downloads a newer version if there is one.</summary>
    public async Task CheckAsync()
    {
        if (!CanUpdate || _isChecking)
        {
            return;
        }

        SetChecking(true);
        Status = "Checking for updates…";
        try
        {
            var version = await _updater.DownloadNewerAsync(Beta, _stop.Token);
            ShowResult(version);
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        catch (Exception e) when (e is InvalidDataException or Velopack.Exceptions.ChecksumFailedException)
        {
            Status = "An update didn't match its checksum, so it was deleted unused. Encore tries again tomorrow.";
            _log($"Update rejected: {e.Message}");
        }
        catch (Exception e)
        {
            Status = "Couldn't check for updates. Encore tries again tomorrow.";
            _log($"Update check failed: {e.Message}");
        }
        finally
        {
            SetChecking(false);
        }
    }

    private void ShowResult(string? version)
    {
        if (version is null)
        {
            Status = "Encore is up to date.";
            return;
        }

        _readyVersion = version;
        OnPropertyChanged(nameof(IsReady));
        RestartToUpdateCommand.RaiseCanExecuteChanged();
        Status = $"Version {version} is ready. It installs when Encore next starts.";
        _log($"Update {version} downloaded");
    }

    private async Task RestartToUpdateAsync()
    {
        if (!IsReady)
        {
            return;
        }

        if (_isBusy())
        {
            Status = "Finish the Recording and any Clip being saved, then try again.";
            return;
        }

        _log($"Restarting to install {_readyVersion}");
        _updater.ApplyOnExit();
        await _quit();
    }

    private void SetChecking(bool checking)
    {
        _isChecking = checking;
        CheckNowCommand.RaiseCanExecuteChanged();
    }

    private void SavePreferences(UpdatePreferences preferences)
    {
        if (preferences == _preferences)
        {
            return;
        }

        _preferences = preferences;
        preferences.Write(_preferencesPath);
        OnPropertyChanged(nameof(CheckAutomatically));
        OnPropertyChanged(nameof(Beta));
    }

    private sealed class NoUpdater : IUpdater
    {
        public bool IsInstalled => false;

        public Task<string?> DownloadNewerAsync(bool beta, CancellationToken cancel) => Task.FromResult<string?>(null);

        public void ApplyOnExit()
        {
        }
    }
}
