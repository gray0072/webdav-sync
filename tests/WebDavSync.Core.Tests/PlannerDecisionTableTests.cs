using WebDavSync.Core.Config;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Every cell of the file decision table (§6.2). Test ids F1–F14 are referenced from the spec.</summary>
public sealed class PlannerDecisionTableTests
{
    [Fact]
    public void F1_Unchanged_on_both_sides_plans_nothing() =>
        PlanAssert.Empty(new World().Synced("a.txt", "one").Plan());

    [Fact]
    public void F2_Remote_modified_downloads_with_overwrite()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1).Remote("a.txt", "two!", World.T1).Plan();
        var op = PlanAssert.Single(plan, OpType.Download, "a.txt");
        Assert.True(op.Overwrite);
        PlanAssert.Only(plan, (OpType.Download, "a.txt"));
    }

    [Fact]
    public void F3_Remote_deleted_deletes_local()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1).Plan();
        PlanAssert.Only(plan, (OpType.DeleteLocal, "a.txt"));
    }

    [Fact]
    public void F4_Local_modified_uploads_with_overwrite()
    {
        var plan = new World().Local("a.txt", "changed", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "one").Plan();
        Assert.True(PlanAssert.Single(plan, OpType.Upload, "a.txt").Overwrite);
        PlanAssert.Only(plan, (OpType.Upload, "a.txt"));
    }

    [Fact]
    public void F5_Modified_on_both_sides_with_different_sizes_is_a_conflict()
    {
        var plan = new World().Local("a.txt", "local edit", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "remote", World.T1).Plan();
        var op = PlanAssert.Single(plan, OpType.Conflict, "a.txt");
        Assert.Equal(ConflictKind.BothModified, op.Conflict);
        Assert.Equal(Side.Remote, op.Loser);
        Assert.Equal("a (conflict 2026-03-04 050607 cloud).txt", op.NewRemotePath);
        Assert.Equal("a (conflict 2026-03-04 050607 cloud).txt", op.NewLocalPath);
    }

    [Fact]
    public void F6_Modified_on_both_sides_with_equal_remote_hash_updates_base()
    {
        var plan = new World().Local("a.txt", "same", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "same", World.T1, withHash: true).Plan();
        PlanAssert.Only(plan, (OpType.UpdateBase, "a.txt"));
    }

    [Fact]
    public void F7_Modified_on_both_sides_same_size_without_remote_hash_compares_by_download()
    {
        var plan = new World().Local("a.txt", "AAAA", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "BBBB", World.T1).Plan();
        var op = PlanAssert.Single(plan, OpType.Compare, "a.txt");
        Assert.NotNull(op.NewLocalPath);
    }

    [Fact]
    public void F8_Local_modified_remote_deleted_uploads_edit_wins()
    {
        var plan = new World().Local("a.txt", "changed", World.T1, 1).Base("a.txt", "one", 1).Plan();
        Assert.False(PlanAssert.Single(plan, OpType.Upload, "a.txt").Overwrite);
        Assert.Contains(plan.Warnings, w => w.Contains("a.txt", StringComparison.Ordinal));
        PlanAssert.NoDeletes(plan);
    }

    [Fact]
    public void F9_Local_deleted_deletes_remote()
    {
        var plan = new World().Remote("a.txt", "one").Base("a.txt", "one").Plan();
        PlanAssert.Only(plan, (OpType.DeleteRemote, "a.txt"));
    }

    [Fact]
    public void F10_Local_deleted_remote_modified_downloads_edit_wins()
    {
        var plan = new World().Remote("a.txt", "changed", World.T1).Base("a.txt", "one").Plan();
        Assert.False(PlanAssert.Single(plan, OpType.Download, "a.txt").Overwrite);
        Assert.NotEmpty(plan.Warnings);
    }

    [Fact]
    public void F11_Deleted_on_both_sides_forgets_base() =>
        PlanAssert.Only(new World().Base("a.txt", "one").Plan(), (OpType.ForgetBase, "a.txt"));

    [Fact]
    public void F12_New_local_file_uploads() =>
        PlanAssert.Only(new World().Local("a.txt", "one").Plan(), (OpType.Upload, "a.txt"));

    [Fact]
    public void F13_New_remote_file_downloads() =>
        PlanAssert.Only(new World().Remote("a.txt", "one").Plan(), (OpType.Download, "a.txt"));

    [Fact]
    public void F14_Created_on_both_sides()
    {
        PlanAssert.Only(new World().Local("a.txt", "same").Remote("a.txt", "same", withHash: true).Plan(), (OpType.UpdateBase, "a.txt"));
        Assert.Equal(ConflictKind.BothCreated, PlanAssert.Single(new World().Local("a.txt", "local").Remote("a.txt", "remote!").Plan(), OpType.Conflict, "a.txt").Conflict);
        PlanAssert.Only(new World().Local("a.txt", "AAAA").Remote("a.txt", "BBBB").Plan(), (OpType.Compare, "a.txt"));
    }

    [Fact]
    public void Compare_is_not_used_above_the_size_limit()
    {
        var settings = World.Settings() with { MaxCompareDownloadBytes = 2 };
        PlanAssert.Only(new World().Local("a.txt", "AAAA").Remote("a.txt", "BBBB").Plan(settings), (OpType.Conflict, "a.txt"));
    }

    [Fact]
    public void Edge13_Mtime_only_local_touch_updates_base_only()
    {
        var plan = new World().Local("a.txt", "one", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "one").Plan();
        PlanAssert.Only(plan, (OpType.UpdateBase, "a.txt"));
    }

    [Fact]
    public void Remote_etag_change_with_same_hash_updates_base_only()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1).Remote("a.txt", "one", World.T1, "new-etag", withHash: true).Plan();
        PlanAssert.Only(plan, (OpType.UpdateBase, "a.txt"));
    }

    [Fact]
    public void Remote_etag_change_without_hash_counts_as_modified()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1).Remote("a.txt", "one", World.T1, "new-etag").Plan();
        PlanAssert.Only(plan, (OpType.Download, "a.txt"));
    }

    [Fact]
    public void Edge7_Zero_byte_files_are_synced()
    {
        PlanAssert.Only(new World().Local("empty.txt", string.Empty).Plan(), (OpType.Upload, "empty.txt"));
        PlanAssert.Only(new World().Local("empty.txt", string.Empty).Remote("empty.txt", string.Empty).Plan(), (OpType.Compare, "empty.txt"));
    }

    [Fact]
    public void Locked_changed_local_file_is_skipped()
    {
        var world = new World().Base("a.txt", "one", 1).Remote("a.txt", "one");
        var input = world.Input();
        var local = new Dictionary<string, Model.LocalEntry>
        {
            ["a.txt"] = new("a.txt", "a.txt", false, 99, World.T1, 1, null, Locked: true),
        };
        var plan = Planner.Create(input with { Local = new Model.LocalSnapshot(local, input.Local.ListedFolders) });
        PlanAssert.Empty(plan);
    }

    [Fact]
    public void Imported_base_is_adopted_by_size_only()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1, confirmed: false).Remote("a.txt", "one", World.T1).Plan();
        var op = PlanAssert.Single(plan, OpType.UpdateBase, "a.txt");
        Assert.Contains("imported", op.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Imported_base_with_size_and_mtime_matching_detects_mtime_change()
    {
        var settings = World.Settings() with { ImportedBaseMatch = ImportedBaseMatch.SizeAndMtime };
        var world = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1, confirmed: false);
        PlanAssert.Only(world.Remote("a.txt", "one", World.T1).Plan(settings), (OpType.Download, "a.txt"));
        PlanAssert.Only(world.Remote("a.txt", "one", World.T0).Plan(settings), (OpType.UpdateBase, "a.txt"));
    }

    [Fact]
    public void Imported_base_with_remote_hash_detects_same_size_change()
    {
        // Mail.ru: getetag is the content hash (§4.4), so the imported base is matched exactly, mtime ignored.
        var world = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1, confirmed: false);
        PlanAssert.Only(world.Remote("a.txt", "two", World.T0, withHash: true).Plan(), (OpType.Download, "a.txt"));
    }

    [Fact]
    public void Imported_base_with_remote_hash_is_adopted_despite_mtime_change()
    {
        var settings = World.Settings() with { ImportedBaseMatch = ImportedBaseMatch.SizeAndMtime };
        var world = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1, confirmed: false);
        PlanAssert.Only(world.Remote("a.txt", "one", World.T1, withHash: true).Plan(settings), (OpType.UpdateBase, "a.txt"));
    }

    [Fact]
    public void Imported_base_with_different_remote_size_downloads()
    {
        var plan = new World().Local("a.txt", "one", id: 1).Base("a.txt", "one", 1, confirmed: false).Remote("a.txt", "longer", World.T1).Plan();
        PlanAssert.Only(plan, (OpType.Download, "a.txt"));
    }
}
