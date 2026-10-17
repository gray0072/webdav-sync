using System.Diagnostics;
using WebDavSync.Core.Config;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Content changes that keep size and mtime, the rolling verify (§5.3) and the hashing rate limit (§11).</summary>
public sealed class VerifyTests
{
    [Fact]
    public void Same_size_and_mtime_with_another_hash_is_a_change_that_needs_review_in_every_mode()
    {
        // The file was re-hashed (new FileId): same size and time, other content.
        var plan = new World()
            .Synced("db.bin", "content A", 5)
            .Local("db.bin", "content B", World.T0, id: 99)
            .Plan();

        var upload = PlanAssert.Single(plan, OpType.Upload, "db.bin");
        Assert.True(upload.NeedsReview);
        Assert.False(upload.IsSafe);
        Assert.Contains("size and time unchanged", upload.Reason, StringComparison.Ordinal);
        var rules = new List<AutoApproveRule> { new() { Type = OpType.Upload, Overwrite = true } };
        Assert.Single(Approval.NeedingApproval(plan, SyncMode.Auto, unionMode: false, rules));
    }

    [Fact]
    public void Same_size_mtime_and_hash_stays_unchanged()
    {
        var plan = new World().Synced("a.txt", "same", 5).Local("a.txt", "same", World.T0, id: 99).Plan();
        Assert.DoesNotContain(plan.Ops, o => !o.IsBaseOnly);
    }

    [Fact]
    public void Slices_follow_the_cursor_wrap_around_and_respect_the_budget()
    {
        var files = Enumerable.Range(0, 6).Select(i => File($"f{i}", 100)).ToList();

        Assert.Equal(["f0", "f1"], Keys(RollingVerify.Slice(files, null, 250)));
        Assert.Equal(["f2", "f3", "f4"], Keys(RollingVerify.Slice(files, "f1", 300)));
        Assert.Equal(["f5", "f0"], Keys(RollingVerify.Slice(files, "f4", 200)));
        Assert.Empty(RollingVerify.Slice(files, "f4", 50));
        Assert.Equal(6, RollingVerify.Slice(files, "f2", 10_000).Count);
    }

    [Fact]
    public void Budget_spreads_all_bytes_over_the_interval_and_caps_one_slice()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1000, RollingVerify.Budget(30_000, t0, t0.AddDays(1), TimeSpan.FromDays(30)), 3);
        Assert.Equal(RollingVerify.MaxSliceBytes, RollingVerify.Budget(long.MaxValue / 2, t0, t0.AddDays(1), TimeSpan.FromDays(30)));
        Assert.Equal(0, RollingVerify.Budget(30_000, t0, t0, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void Huge_file_is_verified_alone_once_the_budget_reaches_the_slice_limit()
    {
        var files = new[] { File("big", 50L * 1024 * 1024 * 1024), File("small", 10) };
        Assert.Empty(RollingVerify.Slice(files, null, 1024));
        Assert.Equal(["big"], Keys(RollingVerify.Slice(files, null, RollingVerify.MaxSliceBytes)));
    }

    [Fact]
    public async Task Rate_limiter_slows_hashing_down()
    {
        var data = new byte[2 * 1024 * 1024];
        var watch = Stopwatch.StartNew();
        await ContentHash.ComputeAsync(new MemoryStream(data), TestContext.Current.CancellationToken, new ByteRateLimiter(10 * 1024 * 1024));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150), $"took {watch.Elapsed}");
    }

    private static BaseEntry File(string key, long size) =>
        new(key, false, key, key, 1, size, World.T0, World.Hash(key), "e", size, World.T0, World.Hash(key), true, World.T0);

    private static List<string> Keys(IEnumerable<BaseEntry> entries) => entries.Select(e => e.Key).ToList();
}
