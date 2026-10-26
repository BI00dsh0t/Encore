using System.Text.Json;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Settings;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private const string GameId = @"c:\games\game.exe";

    private static readonly VideoSettings ProfileVideo = new(VideoCodec.Hevc, 2560, 1440, 120, 80_000, RateControl.Vbr);

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-settings-").FullName;

    private string SettingsPath => Path.Combine(_folder, SettingsStore.FileName);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AFirstRunIsTheEmptyDocumentAndWritesNothing()
    {
        var (store, code) = SettingsStore.Open(_folder);

        Assert.Null(code);
        Assert.Equal(0, store.Current.Revision);
        Assert.Empty(store.Current.Global);
        Assert.Empty(store.Current.Profiles);
        Assert.Equal(Keys.BufferWindow.Default, SettingsResolver.Resolve(store.Current, null).BufferWindow);
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void ACommitWritesTheFileAndAReloadReturnsTheSameDocument()
    {
        var store = Open();
        var changed = new List<SettingsDocument>();
        store.Changed += changed.Add;

        var committed = store.Update(new SettingsUpdate(0,
        [
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(120)),
            SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"Game\"")),
            SettingsOp.SetProfile(GameId, Keys.Video.Name, Keys.Video.ToJson(ProfileVideo)),
        ]));

        Assert.Equal(1, committed.Revision);
        Assert.Same(committed, store.Current);
        Assert.Same(committed, Assert.Single(changed));
        Assert.True(File.Exists(SettingsPath));

        var reloaded = Open().Current;
        Assert.Equal(Serialize(committed), Serialize(reloaded));

        var effective = SettingsResolver.Resolve(reloaded, GameId);
        Assert.Equal(ProfileVideo, effective.Video);
        Assert.Equal(120, effective.BufferWindow);
        Assert.Equal(1, effective.SettingsRevision);
    }

    [Fact]
    public void TheFileIsCamelCaseWithTheDocumentShape()
    {
        var store = Open();
        store.Update(new SettingsUpdate(0, [SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"Game\""))]));

        using var file = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var root = file.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("revision").GetInt64());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("global").ValueKind);
        var profile = root.GetProperty("profiles").GetProperty(GameId);
        Assert.Equal("Game", profile.GetProperty("name").GetString());
        Assert.False(profile.GetProperty("ignored").GetBoolean());
        Assert.False(profile.GetProperty("recordingsEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Object, profile.GetProperty("overrides").ValueKind);
    }

    [Fact]
    public void EachCommitAddsOneRevision()
    {
        var store = Open();
        for (var i = 0; i < 3; i++)
        {
            store.Update(new SettingsUpdate(i, [SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(i))]));
        }

        Assert.Equal(3, store.Current.Revision);
        Assert.Equal(3, Open().Current.Revision);
    }

    [Fact]
    public void AStaleRevisionIsAConflict()
    {
        var store = Open();
        store.Update(new SettingsUpdate(0, [SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))]));

        AssertRefused(store, ErrorCodes.SettingsConflict, new SettingsUpdate(0, [SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(2))]));
    }

    [Fact]
    public void HotkeysWithTwoActionsOnOneCombinationAreRefused()
    {
        var store = Open();
        var save = new HotkeyBinding(0x79, HotkeyModifiers.Alt);
        var duplicate = Keys.Hotkeys.Default with { Save = save, RecordStart = save with { Modifiers = save.Modifiers | HotkeyModifiers.NoRepeat } };

        AssertRefused(store, ErrorCodes.HotkeyDuplicate, Update(store, SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(duplicate))));

        var distinct = Keys.Hotkeys.Default with { Save = new HotkeyBinding(0x87, HotkeyModifiers.Alt) };
        Assert.Equal(1, store.Update(Update(store, SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(distinct)))).Revision);
    }

    [Fact]
    public void AnOverrideOfAGlobalOnlyKeyIsRefused()
    {
        var store = WithProfile();

        AssertRefused(store, ErrorCodes.KeyNotOverridable, Update(store, SettingsOp.SetProfile(GameId, Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))));
    }

    [Fact]
    public void AnUnknownKeyIsRefused()
    {
        var store = WithProfile();

        AssertRefused(store, ErrorCodes.SettingUnknownKey, Update(store, SettingsOp.SetGlobal("futureKey", Json("1"))));
        AssertRefused(store, ErrorCodes.SettingUnknownKey, Update(store, SettingsOp.SetProfile(GameId, "futureKey", Json("1"))));
        AssertRefused(store, ErrorCodes.SettingUnknownKey, Update(store, SettingsOp.SetGlobal(ProfileAttributes.Ignored, Json("true"))));
    }

    [Theory]
    [InlineData("bufferWindow", "0")]
    [InlineData("bufferWindow", "\"300\"")]
    [InlineData("video", """{"codec":"h264","width":1920,"height":1080,"fps":240,"bitrateKbps":40000,"rateControl":"cbr"}""")]
    [InlineData("startWithWindows", "1")]
    public void ABadValueIsRefused(string key, string json)
    {
        var store = Open();

        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store, SettingsOp.SetGlobal(key, Json(json))));
    }

    [Fact]
    public void ASetWithoutAValueIsRefused()
    {
        var store = Open();

        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store, new SettingsOp(SettingsScope.Global, null, Keys.BufferWindow.Name, SettingsOpKind.Set)));
    }

    [Fact]
    public void AnOverrideOnAMissingProfileIsRefused()
    {
        var store = Open();

        AssertRefused(store, ErrorCodes.ProfileNotFound, Update(store, SettingsOp.SetProfile(GameId, Keys.Video.Name, Keys.Video.ToJson(ProfileVideo))));
        AssertRefused(store, ErrorCodes.ProfileNotFound, Update(store, SettingsOp.SetProfile(GameId, ProfileAttributes.Ignored, Json("true"))));
        AssertRefused(store, ErrorCodes.ProfileNotFound, Update(store, new SettingsOp(SettingsScope.Profile, null, Keys.Video.Name, SettingsOpKind.Remove)));
    }

    [Fact]
    public void SettingANameCreatesTheProfileAndAttributesSetInPlace()
    {
        var store = Open();

        store.Update(Update(store, SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"Game\""))));
        var created = store.Current.Profiles[GameId];
        Assert.Equal("Game", created.Name);
        Assert.False(created.Ignored);
        Assert.False(created.RecordingsEnabled);
        Assert.Empty(created.Overrides);

        store.Update(Update(store,
            SettingsOp.SetProfile(GameId, ProfileAttributes.Ignored, Json("true")),
            SettingsOp.SetProfile(GameId, ProfileAttributes.RecordingsEnabled, Json("true")),
            SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"Renamed\""))));
        Assert.Equal(new GameProfile { Name = "Renamed", Ignored = true, RecordingsEnabled = true, Overrides = created.Overrides }, store.Current.Profiles[GameId]);

        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store, SettingsOp.SetProfile(GameId, ProfileAttributes.Ignored, Json("\"yes\""))));
        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store, SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"\""))));
        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store, SettingsOp.RemoveProfile(GameId, ProfileAttributes.Name)));
    }

    [Fact]
    public void RemoveDropsAValueBackToTheNextLayer()
    {
        var store = WithProfile();
        store.Update(Update(store,
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(60)),
            SettingsOp.SetProfile(GameId, Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(30))));
        Assert.Equal(30, SettingsResolver.Resolve(store.Current, GameId).BufferWindow);

        store.Update(Update(store, SettingsOp.RemoveProfile(GameId, Keys.BufferWindow.Name)));
        Assert.Equal(60, SettingsResolver.Resolve(store.Current, GameId).BufferWindow);

        store.Update(Update(store, SettingsOp.RemoveGlobal(Keys.BufferWindow.Name)));
        Assert.Equal(Keys.BufferWindow.Default, SettingsResolver.Resolve(store.Current, GameId).BufferWindow);
    }

    [Fact]
    public void AMixedOpListIsRefusedWholeWithTheFirstFailingCode()
    {
        var store = WithProfile();

        AssertRefused(store, ErrorCodes.SettingInvalidValue, Update(store,
            SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(9)),
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Json("0")),
            SettingsOp.SetProfile(GameId, Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))));
    }

    [Fact]
    public void AnEngineKilledAfterItsTempWriteReopensAtTheLastCommit()
    {
        var store = WithProfile();
        var committed = Serialize(store.Current);
        store.BeforeReplace = () => throw new SimulatedKill();

        Assert.Throws<SimulatedKill>(() => store.Update(Update(store, SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(9)))));
        Assert.True(File.Exists(SettingsPath + ".tmp"));

        var (reopened, code) = SettingsStore.Open(_folder);
        Assert.Null(code);
        Assert.Equal(committed, Serialize(reopened.Current));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void AFailedReplaceChangesNothing()
    {
        var store = WithProfile();
        var before = store.Current;
        var file = File.ReadAllText(SettingsPath);
        store.BeforeReplace = () => throw new IOException("disk gone");

        AssertRefused(store, ErrorCodes.SettingsWriteFailed, Update(store, SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(9))));

        Assert.Same(before, store.Current);
        Assert.Equal(file, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void AFailedTempWriteChangesNothing()
    {
        var store = WithProfile();
        var before = store.Current;
        var file = File.ReadAllText(SettingsPath);
        Directory.CreateDirectory(SettingsPath + ".tmp");

        AssertRefused(store, ErrorCodes.SettingsWriteFailed, Update(store, SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(9))));

        Assert.Same(before, store.Current);
        Assert.Equal(file, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion":2,"revision":3,"global":{},"profiles":{}}""")]
    [InlineData("""{"revision":3,"global":{},"profiles":{}}""")]
    [InlineData("null")]
    [InlineData("""{"schemaVersion":1,"revision":3,"global":{},"profiles":{"c:\\x.exe":null}}""")]
    public void AnUnreadableFileIsMovedAsideAndTheStoreStartsEmpty(string contents)
    {
        File.WriteAllText(SettingsPath, contents);

        var (store, code) = SettingsStore.Open(_folder);

        Assert.Equal(ErrorCodes.SettingsUnreadable, code);
        Assert.Equal(0, store.Current.Revision);
        Assert.Empty(store.Current.Global);
        Assert.False(File.Exists(SettingsPath));
        var aside = Assert.Single(Directory.GetFiles(_folder, SettingsStore.FileName + ".unreadable-*"));
        Assert.Matches(@"settings\.json\.unreadable-\d{8}-\d{6}$", aside);
        Assert.Equal(contents, File.ReadAllText(aside));

        store.Update(Update(store, SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))));
        Assert.Equal(1, Open().Current.Revision);
    }

    [Fact]
    public void UnknownAndBadStoredValuesSurviveARoundTrip()
    {
        File.WriteAllText(SettingsPath, """
            {"schemaVersion":1,"revision":4,"global":{"futureKey":{"a":1},"bufferWindow":"long"},"profiles":{}}
            """);
        var store = Open();

        Assert.Equal(Keys.BufferWindow.Default, SettingsResolver.Resolve(store.Current, null).BufferWindow);

        store.Update(Update(store, SettingsOp.SetGlobal(Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))));
        var reloaded = Open().Current;
        Assert.Equal(5, reloaded.Revision);
        Assert.Equal(1, reloaded.Global["futureKey"].GetProperty("a").GetInt32());
        Assert.Equal("\"long\"", reloaded.Global["bufferWindow"].GetRawText());
    }

    private SettingsStore Open()
    {
        var (store, code) = SettingsStore.Open(_folder);
        Assert.Null(code);
        return store;
    }

    private SettingsStore WithProfile()
    {
        var store = Open();
        store.Update(Update(store, SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Json("\"Game\""))));
        return store;
    }

    private static SettingsUpdate Update(SettingsStore store, params SettingsOp[] ops) => new(store.Current.Revision, ops);

    private void AssertRefused(SettingsStore store, string code, SettingsUpdate update)
    {
        var before = store.Current;
        var file = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        var changed = 0;
        void OnChanged(SettingsDocument _) => changed++;
        store.Changed += OnChanged;

        var error = Assert.Throws<ErrorCodeException>(() => store.Update(update));

        store.Changed -= OnChanged;
        Assert.Equal(code, error.Code);
        Assert.Same(before, store.Current);
        Assert.Equal(file, File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null);
        Assert.Equal(0, changed);
    }

    private static string Serialize(SettingsDocument document) =>
        JsonSerializer.Serialize(document, SettingsJsonContext.Default.SettingsDocument);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class SimulatedKill : Exception;
}
