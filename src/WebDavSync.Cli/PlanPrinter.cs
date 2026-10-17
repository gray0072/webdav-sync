using WebDavSync.Core.Engine;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;

namespace WebDavSync.Cli;

/// <summary>Prints a plan grouped by op type with reasons (§10).</summary>
internal static class PlanPrinter
{
    private const int DefaultLimit = 25;

    public static void Print(CyclePlan cycle, bool full)
    {
        var plan = cycle.Plan;
        Console.WriteLine($"Local:  {Describe(cycle.Local)}");
        Console.WriteLine($"Remote: {Describe(cycle.Remote)}");
        Console.WriteLine($"Base:   {cycle.TrackedItems} tracked items{(cycle.UnionMode ? " — initial sync (union mode, no deletes)" : string.Empty)}");
        foreach (var note in cycle.Notes)
        {
            Console.WriteLine("Note:   " + note);
        }

        Commands.PrintWarnings("Local scan warnings", cycle.Local.Warnings, 10);
        Commands.PrintWarnings("Remote crawl warnings", cycle.Remote.Warnings, 10);

        var groups = plan.Ops.GroupBy(o => o.Type).OrderBy(g => g.Key).ToList();
        if (groups.Count == 0)
        {
            Console.WriteLine("Plan: nothing to do.");
        }
        else
        {
            Console.WriteLine("Plan: " + string.Join(", ", groups.Select(g => $"{g.Count()} {g.Key}" + (g.Sum(o => o.Bytes) is var b and > 0 ? $" ({SafetyGuards.Size(b)})" : string.Empty))));
        }

        if (cycle.UnionMode)
        {
            var identical = plan.Count(OpType.UpdateBase);
            Console.WriteLine($"Summary: {plan.Count(OpType.Download)} to download, {plan.Count(OpType.Upload)} to upload, {identical} identical, {plan.Count(OpType.Compare)} to compare, {plan.Count(OpType.Conflict)} conflicts");
        }

        foreach (var group in groups)
        {
            if (group.Key == OpType.UpdateBase && !full)
            {
                continue;
            }

            Console.WriteLine($"-- {group.Key} ({group.Count()}) --");
            var limit = full ? int.MaxValue : DefaultLimit;
            foreach (var op in group.Take(limit))
            {
                var size = op.Bytes > 0 ? SafetyGuards.Size(op.Bytes) : op.Affected > 1 ? $"{op.Affected} items" : string.Empty;
                var extra = op.Type is OpType.Conflict or OpType.Compare ? $" -> copy '{(op.Loser == Side.Local ? op.NewLocalPath : op.NewRemotePath)}'" : string.Empty;
                Console.WriteLine($"   {op.DisplayPath}{(op.IsDir ? "/" : string.Empty)}  {size}  [{op.Reason}]{extra}");
            }

            if (group.Count() > limit)
            {
                Console.WriteLine($"   ... and {group.Count() - limit} more (use --full)");
            }
        }

        if (cycle.Held.Count > 0)
        {
            Console.WriteLine($"-- Held ({cycle.Held.Count}): not proposed until the item changes; 'wds held --release' --");
            var limit = full ? int.MaxValue : DefaultLimit;
            foreach (var op in cycle.Held.Take(limit))
            {
                Console.WriteLine($"   {op.Type} {op.DisplayPath}{(op.IsDir ? "/" : string.Empty)}  [{op.Reason}]");
            }

            if (cycle.Held.Count > limit)
            {
                Console.WriteLine($"   ... and {cycle.Held.Count - limit} more (use --full)");
            }
        }

        Commands.PrintWarnings("Plan warnings", plan.Warnings, full ? int.MaxValue : 20);
        foreach (var trip in cycle.Guards.Trips)
        {
            Console.WriteLine("GUARD: " + trip);
        }

        foreach (var error in cycle.Guards.Errors)
        {
            Console.WriteLine("GUARD ERROR: " + error);
        }
    }

    private static string Describe<T>(Snapshot<T> snapshot)
        where T : ISnapshotEntry
    {
        var files = snapshot.Entries.Values.Count(e => !e.IsDir);
        var folders = snapshot.Entries.Count - files;
        var incomplete = snapshot.Entries.Values.Count(e => e.IsDir && !snapshot.ListedFolders.Contains(e.Key)) + (snapshot.ListedFolders.Contains(string.Empty) ? 0 : 1);
        return $"{files} files, {folders} folders, {snapshot.SkippedKeys.Count} not synced, " + (incomplete == 0 ? "complete" : $"{incomplete} folders NOT listed completely");
    }
}
