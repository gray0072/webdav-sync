using System.Globalization;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Planning;

/// <summary>
/// A rejected op (§10): not proposed again while the item's state on both sides equals <see cref="State"/>.
/// </summary>
public sealed record HeldItem(string Key, OpType Type, string DisplayPath, string Reason, string State, DateTime HeldAtUtc);

/// <param name="Plan">The plan without held ops.</param>
/// <param name="Held">Ops left out: held ones and the action ops below a held folder op.</param>
/// <param name="Kept">Held items that still match an op.</param>
/// <param name="Released">Held items whose op is gone or whose item changed: they are dropped from the list.</param>
public sealed record HeldResult(Plan Plan, IReadOnlyList<SyncOp> Held, IReadOnlyList<HeldItem> Kept, IReadOnlyList<HeldItem> Released);

/// <summary>The held list (§10): pure functions over a plan and the snapshots it was made from.</summary>
public static class HeldList
{
    /// <summary>
    /// Phase-0 ops cannot be held: later ops were planned on the state after them (§6.5), so rejecting one stops the
    /// cycle and it is proposed again.
    /// </summary>
    public static bool CanHold(SyncOp op) => !op.IsBaseOnly && !op.Early;

    /// <summary>State of an item on both sides; a held op is released when it changes.</summary>
    public static string StateOf(string key, LocalSnapshot local, RemoteSnapshot remote)
    {
        var l = local.Entries.TryGetValue(key, out var le)
            ? le.IsDir ? "dir" : $"{le.Size}:{le.MtimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"
            : "-";
        var r = remote.Entries.TryGetValue(key, out var re)
            ? re.IsDir ? "dir" : $"{re.Size}:{re.ETag ?? re.MtimeUtc?.Ticks.ToString(CultureInfo.InvariantCulture)}"
            : "-";
        return $"L {l} | R {r}";
    }

    public static HeldItem Hold(SyncOp op, LocalSnapshot local, RemoteSnapshot remote, DateTime nowUtc) =>
        CanHold(op)
            ? new HeldItem(op.Key, op.Type, op.DisplayPath, op.Reason, StateOf(op.Key, local, remote), nowUtc)
            : throw new ArgumentException($"{op} cannot be held.", nameof(op));

    /// <summary>Removes held ops from the plan; ops below a held folder op are left out too (their parent is not there).</summary>
    public static HeldResult Apply(Plan plan, IReadOnlyCollection<HeldItem> held, LocalSnapshot local, RemoteSnapshot remote)
    {
        if (held.Count == 0)
        {
            return new HeldResult(plan, [], [], []);
        }

        var byOp = held.ToDictionary(h => (h.Key, h.Type));
        var kept = new List<HeldItem>();
        var matched = new HashSet<SyncOp>(ReferenceEqualityComparer.Instance);
        foreach (var op in plan.Ops.Where(CanHold))
        {
            if (byOp.TryGetValue((op.Key, op.Type), out var item) && item.State == StateOf(op.Key, local, remote))
            {
                kept.Add(item);
                matched.Add(op);
            }
        }

        var heldKeys = matched.Select(o => o.Key).ToList();
        var heldOps = new List<SyncOp>();
        var ops = new List<SyncOp>();
        foreach (var op in plan.Ops)
        {
            if (matched.Contains(op) || (CanHold(op) && heldKeys.Any(k => PathKey.IsUnder(op.Key, k))))
            {
                heldOps.Add(op);
            }
            else
            {
                ops.Add(op);
            }
        }

        var keptSet = kept.ToHashSet();
        return new HeldResult(plan with { Ops = ops }, heldOps, kept, held.Where(h => !keptSet.Contains(h)).ToList());
    }
}
