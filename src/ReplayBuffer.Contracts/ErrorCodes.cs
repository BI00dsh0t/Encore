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
    /// that was running. The engine raises it at start, once per Recording the last instance left
    /// open, after trimming that file to its last whole fragment; its message names the path.
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
    /// A warning: a forced encoder restart ended a running Recording and started a new one at once,
    /// so it continues in a second file; the notice names both. With the <c>recordingStarted</c>
    /// sound; the engine withdraws it after 10 s.
    /// </summary>
    public const string RecordingRestarted = "recording-restarted";

    /// <summary>
    /// <c>library.json</c> and its <c>.bak</c> were both unreadable, so the Library index was rebuilt
    /// from the Output Folder and Favourites may be lost. A dismissable warning; Cleanup stays off
    /// (<c>cleanupBlocked</c>) until it is dismissed.
    /// </summary>
    public const string LibraryCorrupt = "library-corrupt";

    /// <summary>
    /// A Library command named an item by an identity (path, size and modified time, AD-3) that no
    /// longer matches the index or the file on disk. Nothing changed; the App acts again on the
    /// item the snapshot now shows. Never retried automatically.
    /// </summary>
    public const string LibraryItemChanged = "library-item-changed";

    /// <summary>A Library rename or delete named an item a writer is still writing (a Clip or a Recording); nothing changed.</summary>
    public const string FileBeingWritten = "file-being-written";

    /// <summary>
    /// A Library rename's new name can't be a file name: empty, holding a character Windows forbids,
    /// ending with a dot or a space, or a reserved device name. Nothing changed.
    /// </summary>
    public const string InvalidFileName = "invalid-file-name";

    /// <summary>A Library rename's new name is already used by another file or Library item in that folder; nothing changed.</summary>
    public const string FileNameTaken = "file-name-taken";

    /// <summary>
    /// A Library delete was refused because the file's volume has no Recycle Bin (or the shell would
    /// delete it permanently). The file and its item are untouched; nothing is ever deleted permanently.
    /// </summary>
    public const string RecycleBinUnavailable = "recycle-bin-unavailable";

    /// <summary>
    /// A warning: a Track of the active Track Layout mixes the Output Device that Feedback Sounds
    /// play on, so its recordings would hold the sounds. Withdrawn when that is no longer so.
    /// </summary>
    public const string FeedbackDeviceCaptured = "feedback-device-captured";

    /// <summary>
    /// A warning (R3): the machine's available RAM reached the Replay Buffer's margin, so the ring
    /// stops growing and Clips may be shorter than the Buffer Window; or, inside it, the ring holds
    /// under 1 s, so a save makes no Clip until memory frees up. Raised once per entry into the margin.
    /// </summary>
    public const string MemoryMargin = "memory-margin";

    /// <summary>
    /// A warning (AD-7): the Replay Buffer switched to a profile's new encoder chain at a save, and
    /// the new chain's first keyframe came more than one frame after the old Clip's end, so the
    /// next Clip starts that much later (no frame arrived after the press, a static window, say).
    /// Raised once per such switch, with the gap's length.
    /// </summary>
    public const string ResetGap = "reset-gap";

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
        RecordingRestarted,
        LibraryCorrupt,
        LibraryItemChanged,
        FileBeingWritten,
        InvalidFileName,
        FileNameTaken,
        RecycleBinUnavailable,
        FeedbackDeviceCaptured,
        MemoryMargin,
        ResetGap,
        NotDevInstance,
    ];
}
