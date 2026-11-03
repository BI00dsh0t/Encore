using System.Reflection;
using System.Text.RegularExpressions;

namespace ReplayBuffer.Contracts.Tests;

public sealed partial class ErrorCodesTests
{
    [Fact]
    public void EveryCodeIsKebabCase()
    {
        Assert.All(ErrorCodes.All, code => Assert.Matches(KebabCase(), code));
    }

    [Fact]
    public void CodesAreUnique()
    {
        Assert.Equal(ErrorCodes.All.Count, ErrorCodes.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryDeclaredCodeIsInTheRegistry()
    {
        var declared = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Equal(declared.Order(), ErrorCodes.All.Order());
    }

    [Theory]
    [InlineData("window-not-found")]
    [InlineData("window-ambiguous")]
    public void TheTracerMatrixCodesAreRegistered(string code)
    {
        Assert.Contains(code, ErrorCodes.All);
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();
}

public sealed class InstanceTests
{
    private const string Roaming = @"C:\Users\u\AppData\Roaming";
    private const string Local = @"C:\Users\u\AppData\Local";

    [Fact]
    public void AnImageUnderTheInstallFolderIsTheInstalledInstance()
    {
        var instance = Instance.Resolve(@"C:\Users\u\AppData\Local\Programs\ReplayBuffer\ReplayBuffer.Engine.exe", Roaming, Local);

        Assert.Equal("ReplayBuffer", instance.Name);
        Assert.Equal(@"C:\Users\u\AppData\Roaming\ReplayBuffer", instance.RoamingDataFolder);
        Assert.Equal(@"C:\Users\u\AppData\Local\ReplayBuffer", instance.LocalDataFolder);
    }

    [Fact]
    public void TheInstallFolderMatchIgnoresCase()
    {
        var instance = Instance.Resolve(@"c:\users\U\appdata\local\programs\replaybuffer\bin\x.exe", Roaming, Local);

        Assert.Equal(Instance.InstalledName, instance.Name);
    }

    [Theory]
    [InlineData(@"C:\src\ReplayBuffer\src\ReplayBuffer.Engine\bin\Debug\ReplayBuffer.Engine.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\Programs\ReplayBuffer2\ReplayBuffer.Engine.exe")]
    public void EverythingElseIsTheDevInstance(string image)
    {
        var instance = Instance.Resolve(image, Roaming, Local);

        Assert.Equal("ReplayBuffer-Dev", instance.Name);
        Assert.Equal(@"C:\Users\u\AppData\Roaming\ReplayBuffer-Dev", instance.RoamingDataFolder);
        Assert.Equal(@"C:\Users\u\AppData\Local\ReplayBuffer-Dev", instance.LocalDataFolder);
    }

    [Theory]
    [InlineData("ReplayBuffer-Test-abc123", true)]
    [InlineData("ReplayBuffer-Dev", false)]
    public void OnlyTestPrefixedNamesAreTestInstances(string name, bool isTest)
    {
        Assert.Equal(isTest, Instance.IsTestName(name));
        if (isTest)
        {
            var instance = Instance.ForTest(name);
            Assert.True(instance.IsTest);
            Assert.Equal(name, Path.GetFileName(instance.RoamingDataFolder));
            Assert.Equal(name, Path.GetFileName(instance.LocalDataFolder));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Instance.ForTest(name));
        }
    }

    [Fact]
    public void TestRunsAreTheDevInstance()
    {
        Assert.Equal(Instance.DevName, Instance.Current.Name);
    }
}
