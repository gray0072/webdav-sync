using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>One-off "keep local / keep remote" choices (§6.4, CLI <c>--keep-local</c> / <c>--keep-remote</c>).</summary>
public sealed class PlannerKeepSideTests
{
    private static PlannerSettings Keep(string key, Side side) =>
        World.Settings() with { KeepSide = new Dictionary<string, Side> { [key] = side } };

    [Fact]
    public void Keep_local_on_both_modified_overwrites_remote_instead_of_a_conflict()
    {
        var world = new World().Local("a.txt", "local edit", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "remote edit!", World.T1, withHash: true);
        var plan = world.Plan(Keep("a.txt", Side.Local));
        var op = PlanAssert.Single(plan, OpType.Upload, "a.txt");
        Assert.True(op.Overwrite);
        Assert.Contains("keep local", op.Reason, StringComparison.Ordinal);
        PlanAssert.Only(plan, (OpType.Upload, "a.txt"));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Keep_local_on_remote_modified_uploads_instead_of_downloading()
    {
        // Same size, different content: a remote change the user knows to be broken.
        var world = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1).Remote("a.txt", "two", World.T1, withHash: true);
        PlanAssert.Only(world.Plan(), (OpType.Download, "a.txt"));
        var op = PlanAssert.Single(world.Plan(Keep("a.txt", Side.Local)), OpType.Upload, "a.txt");
        Assert.True(op.Overwrite);
    }

    [Fact]
    public void Keep_remote_on_local_modified_downloads_instead_of_uploading()
    {
        var world = new World().Local("a.txt", "changed", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "one");
        var plan = world.Plan(Keep("a.txt", Side.Remote));
        Assert.True(PlanAssert.Single(plan, OpType.Download, "a.txt").Overwrite);
        PlanAssert.Only(plan, (OpType.Download, "a.txt"));
    }

    [Fact]
    public void Keep_local_on_created_on_both_sides_overwrites_remote()
    {
        var world = new World().Local("a.txt", "local", id: 1).Remote("a.txt", "remote!");
        PlanAssert.Only(world.Plan(Keep("a.txt", Side.Local)), (OpType.Upload, "a.txt"));
    }

    [Fact]
    public void Keep_on_equal_content_is_ignored_with_a_warning()
    {
        var plan = new World().Synced("a.txt", "one").Plan(Keep("a.txt", Side.Local));
        PlanAssert.Empty(plan);
        Assert.Contains(plan.Warnings, w => w.Contains("'a.txt'", StringComparison.Ordinal));
    }

    [Fact]
    public void Keep_on_a_missing_file_is_ignored_with_a_warning()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Plan(Keep("b.txt", Side.Remote));
        PlanAssert.Only(plan, (OpType.Upload, "a.txt"));
        Assert.Contains(plan.Warnings, w => w.Contains("'b.txt'", StringComparison.Ordinal));
    }
}
