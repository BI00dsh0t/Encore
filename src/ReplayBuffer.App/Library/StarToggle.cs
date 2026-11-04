using System.Windows.Controls.Primitives;

namespace ReplayBuffer.App.Library;

/// <summary>
/// The Library row's Favourite star: a toggle that never toggles itself. Its checked state comes only from
/// the snapshot's <c>IsFavourite</c>, and a click only runs its command (<c>library.setFavourite</c>). A UI
/// Automation Toggle, which skips the click and the command, changes nothing either.
/// </summary>
internal sealed class StarToggle : ToggleButton
{
    protected override void OnToggle()
    {
    }
}
