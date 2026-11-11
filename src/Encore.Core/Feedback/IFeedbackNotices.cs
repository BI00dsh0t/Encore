namespace Encore.Core.Feedback;

/// <summary>Where the <see cref="FeedbackRouter"/> raises its notices; the engine's warnings.</summary>
public interface IFeedbackNotices
{
    /// <summary>Raises <paramref name="notice"/>, overlay and dismissable, and returns its id.</summary>
    string Raise(FeedbackNotice notice);

    /// <summary>Removes the notice <paramref name="id"/> if it is still active; nothing happens if it isn't.</summary>
    void Withdraw(string id);
}
