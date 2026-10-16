using WebDavSync.Core.Config;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Safety guards (§9) and the approval gate (§10).</summary>
public sealed class GuardsAndApprovalTests
{
    private static World ManySynced(int count)
    {
        var world = new World();
        for (var i = 0; i < count; i++)
        {
            world.Synced($"f{i:000}.txt", $"content {i}");
        }

        return world;
    }

    [Fact]
    public void Mass_delete_guard_trips_above_the_count()
    {
        var world = ManySynced(2000);
        for (var i = 0; i < 51; i++)
        {
            world.RemoveLocal($"f{i:000}.txt");
        }

        var report = SafetyGuards.Evaluate(world.Plan(), 2000, new PairSettings(), null, null);
        Assert.Single(report.Trips);
        Assert.Contains("MASS DELETE: 51 remote deletes", report.Trips[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Mass_delete_guard_trips_above_the_percentage()
    {
        var world = ManySynced(100);
        for (var i = 0; i < 6; i++)
        {
            world.RemoveRemote($"f{i:000}.txt");
        }

        var report = SafetyGuards.Evaluate(world.Plan(), 100, new PairSettings(), null, null);
        Assert.Contains("local deletes", Assert.Single(report.Trips), StringComparison.Ordinal);
    }

    [Fact]
    public void Small_delete_does_not_trip()
    {
        var world = ManySynced(1000).RemoveLocal("f000.txt");
        Assert.False(SafetyGuards.Evaluate(world.Plan(), 1000, new PairSettings(), null, null).RequiresApproval);
    }

    [Fact]
    public void Collapsed_folder_delete_counts_its_subtree()
    {
        var world = new World().SyncedDir("big");
        for (var i = 0; i < 60; i++)
        {
            world.Synced($"big/f{i}.txt", $"c{i}").RemoveLocal($"big/f{i}.txt");
        }

        var plan = world.RemoveLocal("big").Plan();
        Assert.Single(plan.Ops);
        Assert.True(SafetyGuards.Evaluate(plan, 10_000, new PairSettings(), null, null).RequiresApproval);
    }

    [Fact]
    public void Mass_overwrite_guard()
    {
        var world = ManySynced(1000);
        for (var i = 0; i < 51; i++)
        {
            world.Remote($"f{i:000}.txt", $"remote edit {i}", World.T1);
        }

        var report = SafetyGuards.Evaluate(world.Plan(), 1000, new PairSettings(), null, null);
        Assert.Contains("MASS OVERWRITE: 51 local overwrites", Assert.Single(report.Trips), StringComparison.Ordinal);
    }

    [Fact]
    public void Quota_and_disk_space_errors()
    {
        var plan = new World().Local("up.txt", "upload me").Remote("down.txt", "download me").Plan();
        var report = SafetyGuards.Evaluate(plan, 0, new PairSettings { MinFreeDiskSpace = 100 }, new RemoteQuota(0, 3), 105);
        Assert.Equal(2, report.Errors.Count);
        Assert.False(SafetyGuards.Evaluate(plan, 0, new PairSettings { MinFreeDiskSpace = 100 }, new RemoteQuota(0, 1000), 1000).HasErrors);
    }

    [Fact]
    public void Guarded_mode_needs_approval_only_for_unsafe_ops()
    {
        var plan = new World()
            .Local("new.txt", "n")
            .Synced("edited.txt", "e", 3).Local("edited.txt", "e2!", World.T1, 3)
            .Synced("gone.txt", "g").RemoveLocal("gone.txt")
            .Plan();
        var needing = Approval.NeedingApproval(plan, SyncMode.Guarded).Select(o => o.Key).Order().ToList();
        Assert.Equal(["edited.txt", "gone.txt"], needing);
        Assert.Empty(Approval.NeedingApproval(plan, SyncMode.Auto));
        Assert.Equal(3, Approval.NeedingApproval(plan, SyncMode.ApproveAll).Count);
    }

    [Fact]
    public void Select_stops_at_an_unapproved_early_op()
    {
        var plan = new World()
            .Synced("a.txt", "a", 7).RemoveLocal("a.txt").Local("b.txt", "a", id: 7)
            .Local("new.txt", "n")
            .Plan();
        Assert.True(plan.Ops[0].Early);
        Assert.Empty(Approval.Select(plan, op => op.Type != OpType.MoveRemote, null));
        Assert.Equal(2, Approval.Select(plan, _ => true, null).Count);
    }

    [Fact]
    public void Max_ops_counts_actions_and_drops_later_base_updates()
    {
        var plan = new World()
            .Local("a.txt", "a").Local("b.txt", "bb").Local("c.txt", "ccc")
            .Synced("touched.txt", "t", 9).Local("touched.txt", "t", World.T1, 9)
            .Plan();
        var selected = Approval.Select(plan, _ => true, 2);
        Assert.Equal(2, selected.Count(o => !o.IsBaseOnly));
        Assert.Contains(selected, o => o.Type == OpType.UpdateBase);
    }
}
