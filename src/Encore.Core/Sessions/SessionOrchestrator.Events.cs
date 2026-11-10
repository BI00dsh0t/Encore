namespace Encore.Core.Sessions;

public sealed partial class SessionOrchestrator
{
    /// <summary>One session's post-backs, tagged with its id so a late one from an ended session is ignored.</summary>
    private sealed class Events(SessionOrchestrator owner, long id) : ISessionEvents
    {
        public void Closed(string reason) => owner.Post(new SessionClosed(id, reason));

        public void Failed(Exception failure) => owner.Post(new SessionFailed(id, failure));

        public void ChainRestarted(long qpc) => owner.Post(new SessionChainRestarted(id, qpc));

        public void Anchored(long qpc) => owner.Post(new SessionAnchored(id, qpc));

        public void DeviceLost(Exception failure) => owner.Post(new SessionDeviceLost(id, failure));
    }
}
