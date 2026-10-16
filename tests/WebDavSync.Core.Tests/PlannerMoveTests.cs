using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Moves and renames (§6.3, move rules M1–M10) and name edge cases (§13 #1, #5, #15, #16, #21).</summary>
public sealed class PlannerMoveTests
{
    [Fact]
    public void M1_Local_rename_is_one_remote_move()
    {
        var plan = new World().Synced("a.txt", "one", 7).RemoveLocal("a.txt").Local("b.txt", "one", id: 7).Plan();
        var op = PlanAssert.Single(plan, OpType.MoveRemote, "a.txt");
        Assert.Equal("b.txt", op.NewRemotePath);
        PlanAssert.Only(plan, (OpType.MoveRemote, "a.txt"));
    }

    [Fact]
    public void M2_Edge16_Local_folder_move_is_one_remote_move()
    {
        var world = new World().SyncedDir("old", 10);
        for (var i = 0; i < 50; i++)
        {
            world.Synced($"old/f{i}.txt", $"content {i}", (ulong)(100 + i));
        }

        world.RemoveLocal("old").LocalDir("new", 10);
        for (var i = 0; i < 50; i++)
        {
            world.RemoveLocal($"old/f{i}.txt").Local($"new/f{i}.txt", $"content {i}", id: (ulong)(100 + i));
        }

        var plan = world.Plan();
        PlanAssert.Only(plan, (OpType.MoveRemote, "old"));
    }

    [Fact]
    public void M3_Local_move_and_modify_is_move_then_upload()
    {
        var plan = new World().Synced("a.txt", "one", 7).RemoveLocal("a.txt").Local("dir/a.txt", "changed", World.T1, 7).LocalDir("dir", 8).RemoteDir("dir").BaseDir("dir", 8).Plan();
        PlanAssert.Only(plan, (OpType.MoveRemote, "a.txt"), (OpType.Upload, "dir/a.txt"));
        Assert.True(PlanAssert.Single(plan, OpType.Upload, "dir/a.txt").Overwrite);
    }

    [Fact]
    public void M3b_Reused_file_id_with_other_name_and_content_is_not_a_move()
    {
        var plan = new World().Synced("deleted.txt", "old", 7).RemoveLocal("deleted.txt").Local("created.txt", "new content", World.T1, 7).Plan();
        PlanAssert.Only(plan, (OpType.Upload, "created.txt"), (OpType.DeleteRemote, "deleted.txt"));
    }

    [Fact]
    public void M4_Local_move_when_remote_source_is_gone_falls_back_to_upload()
    {
        var plan = new World().Local("b.txt", "one", id: 7).Base("a.txt", "one", 7).Plan();
        PlanAssert.Only(plan, (OpType.Upload, "b.txt"), (OpType.ForgetBase, "a.txt"));
    }

    [Fact]
    public void M5_Local_move_into_a_new_folder_creates_the_parent_first()
    {
        var plan = new World().Synced("a.txt", "one", 7).RemoveLocal("a.txt").LocalDir("new").Local("new/a.txt", "one", id: 7).Plan();
        Assert.Equal(OpType.MkdirRemote, plan.Ops[0].Type);
        Assert.Equal(OpType.MoveRemote, plan.Ops[1].Type);
        Assert.True(plan.Ops[0].Early && plan.Ops[1].Early);
        PlanAssert.Only(plan, (OpType.MkdirRemote, "new"), (OpType.MoveRemote, "a.txt"));
    }

    [Fact]
    public void M6_Remote_move_to_another_folder_is_one_local_move()
    {
        var plan = new World()
            .SyncedDir("a").SyncedDir("b")
            .Synced("a/doc.txt", "content")
            .RemoveRemote("a/doc.txt").Remote("b/doc.txt", "content")
            .Plan();
        var op = PlanAssert.Single(plan, OpType.MoveLocal, "a/doc.txt");
        Assert.Equal("b/doc.txt", op.NewLocalPath);
        PlanAssert.Only(plan, (OpType.MoveLocal, "a/doc.txt"));
    }

    [Fact]
    public void M7_Remote_rename_in_the_same_folder_is_one_local_move()
    {
        var plan = new World().Synced("a.txt", "content").RemoveRemote("a.txt").Remote("b.txt", "content", etag: "moved-etag").Plan();

        // The server assigned a new etag on MOVE: the base adopts it, nothing is downloaded.
        PlanAssert.Only(plan, (OpType.MoveLocal, "a.txt"), (OpType.UpdateBase, "b.txt"));
    }

    [Fact]
    public void M8_Edge15_Ambiguous_remote_move_candidates_are_not_guessed()
    {
        var plan = new World()
            .Synced("a.txt", "same")
            .RemoveRemote("a.txt")
            .Remote("b.txt", "same", etag: "x1")
            .Remote("c.txt", "same", etag: "x2")
            .Plan();
        Assert.DoesNotContain(plan.Ops, o => o.Type == OpType.MoveLocal);
        Assert.Contains(plan.Warnings, w => w.Contains("Ambiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void M9_Edge15_Same_name_candidate_wins_over_same_parent()
    {
        var plan = new World()
            .SyncedDir("x").SyncedDir("y")
            .Synced("x/a.txt", "same")
            .RemoveRemote("x/a.txt")
            .Remote("y/a.txt", "same", etag: "x1")
            .Remote("x/other.txt", "same", etag: "x2")
            .Plan();
        Assert.Equal("y/a.txt", PlanAssert.Single(plan, OpType.MoveLocal, "x/a.txt").NewLocalPath);
    }

    [Fact]
    public void M10_Remote_folder_rename_is_one_local_move()
    {
        var world = new World().SyncedDir("Old");
        for (var i = 0; i < 10; i++)
        {
            world.Synced($"Old/f{i}.txt", $"c{i}").RemoveRemote($"Old/f{i}.txt").Remote($"New/f{i}.txt", $"c{i}", etag: $"new{i}");
        }

        var plan = world.RemoveRemote("Old").RemoteDir("New").Plan();
        Assert.Equal([OpType.MoveLocal], plan.ActionOps.Select(o => o.Type));
        Assert.Equal("New", PlanAssert.Single(plan, OpType.MoveLocal, "old").NewLocalPath);
        Assert.Equal(10, plan.Count(OpType.UpdateBase));
    }

    [Fact]
    public void Edge21_Hard_links_sharing_a_file_id_are_not_a_move()
    {
        var plan = new World().Synced("a.txt", "one", 7).Local("link.txt", "one", id: 7).Plan();
        PlanAssert.Only(plan, (OpType.Upload, "link.txt"));
    }

    [Fact]
    public void Edge1_Local_case_only_rename_renames_remotely()
    {
        var plan = new World().Synced("readme.txt", "x", 3).RemoveLocal("readme.txt").Local("README.txt", "x", id: 3).Plan();
        var op = PlanAssert.Single(plan, OpType.MoveRemote, "readme.txt");
        Assert.Equal("readme.txt", op.NewKey);
        Assert.Equal("README.txt", op.NewRemotePath);
        PlanAssert.Only(plan, (OpType.MoveRemote, "readme.txt"));
    }

    [Fact]
    public void Edge1_Remote_case_only_folder_rename_renames_locally_once()
    {
        var plan = new World()
            .SyncedDir("docs").Synced("docs/a.txt", "a")
            .RemoveRemote("docs").RemoveRemote("docs/a.txt")
            .RemoteDir("Docs").Remote("Docs/a.txt", "a")
            .Plan();
        PlanAssert.Only(plan, (OpType.MoveLocal, "docs"));
        Assert.Equal("Docs", PlanAssert.Single(plan, OpType.MoveLocal, "docs").NewLocalPath);
    }

    [Fact]
    public void Edge5_Unicode_normalization_difference_is_not_a_rename()
    {
        const string nfc = "café.txt";
        const string nfd = "café.txt";
        var plan = new World().Local(nfd, "x", id: 1).Base(nfc, "x", 1).Remote(nfc, "x").Plan();
        Assert.DoesNotContain(plan.Ops, o => o.Type is OpType.MoveLocal or OpType.MoveRemote);
        PlanAssert.Only(plan, (OpType.UpdateBase, nfc));
    }

    [Fact]
    public void Conflict_with_remote_winner_renames_the_local_version()
    {
        var plan = new World().Local("a.txt", "local edit", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "remote", World.T1)
            .Plan(World.Settings(winner: Config.ConflictWinner.Remote));
        var op = PlanAssert.Single(plan, OpType.Conflict, "a.txt");
        Assert.Equal(Side.Local, op.Loser);
        Assert.Equal("a (conflict 2026-03-04 050607 PC).txt", op.NewLocalPath);
    }

    [Fact]
    public void Conflict_names_are_unique()
    {
        var plan = new World()
            .Local("a.txt", "local", World.T1, 1).Base("a.txt", "one", 1).Remote("a.txt", "remote!", World.T1)
            .Synced("a (conflict 2026-03-04 050607 cloud).txt", "older conflict")
            .Plan();
        Assert.Equal("a (conflict 2026-03-04 050607 cloud 2).txt", PlanAssert.Single(plan, OpType.Conflict, "a.txt").NewRemotePath);
    }
}
