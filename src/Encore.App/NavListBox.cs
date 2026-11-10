using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Encore.App;

/// <summary>The shell's nav list; the Settings label is shown but isn't an item for UI Automation, so screen readers skip it.</summary>
internal sealed class NavListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NavListBoxAutomationPeer(this);

    private sealed class NavListBoxAutomationPeer(NavListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() =>
            base.GetChildrenCore()?.Where(peer => peer is not ItemAutomationPeer { Item: NavItem { IsSection: true } }).ToList();
    }
}
