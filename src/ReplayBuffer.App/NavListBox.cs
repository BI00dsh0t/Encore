using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ReplayBuffer.App;

/// <summary>
/// The shell's nav list. The Settings section label is shown in the list but isn't one of its items for
/// UI Automation (EXPERIENCE.md Accessibility), so a screen reader counts only the pages.
/// </summary>
internal sealed class NavListBox : ListBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new NavListBoxAutomationPeer(this);

    private sealed class NavListBoxAutomationPeer(NavListBox owner) : ListBoxAutomationPeer(owner)
    {
        protected override List<AutomationPeer>? GetChildrenCore() =>
            base.GetChildrenCore()?.Where(peer => peer is not ItemAutomationPeer { Item: NavItem { IsSection: true } }).ToList();
    }
}
