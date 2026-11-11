using Encore.Contracts.Messages;
using Encore.Core.Feedback;
using Encore.Core.Library;
using Encore.Core.Sessions;
using Encore.Engine.Audio;
using Encore.Engine.Ipc;
using Encore.Engine.Sessions;
using Encore.Engine.Settings;
using Encore.Engine.Supervision;

namespace Encore.Engine.Hosting;

/// <summary>Registers the engine's commands on its router, one method per group of messages.</summary>
internal static class CommandHandlers
{
    private static ValueTask<Empty> Done => ValueTask.FromResult(Empty.Value);

    public static void RegisterSettingsAndAudio(
        CommandRouter router,
        SettingsStore store,
        DeviceListPublisher devices,
        TrackMeters meters,
        IpcServer server)
    {
        router.Register(
            SettingsMessages.Update,
            (update, _) => ValueTask.FromResult(new SettingsUpdateResult(store.Update(update).Revision)));
        router.Register(AudioMessages.List, (_, _) => ValueTask.FromResult(devices.Current));
        router.Register(AudioMessages.Watch, (watch, _) =>
        {
            meters.Watch(watch.Layout);
            return Done;
        });
        router.Register(AudioMessages.Unwatch, (_, _) =>
        {
            meters.Unwatch();
            return Done;
        });

        // A watch outlives no client, or a crashed App would leave devices open; queued, so a newer watch is kept.
        server.LastDisconnected += () =>
        {
            var generation = meters.Generation;
            ThreadPool.QueueUserWorkItem(_ => UnwatchIfNoClient(server, meters, generation));
        };
    }

    public static void RegisterFeedback(
        CommandRouter router,
        Warnings warnings,
        LibraryActor library,
        string? corruptWarning,
        FeedbackSoundPlayer sounds)
    {
        router.Register(FeedbackMessages.Dismiss, (dismiss, _) =>
        {
            var dismissed = warnings.Dismiss(dismiss);

            // Dismissing library-corrupt acknowledges the rebuilt index, so Cleanup may run again.
            if (dismiss.Id == corruptWarning)
            {
                library.AcknowledgeCorrupt();
            }

            return ValueTask.FromResult(dismissed);
        });
        router.Register(FeedbackMessages.Preview, (preview, _) =>
        {
            sounds.Preview(preview.Sound);
            return Done;
        });
    }

    public static void RegisterLibrary(CommandRouter router, LibraryActor library)
    {
        router.Register(LibraryMessages.SetFavourite, async (favourite, _) =>
        {
            // Never retried: a stale identity comes back as library-item-changed.
            await library.SetFavourite(favourite).ConfigureAwait(false);
            return Empty.Value;
        });
        router.Register(LibraryMessages.Rename, async (rename, _) =>
        {
            // Never retried: a refusal comes back with its code for the App to show.
            await library.Rename(rename).ConfigureAwait(false);
            return Empty.Value;
        });
        router.Register(LibraryMessages.Delete, async (delete, _) =>
        {
            await library.Delete(delete).ConfigureAwait(false);
            return Empty.Value;
        });
        router.Register(
            LibraryMessages.CleanupPreview,
            async (preview, _) => await library.PreviewCleanup(preview).ConfigureAwait(false));
    }

    public static void RegisterGamesAndMonitors(CommandRouter router, SettingsStore store, IDisplayMonitors displays)
    {
        router.Register(RecordingMessages.MonitorsList, (_, _) => ValueTask.FromResult(new MonitorList(displays.List())));
        router.Register(GameMessages.Add, (add, _) => ValueTask.FromResult(ManualGames.Add(store, add)));
    }

    public static void RegisterDevelopment(CommandRouter router, DevSimulator simulator) =>
        router.Register(DevMessages.Simulate, (simulate, _) => ValueTask.FromResult(simulator.Simulate(simulate)));

    public static void RegisterEngine(CommandRouter router, AppSupervisor apps, TaskCompletionSource shutdown)
    {
        router.Register(EngineMessages.Attach, (attach, _) => ValueTask.FromResult(apps.Attach(attach)));
        router.Register(
            EngineMessages.Shutdown,
            (_, _) =>
            {
                // Before the reply, so the App can't exit and be relaunched in between.
                apps.Stop();
                return Done;
            },
            () => shutdown.TrySetResult());
    }

    public static void RegisterTestOnly(CommandRouter router, IpcServer server, HeartbeatPublisher heartbeat)
    {
        router.Register(DebugCommands.Hold, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return Empty.Value;
        });
        router.Register(
            DebugCommands.Drop,
            async (drop, _) =>
            {
                if (drop.RefuseNew)
                {
                    await server.StopAcceptingAsync().ConfigureAwait(false);
                }

                return Empty.Value;
            },
            () => _ = Task.Delay(100).ContinueWith(_ => server.DropAll("debug.drop"), TaskScheduler.Default));
        router.Register(DebugCommands.Hang, async (_, cancellationToken) =>
        {
            await heartbeat.StopAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return Empty.Value;
        });
    }

    private static void UnwatchIfNoClient(IpcServer server, TrackMeters meters, long generation)
    {
        try
        {
            if (server.ConnectionCount == 0)
            {
                meters.UnwatchIf(generation);
            }
        }
        catch (ObjectDisposedException)
        {
            // The engine is stopping; its dispose closes the watch.
        }
    }
}
