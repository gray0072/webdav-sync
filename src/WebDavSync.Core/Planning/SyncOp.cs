using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Planning;

/// <summary>Operation types (§1.3). Declaration order is not execution order — see §6.5.</summary>
public enum OpType
{
    MoveLocal,
    MoveRemote,
    MkdirLocal,
    MkdirRemote,
    Upload,
    Download,

    /// <summary>Same size, no remote hash: download to temp and compare; equal → base update, else conflict (§6.2).</summary>
    Compare,
    Conflict,
    UpdateBase,
    DeleteLocal,
    DeleteRemote,
    ForgetBase,
}

public enum ConflictKind
{
    None,
    BothModified,
    BothCreated,
    FileVsFolder,
}

public enum Side
{
    Local,
    Remote,
}

/// <summary>
/// One planned operation. Paths are relative to the pair roots. <see cref="Local"/>, <see cref="Remote"/>
/// and <see cref="Base"/> are the states the planner saw: the executor re-checks them before acting.
/// </summary>
public sealed record SyncOp
{
    public required OpType Type { get; init; }

    public required string Key { get; init; }

    public required bool IsDir { get; init; }

    /// <summary>Human-readable reason, e.g. "local modified" (§10).</summary>
    public required string Reason { get; init; }

    public string? LocalPath { get; init; }

    public string? RemotePath { get; init; }

    /// <summary>Moves: target key. Conflicts: key of the conflict copy.</summary>
    public string? NewKey { get; init; }

    public string? NewLocalPath { get; init; }

    public string? NewRemotePath { get; init; }

    public LocalEntry? Local { get; init; }

    public RemoteEntry? Remote { get; init; }

    public BaseEntry? Base { get; init; }

    /// <summary>Upload/Download replaces an existing item on the target side.</summary>
    public bool Overwrite { get; init; }

    public ConflictKind Conflict { get; init; }

    /// <summary>Conflicts: the side whose version gets the conflict name.</summary>
    public Side? Loser { get; init; }

    /// <summary>Tracked items affected; a collapsed folder delete counts its whole subtree (§9 #2).</summary>
    public int Affected { get; init; } = 1;

    /// <summary>
    /// Phase-0 op (move, type-conflict rename, parent of a move target): later ops were planned on the state after it,
    /// so if it does not run, the rest of the plan must not run either.
    /// </summary>
    public bool Early { get; init; }

    /// <summary>
    /// Needs a human in every mode, rules included (§10): the local content changed while size and mtime did not,
    /// which may be disk corruption rather than an edit.
    /// </summary>
    public bool NeedsReview { get; init; }

    public long Bytes => Type switch
    {
        OpType.Upload => Local?.Size ?? 0,
        OpType.Download or OpType.Compare => Remote?.Size ?? 0,
        OpType.Conflict => (Local?.Size ?? 0) + (Remote?.Size ?? 0),
        _ => 0,
    };

    public bool IsBaseOnly => Type is OpType.UpdateBase or OpType.ForgetBase;

    public bool IsDelete => Type is OpType.DeleteLocal or OpType.DeleteRemote;

    /// <summary>Ops that run without approval in <see cref="SyncMode.Guarded"/> (§10).</summary>
    public bool IsSafe => !NeedsReview && Type switch
    {
        OpType.MkdirLocal or OpType.MkdirRemote or OpType.UpdateBase or OpType.ForgetBase => true,
        OpType.Upload or OpType.Download => !Overwrite,
        _ => false,
    };

    public string DisplayPath => Type switch
    {
        OpType.MoveLocal => $"{LocalPath} -> {NewLocalPath}",
        OpType.MoveRemote => $"{RemotePath} -> {NewRemotePath}",
        OpType.Upload or OpType.MkdirRemote or OpType.DeleteRemote => RemotePath ?? LocalPath ?? Key,
        _ => LocalPath ?? RemotePath ?? Key,
    };

    public override string ToString() => $"{Type} {DisplayPath} ({Reason})";
}

public sealed record Plan(IReadOnlyList<SyncOp> Ops, IReadOnlyList<string> Warnings)
{
    public int Count(OpType type) => Ops.Count(o => o.Type == type);

    public IEnumerable<SyncOp> ActionOps => Ops.Where(o => !o.IsBaseOnly);

    public int DeletesOn(Side side) =>
        Ops.Where(o => o.Type == (side == Side.Local ? OpType.DeleteLocal : OpType.DeleteRemote)).Sum(o => o.Affected);

    public int OverwritesOn(Side side) =>
        Ops.Count(o => o.Overwrite && o.Type == (side == Side.Local ? OpType.Download : OpType.Upload));

    public long UploadBytes => Ops.Where(o => o.Type is OpType.Upload or OpType.Conflict).Sum(o => o.Local?.Size ?? 0);

    public long DownloadBytes => Ops.Where(o => o.Type is OpType.Download or OpType.Compare or OpType.Conflict).Sum(o => o.Remote?.Size ?? 0);
}

/// <summary>Planner settings; <see cref="Now"/> and <see cref="MachineName"/> keep it deterministic (§6.4).</summary>
public sealed record PlannerSettings
{
    /// <summary>Initial sync (§7.6): deletes are never planned.</summary>
    public bool UnionMode { get; init; }

    public ConflictWinner ConflictWinner { get; init; } = ConflictWinner.Local;

    /// <summary>Local time used in conflict names.</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>False after the folder moved to another volume (§13 #41): FileIds are meaningless.</summary>
    public bool TrustLocalFileIds { get; init; } = true;

    public ImportedBaseMatch ImportedBaseMatch { get; init; } = ImportedBaseMatch.SizeOnly;

    public long MaxCompareDownloadBytes { get; init; } = 64L * 1024 * 1024;

    public SyncFilter Filter { get; init; } = SyncFilter.Empty;

    /// <summary>
    /// One-off user choice per file key (§6.4 "keep local / keep remote"): when the two versions differ, the kept side
    /// overwrites the other one instead of a conflict, download or upload.
    /// </summary>
    public IReadOnlyDictionary<string, Side> KeepSide { get; init; } = new Dictionary<string, Side>();
}

public sealed record PlannerInput(
    LocalSnapshot Local,
    RemoteSnapshot Remote,
    IReadOnlyDictionary<string, BaseEntry> Base,
    PlannerSettings Settings);
