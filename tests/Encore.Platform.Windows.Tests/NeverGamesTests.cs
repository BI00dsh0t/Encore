namespace Encore.Platform.Windows.Tests;

public sealed class NeverGamesTests
{
    [Fact]
    public void WindowsOwnProgramsAndListedAppsAreNeverGamesButOthersMayBe()
    {
        const string windows = @"c:\windows\";

        Assert.True(NeverGames.Contains(@"c:\windows\systemapps\microsoft.lockapp_cw5n1h2txyewy\lockapp.exe", windows));
        Assert.True(NeverGames.Contains(@"c:\windows\system32\mstsc.exe", windows));
        Assert.True(NeverGames.Contains(
            @"c:\program files\windowsapps\microsoftcorporationii.windows365_2.0.1489.0_x64__8wekyb3d8bbwe\msrdc\msrdc.exe", windows));
        Assert.True(NeverGames.Contains(@"d:\steamlibrary\steamapps\common\lossless scaling\losslessscaling.exe", windows));
        Assert.False(NeverGames.Contains(@"d:\steamlibrary\steamapps\common\rocketleague\binaries\win64\rocketleague.exe", windows));
        Assert.False(NeverGames.Contains(@"c:\windowsgames\game.exe", windows));
    }

    [Fact]
    public void AStoreAppsNewVersionIsTheSameProgramAndAnotherProgramIsNot()
    {
        const string old = @"c:\program files\windowsapps\microsoftcorporationii.windows365_2.0.1485.0_x64__8wekyb3d8bbwe\msrdc\msrdc.exe";
        const string updated = @"C:\Program Files\WindowsApps\MicrosoftCorporationII.Windows365_2.0.1489.0_x64__8wekyb3d8bbwe\msrdc\msrdc.exe";
        const string other = @"c:\program files\windowsapps\microsoftcorporationii.windows365_2.0.1489.0_x64__8wekyb3d8bbwe\windows365.exe";

        Assert.Equal(
            @"c:\program files\windowsapps\microsoftcorporationii.windows365__8wekyb3d8bbwe\msrdc\msrdc.exe",
            StorePackages.Key(old));
        Assert.True(StorePackages.SameProgram(old, updated));
        Assert.False(StorePackages.SameProgram(old, other));
        Assert.Null(StorePackages.Key(@"c:\xboxgames\gravhounds\content\game.exe"));
    }
}
