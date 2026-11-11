using System.Windows.Controls.Primitives;

namespace Encore.App.Library;

/// <summary>The Favourite star: a toggle that never toggles itself; its state comes from the snapshot, a click only sends.</summary>
internal sealed class StarToggle : ToggleButton
{
    protected override void OnToggle()
    {
    }
}
