using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Encore.App;

/// <summary>Opens a button's context menu as a drop-down, for the buttons that show a menu on a plain click.</summary>
internal static class ButtonMenu
{
    /// <summary>Opens the menu under the button that raised the click; does nothing for a sender without a menu.</summary>
    public static void OpenBelow(object sender)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
