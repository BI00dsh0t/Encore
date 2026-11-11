using Encore.Core.Feedback;

namespace Encore.Core.Tests;

/// <summary>Keeps every notice raised, in order, and the ones still active by id.</summary>
internal sealed class FakeFeedbackNotices : IFeedbackNotices
{
    public List<FeedbackNotice> Raised { get; } = [];

    public Dictionary<string, FeedbackNotice> Active { get; } = [];

    public string Raise(FeedbackNotice notice)
    {
        var id = Guid.NewGuid().ToString("N");
        Raised.Add(notice);
        Active[id] = notice;
        return id;
    }

    public void Withdraw(string id) => Active.Remove(id);
}
