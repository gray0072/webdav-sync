using WebDavSync.Core.Config;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Invariants #1, #2, #4, #10 and the union mode (§7.6).</summary>
public sealed class PlannerSafetyTests
{
    [Fact]
    public void Edge24_Incomplete_remote_listing_is_never_interpreted_as_deletions()
    {
        var plan = new World()
            .SyncedDir("docs")
            .Synced("docs/a.txt", "one")
            .RemoveRemote("docs/a.txt")
            .UnlistedRemote("docs")
            .Plan();
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Edge24_Unlisted_remote_root_plans_no_deletes()
    {
        var plan = new World().Synced("a.txt", "one").Synced("b.txt", "two").RemoveRemote("a.txt").RemoveRemote("b.txt").UnlistedRemote(string.Empty).Plan();
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Missing_folder_under_an_unlisted_ancestor_is_unknown()
    {
        var plan = new World()
            .SyncedDir("a")
            .SyncedDir("a/b")
            .Synced("a/b/c.txt", "x")
            .RemoveRemote("a/b")
            .RemoveRemote("a/b/c.txt")
            .UnlistedRemote("a")
            .Plan();
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Unreadable_local_folder_is_never_interpreted_as_deletions()
    {
        var plan = new World().SyncedDir("docs").Synced("docs/a.txt", "one").RemoveLocal("docs/a.txt").UnlistedLocal("docs").Plan();
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Union_mode_never_deletes_and_restores_instead()
    {
        var world = new World()
            .Local("only-base-local.txt", "a", id: 1).Base("only-base-local.txt", "a", 1)
            .Remote("only-base-remote.txt", "b").Base("only-base-remote.txt", "b")
            .SyncedDir("dir").RemoveRemote("dir");
        var plan = world.Plan(World.Settings(union: true));
        PlanAssert.NoDeletes(plan);
        PlanAssert.Only(plan,
            (OpType.Upload, "only-base-local.txt"),
            (OpType.Download, "only-base-remote.txt"),
            (OpType.MkdirRemote, "dir"));
    }

    [Fact]
    public void Scenario_B_union_merge_of_two_non_empty_trees()
    {
        var plan = new World()
            .Local("same.txt", "same").Remote("same.txt", "same", withHash: true)
            .Local("diff.txt", "local version").Remote("diff.txt", "remote")
            .Local("local-only.txt", "l")
            .Remote("remote-only.txt", "r")
            .Plan(World.Settings(union: true));
        PlanAssert.Only(plan,
            (OpType.UpdateBase, "same.txt"),
            (OpType.Conflict, "diff.txt"),
            (OpType.Upload, "local-only.txt"),
            (OpType.Download, "remote-only.txt"));
    }

    [Fact]
    public void Edge10_Excluded_folder_is_neither_synced_nor_deleted()
    {
        var settings = World.Settings() with { Filter = new SyncFilter(["Photos"]) };
        var plan = new World()
            .Synced("keep.txt", "k")
            .BaseDir("Photos").Base("Photos/a.jpg", "jpg")
            .RemoteDir("Photos").Remote("Photos/a.jpg", "jpg")
            .Plan(settings);
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Edge2_Remote_folder_with_unsyncable_items_is_not_deleted_recursively()
    {
        var plan = new World()
            .BaseDir("docs").RemoteDir("docs")
            .Base("docs/a.txt", "a").Remote("docs/a.txt", "a")
            .SkippedRemote("docs/bad:name.txt")
            .Plan();
        PlanAssert.Only(plan, (OpType.DeleteRemote, "docs/a.txt"));
        Assert.Contains(plan.Warnings, w => w.Contains("docs", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_folder_with_excluded_items_is_not_deleted_recursively()
    {
        var plan = new World()
            .LocalDir("docs", 5).BaseDir("docs", 5)
            .Local("docs/a.txt", "a", id: 6).Base("docs/a.txt", "a", 6)
            .SkippedLocal("docs/link")
            .Plan();
        PlanAssert.Only(plan, (OpType.DeleteLocal, "docs/a.txt"));
    }
}
