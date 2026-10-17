using WebDavSync.Core.Config;

namespace WebDavSync.Core.Tests;

public sealed class PairSettingsEditorTests
{
    [Fact]
    public void Sets_typed_values_case_insensitively()
    {
        var pair = new PairSettings { Name = "cloud", Mode = SyncMode.DryRun };
        PairSettingsEditor.Set(pair,
        [
            "mode=approveall", "MaxOpsPerCycle=20", "conflictWinner=Newer", "massDeletePercent=2.5",
            "minFreeDiskSpace=4GB", "maxCompareDownloadBytes=512 KB", "keepOverwrittenVersions=off",
            "localDeleteTarget=StateTrash", "remoteHashProperty={DAV:}getetag", "maxRequestsPerSecond=0",
        ]);

        Assert.Equal(SyncMode.ApproveAll, pair.Mode);
        Assert.Equal(20, pair.MaxOpsPerCycle);
        Assert.Equal(ConflictWinner.Newer, pair.ConflictWinner);
        Assert.Equal(2.5, pair.MassDeletePercent);
        Assert.Equal(4L << 30, pair.MinFreeDiskSpace);
        Assert.Equal(512L << 10, pair.MaxCompareDownloadBytes);
        Assert.False(pair.KeepOverwrittenVersions);
        Assert.Equal(LocalDeleteTarget.StateTrash, pair.LocalDeleteTarget);
        Assert.Equal("{DAV:}getetag", pair.RemoteHashProperty);
        Assert.Equal(0, pair.MaxRequestsPerSecond);
    }

    [Fact]
    public void None_clears_optional_values()
    {
        var pair = new PairSettings { MaxOpsPerCycle = 20, RemoteHashProperty = "{DAV:}getetag" };
        PairSettingsEditor.Set(pair, ["maxOpsPerCycle=none", "remoteHashProperty=none"]);
        Assert.Null(pair.MaxOpsPerCycle);
        Assert.Null(pair.RemoteHashProperty);
    }

    [Theory]
    [InlineData("id=x")]
    [InlineData("localRoot=D:\\Other")]
    [InlineData("remoteUrl=https://example.org/")]
    [InlineData("remoteRoot=/Other")]
    [InlineData("excludedFolders=Photos")]
    [InlineData("excludePatterns=*.bak")]
    [InlineData("autoApprove=Download")]
    [InlineData("noSuchSetting=1")]
    [InlineData("mode=Turbo")]
    [InlineData("mode=1")]
    [InlineData("maxOpsPerCycle=0")]
    [InlineData("massDeleteCount=-1")]
    [InlineData("massDeletePercent=150")]
    [InlineData("minFreeDiskSpace=lots")]
    [InlineData("keepOverwrittenVersions=maybe")]
    [InlineData("remoteHashProperty={DAV:")]
    [InlineData("name=")]
    [InlineData("mode")]
    public void Rejects_fixed_unknown_and_invalid_settings(string assignment) =>
        Assert.Throws<ArgumentException>(() => PairSettingsEditor.Set(new PairSettings(), [assignment]));

    [Fact]
    public void Invalid_assignment_leaves_the_pair_unchanged()
    {
        var pair = new PairSettings { Mode = SyncMode.DryRun, MaxOpsPerCycle = 20 };
        Assert.Throws<ArgumentException>(() => PairSettingsEditor.Set(pair, ["mode=Auto", "maxOpsPerCycle=-5"]));
        Assert.Equal(SyncMode.DryRun, pair.Mode);
        Assert.Equal(20, pair.MaxOpsPerCycle);
    }

    [Theory]
    [InlineData("1048576", 1L << 20)]
    [InlineData("64MB", 64L << 20)]
    [InlineData("1.5 gb", 3L << 29)]
    [InlineData("100B", 100)]
    [InlineData("1TB", 1L << 40)]
    public void Parses_sizes(string text, long expected) =>
        Assert.Equal(expected, PairSettingsEditor.ParseSize(text));

    [Fact]
    public void Every_writable_setting_is_editable_fixed_or_changed_elsewhere()
    {
        var names = typeof(PairSettings).GetProperties().Where(p => p.CanWrite)
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]);
        foreach (var name in names)
        {
            Assert.True(
                PairSettingsEditor.Names.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                PairSettingsEditor.Fixed.Contains(name) || PairSettingsEditor.Elsewhere.ContainsKey(name),
                $"Setting '{name}' is not covered by PairSettingsEditor.");
        }
    }
}
