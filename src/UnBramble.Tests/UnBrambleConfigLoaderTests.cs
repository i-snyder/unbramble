using UnBramble.Core.Config;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>`unbramble.json`'s `watch.autoStart` toggle (docs/architecture.md "Auto-spawn
/// watcher") — defaults to true, and both explicit true/false values round-trip.</summary>
public class UnBrambleConfigLoaderTests
{
    [Fact]
    public void NoConfigFile_WatchAutoStartDefaultsTrue()
    {
        using var dir = TempDir.Create();

        var config = UnBrambleConfigLoader.Load(dir.Root, out var warnings);

        Assert.True(config.Watch.AutoStart);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ConfigFile_WatchAutoStartExplicitFalse_IsHonored()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir.Root, "unbramble.json"), """{"watch":{"autoStart":false}}""");

        var config = UnBrambleConfigLoader.Load(dir.Root, out var warnings);

        Assert.False(config.Watch.AutoStart);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ConfigFile_WatchAutoStartExplicitTrue_IsHonored()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir.Root, "unbramble.json"), """{"watch":{"autoStart":true}}""");

        var config = UnBrambleConfigLoader.Load(dir.Root, out var warnings);

        Assert.True(config.Watch.AutoStart);
    }

    [Fact]
    public void ConfigFile_WatchKeyPresentButNoAutoStart_DefaultsTrue()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir.Root, "unbramble.json"), """{"watch":{}}""");

        var config = UnBrambleConfigLoader.Load(dir.Root, out _);

        Assert.True(config.Watch.AutoStart);
    }
}
