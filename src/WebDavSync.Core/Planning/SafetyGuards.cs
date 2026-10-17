using System.Globalization;
using WebDavSync.Core.Config;
using WebDavSync.Core.Remote;

namespace WebDavSync.Core.Planning;

/// <summary>
/// Result of the safety guards (§9). <see cref="Trips"/> require explicit approval in every mode, including
/// <see cref="SyncMode.Auto"/>; <see cref="Errors"/> stop the cycle.
/// </summary>
public sealed record GuardReport(IReadOnlyList<string> Trips, IReadOnlyList<string> Errors)
{
    public bool RequiresApproval => Trips.Count > 0;

    public bool HasErrors => Errors.Count > 0;
}

public static class SafetyGuards
{
    public static GuardReport Evaluate(Plan plan, int trackedItems, PairSettings settings, RemoteQuota? quota, long? freeDiskBytes)
    {
        var trips = new List<string>();
        var errors = new List<string>();

        foreach (var side in new[] { Side.Local, Side.Remote })
        {
            var where = side == Side.Local ? "local" : "remote";
            Check(plan.DeletesOn(side), $"MASS DELETE: {{0}} {where} deletes");
            Check(plan.OverwritesOn(side), $"MASS OVERWRITE: {{0}} {where} overwrites");
        }

        if (quota?.AvailableBytes is { } available && plan.UploadBytes > available)
        {
            errors.Add($"Not enough remote quota: {Size(plan.UploadBytes)} to upload, {Size(available)} available.");
        }

        if (freeDiskBytes is { } free && plan.DownloadBytes > 0 && plan.DownloadBytes + settings.MinFreeDiskSpace > free)
        {
            errors.Add($"Not enough disk space: {Size(plan.DownloadBytes)} to download, {Size(free)} free, {Size(settings.MinFreeDiskSpace)} must stay free.");
        }

        return new GuardReport(trips, errors);

        void Check(int count, string format)
        {
            if (count == 0)
            {
                return;
            }

            var percent = trackedItems > 0 ? count * 100.0 / trackedItems : 0;
            if (count > settings.MassDeleteCount || percent > settings.MassDeletePercent)
            {
                trips.Add(string.Format(CultureInfo.InvariantCulture, format, count) +
                          string.Format(CultureInfo.InvariantCulture, " ({0:0.#}% of {1} tracked items; limits {2} / {3}%).", percent, trackedItems, settings.MassDeleteCount, settings.MassDeletePercent));
            }
        }
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, unit == 0 ? "{0:0} {1}" : "{0:0.#} {1}", value, units[unit]);
    }
}

/// <summary>Approval gate rules (§10).</summary>
public static class Approval
{
    /// <summary>Action ops that need a human decision in the given mode (guard trips aside).</summary>
    public static IReadOnlyList<SyncOp> NeedingApproval(Plan plan, SyncMode mode) => mode switch
    {
        SyncMode.Auto => plan.ActionOps.Where(o => o.NeedsReview).ToList(),
        SyncMode.Guarded => plan.ActionOps.Where(o => !o.IsSafe).ToList(),
        _ => plan.ActionOps.ToList(),
    };

    /// <summary>
    /// Same, with union mode and "approve and remember" rules (§10): union-mode plans are always shown in full
    /// (§7.2), otherwise ops matched by a rule are approved.
    /// </summary>
    public static IReadOnlyList<SyncOp> NeedingApproval(Plan plan, SyncMode mode, bool unionMode, IReadOnlyList<AutoApproveRule> rules) =>
        unionMode
            ? mode == SyncMode.DryRun ? NeedingApproval(plan, mode) : plan.ActionOps.ToList()
            : NeedingApproval(plan, mode).Where(o => o.NeedsReview || !rules.Any(r => r.Matches(o))).ToList();

    /// <summary>
    /// Ops to execute, in plan order. Stops at the first early (phase-0) op that is not approved, because later
    /// ops were planned on the state after it. <paramref name="maxOps"/> counts action ops only (§10 <c>MaxOpsPerCycle</c>);
    /// base-only ops after the cut are dropped too.
    /// </summary>
    public static IReadOnlyList<SyncOp> Select(Plan plan, Func<SyncOp, bool> approved, int? maxOps)
    {
        var result = new List<SyncOp>();
        var actions = 0;
        foreach (var op in plan.Ops)
        {
            if (op.IsBaseOnly)
            {
                result.Add(op);
                continue;
            }

            if (!approved(op))
            {
                if (op.Early)
                {
                    break;
                }

                continue;
            }

            if (maxOps is { } max && actions >= max)
            {
                break;
            }

            result.Add(op);
            actions++;
        }

        return result;
    }
}
