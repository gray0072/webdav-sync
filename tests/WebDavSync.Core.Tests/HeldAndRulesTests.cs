using System.Text.Json;
using WebDavSync.Core.Config;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Held list and "approve and remember" rules (§10).</summary>
public sealed class HeldAndRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Held_op_is_left_out_while_the_item_is_unchanged()
    {
        var world = new World().Remote("new.txt", "n").Local("up.txt", "u");
        var input = world.Input();
        var plan = Planner.Create(input);
        var held = HeldList.Hold(PlanAssert.Single(plan, OpType.Download, "new.txt"), input.Local, input.Remote, Now);

        var result = HeldList.Apply(Planner.Create(world.Input()), [held], input.Local, input.Remote);

        PlanAssert.Only(result.Plan, (OpType.Upload, "up.txt"));
        Assert.Equal("new.txt", Assert.Single(result.Held).Key);
        Assert.Equal([held], result.Kept);
        Assert.Empty(result.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Held_op_is_released_when_the_item_changes_on_either_side(bool changeLocal)
    {
        var before = new World().Synced("doc.txt", "v1", 5).Local("doc.txt", "v2 local", World.T1, 5).Input();
        var held = HeldList.Hold(PlanAssert.Single(Planner.Create(before), OpType.Upload, "doc.txt"), before.Local, before.Remote, Now);

        var world = new World().Synced("doc.txt", "v1", 5);
        world = changeLocal
            ? world.Local("doc.txt", "v3 local, longer", World.T1.AddMinutes(1), 5)
            : world.Local("doc.txt", "v2 local", World.T1, 5).Remote("doc.txt", "v2 remote", World.T1);
        var after = world.Input();
        var result = HeldList.Apply(Planner.Create(after), [held], after.Local, after.Remote);

        Assert.Empty(result.Held);
        Assert.Equal([held], result.Released);
        Assert.Contains(result.Plan.Ops, o => o.Key == "doc.txt");
    }

    [Fact]
    public void Held_item_without_an_op_is_released()
    {
        var before = new World().Remote("new.txt", "n").Input();
        var held = HeldList.Hold(Planner.Create(before).Ops.Single(), before.Local, before.Remote, Now);
        var after = new World().Input();

        var result = HeldList.Apply(Planner.Create(after), [held], after.Local, after.Remote);

        Assert.Equal([held], result.Released);
    }

    [Fact]
    public void Ops_below_a_held_folder_op_are_left_out_too()
    {
        var input = new World().RemoteDir("Photos").Remote("Photos/a.jpg", "a").Remote("Photos/b.jpg", "b").Remote("other.txt", "o").Input();
        var plan = Planner.Create(input);
        var held = HeldList.Hold(PlanAssert.Single(plan, OpType.MkdirLocal, "Photos"), input.Local, input.Remote, Now);

        var result = HeldList.Apply(plan, [held], input.Local, input.Remote);

        PlanAssert.Only(result.Plan, (OpType.Download, "other.txt"));
        Assert.Equal(3, result.Held.Count);
    }

    [Fact]
    public void Moves_cannot_be_held()
    {
        var input = new World().Synced("a.txt", "a", 7).RemoveLocal("a.txt").Local("b.txt", "a", id: 7).Input();
        var move = Planner.Create(input).Ops[0];

        Assert.True(move.Early);
        Assert.False(HeldList.CanHold(move));
        Assert.Throws<ArgumentException>(() => HeldList.Hold(move, input.Local, input.Remote, Now));
    }

    [Fact]
    public void Rule_approves_its_type_inside_its_folder_only()
    {
        var plan = new World()
            .RemoteDir("Camera Uploads").Remote("Camera Uploads/1.jpg", "1").Remote("Camera Uploads/Sub/2.jpg", "2")
            .Remote("Docs/3.txt", "3")
            .Local("Camera Uploads/mine.jpg", "m")
            .Plan();
        var rules = new List<AutoApproveRule> { new() { Type = OpType.Download, Folder = "camera uploads" } };

        var needing = Approval.NeedingApproval(plan, SyncMode.ApproveAll, unionMode: false, rules);

        Assert.DoesNotContain(needing, o => o.Type == OpType.Download && o.Key.StartsWith("camera uploads/", StringComparison.Ordinal));
        Assert.Contains(needing, o => o.Type == OpType.Download && o.Key == "docs/3.txt");
        Assert.Contains(needing, o => o.Type == OpType.Upload);
    }

    [Fact]
    public void Rule_covers_overwrites_only_when_asked()
    {
        var plan = new World().Synced("doc.txt", "v1").Remote("doc.txt", "v2", World.T1).Plan();
        var download = PlanAssert.Single(plan, OpType.Download, "doc.txt");
        Assert.True(download.Overwrite);

        Assert.False(new AutoApproveRule { Type = OpType.Download }.Matches(download));
        Assert.True(new AutoApproveRule { Type = OpType.Download, Overwrite = true }.Matches(download));
    }

    [Fact]
    public void Rule_does_not_cover_a_move_out_of_its_folder()
    {
        var plan = new World().Synced("In/a.txt", "a", 7).SyncedDir("In").RemoveLocal("In/a.txt").Local("Out/a.txt", "a", id: 7).LocalDir("Out").Plan();
        var move = PlanAssert.Single(plan, OpType.MoveRemote, "In/a.txt");

        Assert.False(new AutoApproveRule { Type = OpType.MoveRemote, Folder = "In" }.Matches(move));
        Assert.True(new AutoApproveRule { Type = OpType.MoveRemote }.Matches(move));
    }

    [Fact]
    public void Union_mode_ignores_rules()
    {
        var plan = new World().Remote("a.txt", "a").Plan(World.Settings(union: true));
        var rules = new List<AutoApproveRule> { new() { Type = OpType.Download } };

        Assert.Single(Approval.NeedingApproval(plan, SyncMode.Auto, unionMode: true, rules));
        Assert.Empty(Approval.NeedingApproval(plan, SyncMode.ApproveAll, unionMode: false, rules));
    }

    [Fact]
    public void Rules_round_trip_through_settings_json()
    {
        var settings = new AppSettings
        {
            Pairs = [new PairSettings { Name = "p", AutoApprove = [new AutoApproveRule { Type = OpType.Download, Folder = "Camera Uploads" }] }],
        };

        var json = JsonSerializer.Serialize(settings, AppSettings.JsonOptions);
        var rule = Assert.Single(JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!.Pairs[0].AutoApprove);

        Assert.Contains("\"type\": \"Download\"", json, StringComparison.Ordinal);
        Assert.Equal(OpType.Download, rule.Type);
        Assert.Equal("Camera Uploads", rule.Folder);
    }
}
