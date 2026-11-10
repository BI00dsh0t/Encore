using Encore.Core.Threading;

namespace Encore.Core.Tests;

public sealed class ActorMailboxTests
{
    [Fact]
    public void MessagesAreHandledInOrderOnTheActorThreadAndCloseRunsLast()
    {
        var seen = new List<string>();
        var threads = new HashSet<int>();
        ActorMailbox<int>? mailbox = null;
        mailbox = new ActorMailbox<int>(
            "ActorMailboxTest",
            message =>
            {
                seen.Add("message " + message);
                threads.Add(Environment.CurrentManagedThreadId);
                Assert.True(mailbox!.IsOnThread);
            },
            closed: () => seen.Add("closed"),
            drained: () => seen.Add("drained"));

        // Posted before the thread starts: one batch.
        Assert.True(mailbox.Post(1));
        Assert.True(mailbox.Post(2));
        mailbox.Start();
        mailbox.Close();

        Assert.Equal(["message 1", "message 2", "drained", "closed"], seen);
        Assert.Equal([mailbox.ThreadId], threads);
        Assert.False(mailbox.IsOnThread);
        Assert.False(mailbox.Post(3));
    }
}
