namespace Encore.Contracts;

/// <summary>The one registry of error and warning codes; every code is kebab-case and appears here.</summary>
public static class ErrorCodes
{
    /// <summary>The command line, or a request's shape, is malformed.</summary>
    public const string InvalidArguments = "invalid-arguments";

    /// <summary>No top-level window matches the requested title.</summary>
    public const string WindowNotFound = "window-not-found";

    /// <summary>More than one top-level window matches the requested title.</summary>
    public const string WindowAmbiguous = "window-ambiguous";

    /// <summary>The FFmpeg DLLs are not beside the executable.</summary>
    public const string FFmpegMissing = "ffmpeg-missing";

    /// <summary>No audio endpoint matches the requested name.</summary>
    public const string AudioDeviceNotFound = "audio-device-not-found";

    /// <summary>More than one audio endpoint matches the requested name.</summary>
    public const string AudioDeviceAmbiguous = "audio-device-ambiguous";

    /// <summary>An audio endpoint could not be opened or failed while capturing.</summary>
    public const string AudioDeviceFailed = "audio-device-failed";

    /// <summary>Window capture could not start or delivered no frames.</summary>
    public const string CaptureFailed = "capture-failed";

    /// <summary>An encoder or the muxer failed.</summary>
    public const string EncodeFailed = "encode-failed";

    /// <summary>A warning: the GPU thread priority could not be set; capture runs without it.</summary>
    public const string GpuPriorityFailed = "gpu-priority-failed";

    /// <summary>The capture statistics file could not be written; the capture itself is complete.</summary>
    public const string StatsWriteFailed = "stats-write-failed";

    /// <summary>A settings update's <c>expectedRevision</c> is not the current revision.</summary>
    public const string SettingsConflict = "settings-conflict";

    /// <summary>A Game Profile override names a Global-only key.</summary>
    public const string KeyNotOverridable = "key-not-overridable";

    /// <summary>A settings update names a key that is not in the catalog.</summary>
    public const string SettingUnknownKey = "setting-unknown-key";

    /// <summary>A settings value does not fit its key's type or range.</summary>
    public const string SettingInvalidValue = "setting-invalid-value";

    /// <summary>A settings update names a Game Profile that does not exist.</summary>
    public const string ProfileNotFound = "profile-not-found";

    /// <summary>A settings update would put the cache folder inside the Clips folder, or the other way round; nothing changed.</summary>
    public const string RingPathInvalid = "ring-path-invalid";

    /// <summary>The settings file could not be written; nothing changed.</summary>
    public const string SettingsWriteFailed = "settings-write-failed";

    /// <summary>The settings file could not be read and was moved aside.</summary>
    public const string SettingsUnreadable = "settings-unreadable";

    /// <summary>The connection to the engine dropped, or there is none; a pending command fails locally with this.</summary>
    public const string EngineDisconnected = "engine-disconnected";

    /// <summary>The process serving the engine pipe failed the client's server check.</summary>
    public const string EngineUntrusted = "engine-untrusted";

    /// <summary>A command's <c>type</c> is not one the engine handles.</summary>
    public const string CommandUnknown = "command-unknown";

    /// <summary>A command failed inside the engine for a reason with no code of its own.</summary>
    public const string CommandFailed = "command-failed";

    /// <summary>The engine pipe for this instance already exists: another engine is serving it.</summary>
    public const string IpcPipeInUse = "ipc-pipe-in-use";

    /// <summary><c>warning.dismiss</c> names a warning that is not active.</summary>
    public const string WarningNotFound = "warning-not-found";

    /// <summary><c>warning.dismiss</c> names a warning that can't be dismissed.</summary>
    public const string WarningNotDismissable = "warning-not-dismissable";

    /// <summary>The App restarted an engine that crashed or hung; raised by the new engine.</summary>
    public const string EngineRestarted = "engine-restarted";

    /// <summary>The engine relaunched an App that exited with no <c>engine.shutdown</c>.</summary>
    public const string AppRestarted = "app-restarted";

    /// <summary>The engine crashed or hung again after 3 restarts within 10 minutes; the App stopped relaunching it.</summary>
    public const string EngineCrashLoop = "engine-crash-loop";

    /// <summary>The App exited again after 3 relaunches within 10 minutes; the engine stopped relaunching it.</summary>
    public const string AppCrashLoop = "app-crash-loop";

    /// <summary>Another copy of this process already runs for this instance.</summary>
    public const string InstanceRunning = "instance-running";

    /// <summary>A Recording was running when the engine restarted and was not resumed; only Rewind resumes after a restart.</summary>
    public const string RecordingInterrupted = "recording-interrupted";

    /// <summary>The PC went to sleep during a Recording, which ended at its last fragment and isn't restarted on resume.</summary>
    public const string RecordingSuspended = "recording-suspended";

    /// <summary>A session start was refused: a session is already running.</summary>
    public const string SessionRunning = "session-running";

    /// <summary>A retarget or end was refused: no session is running.</summary>
    public const string SessionNotRunning = "session-not-running";

    /// <summary>A retarget names a window of another process; changing the process means ending the session and starting one.</summary>
    public const string RetargetOtherProcess = "retarget-other-process";

    /// <summary>A Hotkey's combination is registered by another process; the other Hotkeys still register.</summary>
    public const string HotkeyTaken = "hotkey-taken";

    /// <summary>Two Hotkeys share a combination: such an update is refused, and a hand-edited file registers only the first.</summary>
    public const string HotkeyDuplicate = "hotkey-duplicate";

    /// <summary>Registering a Hotkey failed for a reason other than another process holding it.</summary>
    public const string HotkeyFailed = "hotkey-failed";

    /// <summary>A Hotkey action has no binding, so nothing is registered for it.</summary>
    public const string HotkeyUnbound = "hotkey-unbound";

    /// <summary>A Hotkey action is not used in the current <c>recordHotkeyMode</c>, so it isn't registered.</summary>
    public const string HotkeyInactive = "hotkey-inactive";

    /// <summary>A warning: an audio device in use dropped out; its Tracks record silence from it until it returns.</summary>
    public const string DeviceDropout = "device-dropout";

    /// <summary>A save was refused: it came under 1 s after the session's start or the last Reset, or Rewind held no keyframe.</summary>
    public const string ClipTooShort = "clip-too-short";

    /// <summary>A save was refused because two Clips were already being written; no Clip is made and nothing is Reset.</summary>
    public const string SaveBusy = "save-busy";

    /// <summary>A save failed because the volume is full; no file is left, nothing is Reset, and the next press retries.</summary>
    public const string DiskFull = "disk-full";

    /// <summary>A warning: a Recording's disk filled while Rewind kept its ring on disk, so Rewind stopped until there is room.</summary>
    public const string ReplayStoppedDiskFull = "replay-stopped-disk-full";

    /// <summary>A save failed because the Output Folder or the file could not be written; no file is left and nothing is Reset.</summary>
    public const string AccessDenied = "access-denied";

    /// <summary>A save failed for any other reason, such as a mux error; no file is left and nothing is Reset.</summary>
    public const string SaveFailed = "save-failed";

    /// <summary>A notice: a Clip was saved. Info, with the <c>clipSaved</c> sound; the engine dismisses it after 10 s.</summary>
    public const string ClipSaved = "clip-saved";

    /// <summary>A notice: a save made no Clip. Error, with the <c>saveFailed</c> sound; active until dismissed or the next Clip.</summary>
    public const string ClipFailed = "clip-failed";

    /// <summary>A notice: a Recording started. Info, with the <c>recordingStarted</c> sound; the engine dismisses it after 10 s.</summary>
    public const string RecordingStarted = "recording-started";

    /// <summary>A notice: a Recording stopped. Info, with the <c>recordingStopped</c> sound; the engine dismisses it after 10 s.</summary>
    public const string RecordingStopped = "recording-stopped";

    /// <summary>A warning: a forced encoder restart ended a Recording and started a new one at once, in a second file.</summary>
    public const string RecordingRestarted = "recording-restarted";

    /// <summary>The Library index was unreadable and was rebuilt from the Output Folder; Favourites may be lost.</summary>
    public const string LibraryCorrupt = "library-corrupt";

    /// <summary>A Library command named an item whose path, size or modified time no longer matches; nothing changed.</summary>
    public const string LibraryItemChanged = "library-item-changed";

    /// <summary>A Library rename or delete named an item a writer is still writing (a Clip or a Recording); nothing changed.</summary>
    public const string FileBeingWritten = "file-being-written";

    /// <summary>A Library rename's new name can't be a file name: empty, a forbidden character, or a reserved name.</summary>
    public const string InvalidFileName = "invalid-file-name";

    /// <summary>A Library rename's new name is already used by another file or Library item in that folder; nothing changed.</summary>
    public const string FileNameTaken = "file-name-taken";

    /// <summary>A Library delete was refused because the file's volume has no Recycle Bin; nothing is ever deleted permanently.</summary>
    public const string RecycleBinUnavailable = "recycle-bin-unavailable";

    /// <summary>A warning: Cleanup can't reach the size limit because only Favourites and files being written are left.</summary>
    public const string CleanupLimitUnreachable = "cleanup-limit-unreachable";

    /// <summary>A warning: a Track records the Output Device that Feedback Sounds play on, so recordings would hold the sounds.</summary>
    public const string FeedbackDeviceCaptured = "feedback-device-captured";

    /// <summary>A warning: available RAM reached Rewind's margin, so the ring stops growing and Clips may be shorter.</summary>
    public const string MemoryMargin = "memory-margin";

    /// <summary>A warning: after an encoder switch at a save, the next Clip starts later than the old Clip's end.</summary>
    public const string ResetGap = "reset-gap";

    /// <summary>A warning: a Monitor Recording was refused because a third encoder chain would exceed the encoder budget.</summary>
    public const string EncoderBudget = "encoder-budget";

    /// <summary>A warning: the GPU driver reset the capture device, so Rewind stopped buffering while the session rebuilds.</summary>
    public const string GpuReset = "gpu-reset";

    /// <summary>An error: the session couldn't rebuild after a GPU driver reset in time; Rewind stays off until the next Target.</summary>
    public const string GpuResetFailed = "gpu-reset-failed";

    /// <summary><c>dev.simulate</c> was sent to the installed instance, which refuses it.</summary>
    public const string NotDevInstance = "not-dev-instance";

    /// <summary>A warning: WGC couldn't capture the monitor, so Desktop Duplication is used instead; once per Monitor session.</summary>
    public const string MonitorCaptureFallback = "monitor-capture-fallback";

    /// <summary>The monitor a Monitor Recording captured was removed; the Recording ended at its last frame, its file kept.</summary>
    public const string MonitorRemoved = "monitor-removed";

    /// <summary>A warning: a chosen screen isn't attached as a Monitor Recording starts, so it is skipped. No Overlay message.</summary>
    public const string MonitorMissing = "monitor-missing";

    /// <summary>None of the chosen screens is attached, so the Monitor Recording doesn't start.</summary>
    public const string MonitorRecordingNoScreens = "monitor-recording-no-screens";

    /// <summary>A warning: a monitor arrived during a Monitor Recording of all screens; it joins at the next recording.</summary>
    public const string MonitorAddedNextRecording = "monitor-added-next-recording";

    /// <summary>A warning: a Monitor Recording's canvas is too large for H.264 and no other codec works, so it is scaled down.</summary>
    public const string MonitorRecordingScaled = "monitor-recording-scaled";

    /// <summary>A notice: a game was first detected while <c>captureNewGames</c> is off, so its profile was created Ignored.</summary>
    public const string NewGameNotCaptured = "new-game-not-captured";

    /// <summary>Every code in the registry.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        InvalidArguments,
        WindowNotFound,
        WindowAmbiguous,
        FFmpegMissing,
        AudioDeviceNotFound,
        AudioDeviceAmbiguous,
        AudioDeviceFailed,
        CaptureFailed,
        EncodeFailed,
        GpuPriorityFailed,
        StatsWriteFailed,
        SettingsConflict,
        KeyNotOverridable,
        SettingUnknownKey,
        SettingInvalidValue,
        ProfileNotFound,
        SettingsWriteFailed,
        SettingsUnreadable,
        EngineDisconnected,
        EngineUntrusted,
        CommandUnknown,
        CommandFailed,
        IpcPipeInUse,
        WarningNotFound,
        WarningNotDismissable,
        EngineRestarted,
        AppRestarted,
        EngineCrashLoop,
        AppCrashLoop,
        InstanceRunning,
        RecordingInterrupted,
        RecordingSuspended,
        SessionRunning,
        SessionNotRunning,
        RetargetOtherProcess,
        HotkeyTaken,
        HotkeyDuplicate,
        HotkeyFailed,
        HotkeyUnbound,
        HotkeyInactive,
        DeviceDropout,
        ClipTooShort,
        SaveBusy,
        DiskFull,
        ReplayStoppedDiskFull,
        AccessDenied,
        SaveFailed,
        ClipSaved,
        ClipFailed,
        RecordingStarted,
        RecordingStopped,
        RecordingRestarted,
        LibraryCorrupt,
        LibraryItemChanged,
        FileBeingWritten,
        InvalidFileName,
        FileNameTaken,
        RecycleBinUnavailable,
        CleanupLimitUnreachable,
        FeedbackDeviceCaptured,
        MemoryMargin,
        ResetGap,
        EncoderBudget,
        GpuReset,
        GpuResetFailed,
        NotDevInstance,
        MonitorCaptureFallback,
        MonitorRemoved,
        RingPathInvalid,
        MonitorMissing,
        MonitorRecordingNoScreens,
        MonitorAddedNextRecording,
        MonitorRecordingScaled,
        NewGameNotCaptured,
    ];
}
