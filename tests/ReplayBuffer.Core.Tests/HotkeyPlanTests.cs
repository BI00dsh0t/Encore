using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Hotkeys;

namespace ReplayBuffer.Core.Tests;

public sealed class HotkeyPlanTests
{
    private const int VkF24 = 0x87;
    private const int CtrlAltShift = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    private static readonly HotkeyBinding A = new(VkF24, CtrlAltShift);
    private static readonly HotkeyBinding B = new(VkF24 - 1, CtrlAltShift);
    private static readonly HotkeyBinding C = new(VkF24 - 2, CtrlAltShift);
    private static readonly HotkeyBinding D = new(VkF24 - 3, CtrlAltShift);

    private static readonly HotkeySettings All = new(A, B, C, D, new HotkeyBinding(0x41, HotkeyModifiers.Win), new HotkeyBinding(0x42, HotkeyModifiers.Win));

    [Fact]
    public void ToggleModeRegistersRecordToggleAndNotStartOrStop()
    {
        var plan = HotkeyPlan.For(All, RecordHotkeyMode.Toggle);

        Assert.Equal(HotkeyPlan.Actions, plan.Select(e => e.Action));
        Assert.Equal(
            [null, null, null, ErrorCodes.HotkeyInactive, ErrorCodes.HotkeyInactive, null],
            plan.Select(e => e.SkipCode));
        Assert.Equal(All.RecordStart, plan[3].Binding);
    }

    [Fact]
    public void SeparateModeRegistersStartAndStopAndNotRecordToggle()
    {
        var plan = HotkeyPlan.For(All, RecordHotkeyMode.Separate);

        Assert.Equal(
            [null, null, ErrorCodes.HotkeyInactive, null, null, null],
            plan.Select(e => e.SkipCode));
    }

    [Fact]
    public void AnUnboundActionIsSkippedAsUnbound()
    {
        var plan = HotkeyPlan.For(All with { MicMute = null }, RecordHotkeyMode.Toggle);

        Assert.Equal(ErrorCodes.HotkeyUnbound, plan[1].SkipCode);
        Assert.Null(plan[1].Binding);
        Assert.True(plan[0].Registers);
    }

    [Fact]
    public void ADuplicateRegistersTheFirstInActionOrderOnly()
    {
        // NoRepeat doesn't make a different combination.
        var hotkeys = All with { MonitorRecordToggle = A with { Modifiers = A.Modifiers | HotkeyModifiers.NoRepeat } };

        var plan = HotkeyPlan.For(hotkeys, RecordHotkeyMode.Toggle);

        Assert.True(plan[0].Registers);
        Assert.Equal(ErrorCodes.HotkeyDuplicate, plan[5].SkipCode);
        Assert.Equal((HotkeyAction.Save, HotkeyAction.MonitorRecordToggle), HotkeyPlan.FindDuplicate(hotkeys));
    }

    [Fact]
    public void AnInactiveActionDoesNotTakeACombination()
    {
        // recordStart is inactive in toggle mode, so monitorRecordToggle may share its combination there.
        var hotkeys = All with { MonitorRecordToggle = D };

        var plan = HotkeyPlan.For(hotkeys, RecordHotkeyMode.Toggle);

        Assert.True(plan[5].Registers);
        Assert.Equal((HotkeyAction.RecordStart, HotkeyAction.MonitorRecordToggle), HotkeyPlan.FindDuplicate(hotkeys));
    }

    [Fact]
    public void ADocumentWithoutHotkeysBindsNothingOffTheInstalledInstance()
    {
        var plan = HotkeyPlan.For(SettingsDocument.Empty, useCatalogDefault: false);

        Assert.All(plan, e => Assert.False(e.Registers));
        Assert.Equal(
            Enumerable.Repeat(ErrorCodes.HotkeyUnbound, HotkeyPlan.Actions.Count),
            plan.Select(e => e.SkipCode));
    }

    [Fact]
    public void ADocumentWithoutHotkeysBindsTheDefaultsOnTheInstalledInstance()
    {
        var plan = HotkeyPlan.For(SettingsDocument.Empty, useCatalogDefault: true);

        Assert.Equal(HotkeyPlan.For(Keys.Hotkeys.Default, RecordHotkeyMode.Toggle), plan);
        Assert.Equal(Keys.Hotkeys.Default.Save, plan[0].Binding);
        Assert.Equal(4, plan.Count(e => e.Registers));
    }

    [Fact]
    public void AnExplicitValueAppliesOnAnyInstanceWithTheDocumentsMode()
    {
        var document = SettingsDocument.Empty with
        {
            Global = SettingsMaps.EmptyValues
                .Add(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(All))
                .Add(Keys.RecordHotkeyMode.Name, Keys.RecordHotkeyMode.ToJson(RecordHotkeyMode.Separate)),
        };

        Assert.Equal(HotkeyPlan.For(All, RecordHotkeyMode.Separate), HotkeyPlan.For(document, useCatalogDefault: false));
        Assert.Equal(HotkeyPlan.For(All, RecordHotkeyMode.Separate), HotkeyPlan.For(document, useCatalogDefault: true));
    }
}
