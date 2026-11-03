namespace ReplayBuffer.Contracts;

/// <summary>
/// The one registry of error and warning codes. Every code is kebab-case and appears here.
/// </summary>
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

    /// <summary>The App restarted an engine that crashed or hung; raised by the new engine (AD-16).</summary>
    public const string EngineRestarted = "engine-restarted";

    /// <summary>The engine relaunched an App that exited with no <c>engine.shutdown</c> (AD-16).</summary>
    public const string AppRestarted = "app-restarted";

    /// <summary>Another copy of this process already runs for this instance (AD-16).</summary>
    public const string InstanceRunning = "instance-running";

    /// <summary>
    /// A Recording was running when the engine restarted, and was not resumed (AD-16).
    /// The resume rule, for epic-recordings: after an engine restart only the Replay Buffer
    /// resumes, never a Recording or Monitor Recording, and this warning is raised for each one
    /// that was running. Nothing raises it yet.
    /// </summary>
    public const string RecordingInterrupted = "recording-interrupted";

    /// <summary>A session start was refused: a session is already running (AD-19).</summary>
    public const string SessionRunning = "session-running";

    /// <summary>A retarget or end was refused: no session is running.</summary>
    public const string SessionNotRunning = "session-not-running";

    /// <summary>
    /// A retarget names a window of another process; changing the process is end-session plus
    /// new-session (AD-19).
    /// </summary>
    public const string RetargetOtherProcess = "retarget-other-process";

    /// <summary>A Hotkey's combination is registered by another process; the other Hotkeys still register.</summary>
    public const string HotkeyTaken = "hotkey-taken";

    /// <summary>
    /// Two Hotkeys share one combination: a <c>settings.update</c> making them so is refused, and in a
    /// hand-edited file only the first in <c>HotkeyAction</c> order registers.
    /// </summary>
    public const string HotkeyDuplicate = "hotkey-duplicate";

    /// <summary>Registering a Hotkey failed for a reason other than another process holding it.</summary>
    public const string HotkeyFailed = "hotkey-failed";

    /// <summary>A Hotkey action has no binding, so nothing is registered for it.</summary>
    public const string HotkeyUnbound = "hotkey-unbound";

    /// <summary>A Hotkey action is not used in the current <c>recordHotkeyMode</c>, so it isn't registered.</summary>
    public const string HotkeyInactive = "hotkey-inactive";

    /// <summary>A warning: an audio device in use dropped out; its Tracks record silence from it until it returns.</summary>
    public const string DeviceDropout = "device-dropout";

    /// <summary>
    /// A save was refused (R4): it came less than 1 s after the session's start or the last Reset,
    /// or the Replay Buffer held no keyframe before its cut. No Clip is made and nothing is Reset.
    /// </summary>
    public const string ClipTooShort = "clip-too-short";

    /// <summary>
    /// A save was refused (R6): two Clips were already being written. No Clip is made, the session
    /// isn't asked for anything and nothing is Reset.
    /// </summary>
    public const string SaveBusy = "save-busy";

    /// <summary>
    /// A save failed (R6) because the volume is full. Raised by the Clip writer now, and by the
    /// Recording writer later. No file is left and nothing is Reset; the next press retries.
    /// </summary>
    public const string DiskFull = "disk-full";

    /// <summary>
    /// A save failed (R6) because the Output Folder or the file could not be written. Raised by the
    /// Clip writer now, and by the Recording writer later. No file is left and nothing is Reset.
    /// </summary>
    public const string AccessDenied = "access-denied";

    /// <summary>
    /// A save failed (R6) for any other reason, such as a mux error. Raised by the Clip writer now,
    /// and by the Recording writer later. No file is left and nothing is Reset.
    /// </summary>
    public const string SaveFailed = "save-failed";

    /// <summary>A notice: a Clip was saved. Info, with the <c>clipSaved</c> sound; the engine dismisses it after 10 s.</summary>
    public const string ClipSaved = "clip-saved";

    /// <summary>
    /// A notice: a save made no Clip. Error, with the <c>saveFailed</c> sound; active until it is
    /// dismissed or the next Clip is saved.
    /// </summary>
    public const string ClipFailed = "clip-failed";

    /// <summary>A notice: a Recording started. Info, with the <c>recordingStarted</c> sound; the engine dismisses it after 10 s.</summary>
    public const string RecordingStarted = "recording-started";

    /// <summary>A notice: a Recording stopped. Info, with the <c>recordingStopped</c> sound; the engine dismisses it after 10 s.</summary>
    public const string RecordingStopped = "recording-stopped";

    /// <summary>
    /// <c>library.json</c> and its <c>.bak</c> were both unreadable, so the Library index was rebuilt
    /// from the Output Folder and Favourites may be lost. A dismissable warning; Cleanup stays off
    /// (<c>cleanupBlocked</c>) until it is dismissed.
    /// </summary>
    public const string LibraryCorrupt = "library-corrupt";

    /// <summary>
    /// A warning: a Track of the active Track Layout mixes the Output Device that Feedback Sounds
    /// play on, so its recordings would hold the sounds. Withdrawn when that is no longer so.
    /// </summary>
    public const string FeedbackDeviceCaptured = "feedback-device-captured";

    /// <summary><c>dev.simulate</c> was sent to the installed instance, which refuses it.</summary>
    public const string NotDevInstance = "not-dev-instance";

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
        InstanceRunning,
        RecordingInterrupted,
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
        AccessDenied,
        SaveFailed,
        ClipSaved,
        ClipFailed,
        RecordingStarted,
        RecordingStopped,
        LibraryCorrupt,
        FeedbackDeviceCaptured,
        NotDevInstance,
    ];
}
