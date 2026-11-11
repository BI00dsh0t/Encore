using System.Diagnostics.CodeAnalysis;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Core.Library;

// Cleanup: when a pass may run, and its steps.
public sealed partial class LibraryActor
{
    private void OnAcknowledge()
    {
        if (_cleanupBlocked)
        {
            _cleanupBlocked = false;
            _cleanupBlockedChanged(new LibraryCleanupBlocked(false));
            _dirty = true;
        }

        StartCleanup();
    }

    // Both Cleanup timers only post.
    private void OnCleanupTimer(object? state) => _mailbox.Post(new CleanupTick(Changed: false));

    private void OnCleanupTick(CleanupTick tick)
    {
        if (tick.Changed)
        {
            var limits = CleanupLimits();
            if (limits == _lastCleanup)
            {
                return;
            }

            _lastCleanup = limits;
        }

        StartCleanup();
    }

    private void OnCleanupStepOrEndPass()
    {
        try
        {
            OnCleanupStep();
        }
        catch (Exception e) when (LibraryPaths.IsFileFailure(e))
        {
            // A step that fails ends its pass, so the next trigger starts another.
            _pass.IsRunning = false;
            _failed(e);
        }
    }

    private void OnPreview(PreviewRequest request)
    {
        try
        {
            request.Done.TrySetResult(Preview(request.Args));
        }
        finally
        {
            // A no-op once answered.
            request.Done.TrySetException(new ErrorCodeException(ErrorCodes.CommandFailed, "The Cleanup preview failed."));
        }
    }

    private LibraryCleanupPlan Preview(LibraryCleanupPreview args)
    {
        var plan = PlanCleanup(new CleanupSettings(args.MaxBytes, args.MaxAgeSeconds), new HashSet<string>(), null);
        return new LibraryCleanupPlan(plan.Items.Count, plan.Bytes, [.. plan.Items.Select(item => item.Path)]);
    }

    /// <summary>The <c>cleanup</c> limits now; off without the port.</summary>
    private CleanupSettings CleanupLimits() => _cleanup?.Invoke() ?? CleanupOff;

    /// <summary>Whether Cleanup may run now: a limit is set, no rescan is pending, the index is trusted, and 60 s have passed.</summary>
    private bool CleanupOpen(CleanupSettings limits) =>
        (limits.MaxBytes is not null || limits.MaxAgeSeconds is not null)
        && _started
        && !_pending.RescanPending
        && !_cleanupBlocked
        && _time.GetUtcNow() - _startedAt >= CleanupStartDelay;

    private CleanupPlan PlanCleanup(CleanupSettings limits, IReadOnlySet<string> skip, Dictionary<string, bool>? cache) =>
        CleanupPlanner.Plan(_time.GetUtcNow(), limits, _index.Items, _folder, skip, key =>
        {
            if (cache is null || !cache.TryGetValue(key, out var inside))
            {
                inside = Inside(Path.GetFullPath(Path.Combine(_folder, key)));
                cache?.Add(key, inside);
            }

            return inside;
        });

    /// <summary>Starts a Cleanup pass unless one runs or Cleanup may not run: logs its plan, then posts its first step.</summary>
    private void StartCleanup()
    {
        if (_pass.IsRunning)
        {
            return;
        }

        var limits = CleanupLimits();
        if (!CleanupOpen(limits))
        {
            _limitWarning.RearmWhenOff(limits);
            return;
        }

        _pass.Reset();
        var plan = PlanCleanup(limits, _pass.Skipped, _pass.InsideOutputFolder);
        if (plan.Items.Count == 0)
        {
            EndCleanup(plan);
            return;
        }

        _pass.IsRunning = true;

        // Logged before the first step is posted, so what is posted meanwhile is handled before it.
        var files = string.Join(", ", plan.Items.Select(item => $"{item.Path} ({item.Size} bytes)"));
        Log($"Cleanup plan: {plan.Items.Count} files, {plan.Bytes} bytes: {files}.");
        _mailbox.Post(new CleanupStep());
    }

    /// <summary>One step of a pass: plans again, then deletes the plan's first item once it is re-checked, or ends the pass.</summary>
    private void OnCleanupStep()
    {
        if (!_pass.IsRunning)
        {
            return;
        }

        var limits = CleanupLimits();
        if (!CleanupOpen(limits))
        {
            _pass.IsRunning = false;
            _limitWarning.RearmWhenOff(limits);
            Log($"Cleanup stopped after deleting {_pass.Deleted} files ({_pass.DeletedBytes} bytes): it may not run now.");
            return;
        }

        var plan = PlanCleanup(limits, _pass.Skipped, _pass.InsideOutputFolder);
        if (plan.Items.Count == 0)
        {
            _pass.IsRunning = false;
            EndCleanup(plan);
            return;
        }

        var item = plan.Items[0];
        if (CleanupDelete(item.Path))
        {
            _pass.CountDeleted(item.Size);
        }
        else
        {
            // Not tried again in this pass.
            _pass.Skip(item.Path);
        }

        _mailbox.Post(new CleanupStep());
    }

    /// <summary>A pass ended with nothing left to delete: raises <c>cleanup-limit-unreachable</c> when it is due.</summary>
    private void EndCleanup(CleanupPlan plan)
    {
        var maxBytes = CleanupLimits().MaxBytes;
        if (_limitWarning.IsDue(maxBytes, plan) && maxBytes is { } limit)
        {
            _limitUnreachable?.Invoke(
                $"Cleanup can't keep the Clips folder within its size limit of {limit / 1e9:0.#} GB: "
                + $"the {plan.Remaining / 1e9:0.#} GB left are Favourites or files being saved.");
        }

        Log(
            $"Cleanup finished: {_pass.Deleted} files deleted ({_pass.DeletedBytes} bytes), {_pass.SkippedCount} skipped; "
            + $"the Library holds {plan.Remaining} bytes.");
    }

    /// <summary>Deletes an item's file for good once it is re-checked against the index and the disk; false when it wasn't.</summary>
    private bool CleanupDelete(string key)
    {
        if (!CleanupMayDelete(key, out var item, out var full))
        {
            return false;
        }

        _ownOperations.Register(full);
        try
        {
            File.Delete(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Cleanup skipped {key}: {e.Message}");
            return false;
        }

        _index.Remove(key, LibraryRemovalReasons.Cleanup);
        Log($"Cleanup deleted {key} ({item.Size} bytes).");
        return true;
    }

    // The re-check before a delete: each reason to leave the file is logged.
    private bool CleanupMayDelete(string key, [NotNullWhen(true)] out LibraryItem? item, out string full)
    {
        full = "";
        if (!_index.TryGet(key, out item) || item.BeingWritten || item.Favourite)
        {
            Log($"Cleanup skipped {key}: it is gone, being written or a Favourite.");
            return false;
        }

        full = Path.GetFullPath(Path.Combine(_folder, key));
        if (_index.IsBeingWritten(full) || !LibraryPaths.TryKey(_folder, full, out _) || !LibraryPaths.IsMp4(full))
        {
            Log($"Cleanup skipped {key}: it is being written, outside the Output Folder or not an .mp4.");
            return false;
        }

        var file = new FileInfo(full);
        if (!file.Exists || file.Length != item.Size)
        {
            _pending.Add(full);
            LogChangedOnDisk(key);
            return false;
        }

        if (!Inside(full))
        {
            Log(
                $"Cleanup skipped {key}: it is reached through a junction or symbolic link, "
                + "or its final path isn't in the Output Folder.");
            return false;
        }

        // Touched at the same size (reconcile compares sizes only): refreshed like a command's, for the next pass.
        var onDisk = LibraryFiles.Utc(file.LastWriteTimeUtc);
        if (onDisk != item.Modified)
        {
            _index.Change(item with { Modified = onDisk });
            LogChangedOnDisk(key);
            return false;
        }

        return true;
    }

    private void Log(string message) => _log?.Invoke(message);

    private void LogChangedOnDisk(string key) => Log($"Cleanup skipped {key}: the file changed on disk.");
}
