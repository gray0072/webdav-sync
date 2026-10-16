using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Folder rules (§6.2 folder table D1–D9) and type changes (§13 #17, #18, #20).</summary>
public sealed class PlannerFolderTests
{
    [Fact]
    public void D1_New_local_folder_is_created_remotely_before_its_files()
    {
        var plan = new World().LocalDir("docs").Local("docs/a.txt", "a").LocalDir("docs/sub").Plan();
        PlanAssert.Only(plan, (OpType.MkdirRemote, "docs"), (OpType.MkdirRemote, "docs/sub"), (OpType.Upload, "docs/a.txt"));
        var types = plan.Ops.Select(o => o.Key).ToList();
        Assert.True(types.IndexOf("docs") < types.IndexOf("docs/sub"));
        Assert.True(types.IndexOf("docs/sub") < types.IndexOf("docs/a.txt"));
    }

    [Fact]
    public void D2_New_remote_folder_is_created_locally()
    {
        var plan = new World().RemoteDir("Docs").Remote("Docs/A.txt", "a").Plan();
        PlanAssert.Only(plan, (OpType.MkdirLocal, "docs"), (OpType.Download, "docs/a.txt"));
        Assert.Equal("Docs/A.txt", PlanAssert.Single(plan, OpType.Download, "docs/a.txt").LocalPath);
    }

    [Fact]
    public void D3_Edge20_Empty_folders_are_synced()
    {
        PlanAssert.Only(new World().LocalDir("empty").Plan(), (OpType.MkdirRemote, "empty"));
        PlanAssert.Only(new World().LocalDir("empty").RemoteDir("empty").Plan(), (OpType.UpdateBase, "empty"));
    }

    [Fact]
    public void D4_Folder_deleted_locally_is_one_remote_delete()
    {
        var plan = new World()
            .RemoteDir("docs").BaseDir("docs")
            .Remote("docs/a.txt", "a").Base("docs/a.txt", "a")
            .RemoteDir("docs/sub").BaseDir("docs/sub")
            .Remote("docs/sub/b.txt", "b").Base("docs/sub/b.txt", "b")
            .Plan();
        var op = PlanAssert.Single(plan, OpType.DeleteRemote, "docs");
        Assert.Equal(4, op.Affected);
        PlanAssert.Only(plan, (OpType.DeleteRemote, "docs"));
        Assert.Equal(4, plan.DeletesOn(Side.Remote));
    }

    [Fact]
    public void D5_Edge17_Folder_deleted_locally_keeps_new_remote_items()
    {
        var plan = new World()
            .RemoteDir("docs").BaseDir("docs")
            .Remote("docs/old.txt", "old").Base("docs/old.txt", "old")
            .Remote("docs/new.txt", "added remotely")
            .Plan();
        PlanAssert.Only(plan,
            (OpType.MkdirLocal, "docs"),
            (OpType.Download, "docs/new.txt"),
            (OpType.DeleteRemote, "docs/old.txt"));
        Assert.Contains(plan.Warnings, w => w.Contains("re-created", StringComparison.Ordinal));
    }

    [Fact]
    public void D6_Edge17_Folder_deleted_remotely_keeps_modified_local_items()
    {
        var plan = new World()
            .LocalDir("docs", 1).BaseDir("docs", 1)
            .Local("docs/old.txt", "old", id: 2).Base("docs/old.txt", "old", 2)
            .Local("docs/edited.txt", "edited!", World.T1, 3).Base("docs/edited.txt", "orig", 3)
            .Plan();
        PlanAssert.Only(plan,
            (OpType.MkdirRemote, "docs"),
            (OpType.Upload, "docs/edited.txt"),
            (OpType.DeleteLocal, "docs/old.txt"));
    }

    [Fact]
    public void D7_Folder_deleted_on_both_sides_forgets_the_subtree_once()
    {
        var plan = new World().BaseDir("docs").Base("docs/a.txt", "a").BaseDir("docs/sub").Plan();
        PlanAssert.Only(plan, (OpType.ForgetBase, "docs"));
    }

    [Fact]
    public void D8_Edge18_Local_file_vs_remote_folder_keeps_both()
    {
        var plan = new World()
            .Local("item", "a file")
            .RemoteDir("item").Remote("item/inner.txt", "x")
            .Plan();
        var conflict = PlanAssert.Single(plan, OpType.Conflict, "item");
        Assert.Equal(ConflictKind.FileVsFolder, conflict.Conflict);
        Assert.Equal(Side.Local, conflict.Loser);
        Assert.True(conflict.Early);
        Assert.Equal("item (conflict 2026-03-04 050607 PC)", conflict.NewLocalPath);
        PlanAssert.Only(plan, (OpType.Conflict, "item"), (OpType.MkdirLocal, "item"), (OpType.Download, "item/inner.txt"));
    }

    [Fact]
    public void D9_Edge18_File_replaced_by_folder_locally_never_deletes()
    {
        var plan = new World()
            .Base("item.txt", "old file")
            .Remote("item.txt", "old file")
            .LocalDir("item.txt").Local("item.txt/new.txt", "n")
            .Plan();
        var conflict = PlanAssert.Single(plan, OpType.Conflict, "item.txt");
        Assert.Equal(Side.Remote, conflict.Loser);
        PlanAssert.NoDeletes(plan);
        PlanAssert.Only(plan, (OpType.Conflict, "item.txt"), (OpType.MkdirRemote, "item.txt"), (OpType.Upload, "item.txt/new.txt"));
    }

    [Fact]
    public void New_file_inside_remote_folder_uses_remote_folder_case()
    {
        var plan = new World().LocalDir("Docs", 1).RemoteDir("docs").BaseDir("Docs", 1, remotePath: "docs").Local("Docs/new.txt", "n").Plan();
        Assert.Equal("docs/new.txt", PlanAssert.Single(plan, OpType.Upload, "docs/new.txt").RemotePath);
    }

    [Fact]
    public void Ops_are_ordered_moves_mkdirs_transfers_deletes()
    {
        var plan = new World()
            .Synced("moved.txt", "m", 7).RemoveLocal("moved.txt").Local("renamed.txt", "m", id: 7)
            .LocalDir("new")
            .Local("big.txt", "a much larger file").Local("small.txt", "s")
            .SyncedDir("gone").Synced("gone/x.txt", "x").RemoveLocal("gone/x.txt").RemoveLocal("gone")
            .Synced("gone-file.txt", "g").RemoveLocal("gone-file.txt")
            .Plan();
        var order = plan.Ops.Select(o => o.Type).ToList();
        Assert.Equal(OpType.MoveRemote, order[0]);
        Assert.Equal(OpType.MkdirRemote, order[1]);
        var uploads = plan.Ops.Where(o => o.Type == OpType.Upload).Select(o => o.Key).ToList();
        Assert.Equal(["small.txt", "big.txt"], uploads);
        Assert.True(order.LastIndexOf(OpType.Upload) < order.IndexOf(OpType.DeleteRemote));
    }
}
