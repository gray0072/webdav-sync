using System.Globalization;
using System.Text;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Planning;

/// <summary>
/// Pure three-way planner <c>(Local, Remote, Base, Settings) → Plan</c> (invariant #11). Contract: §6.9.
/// </summary>
public static class Planner
{
    public static Plan Create(PlannerInput input) => new PlannerRun(input).Run();
}

/// <summary>Change of one side vs base (§6.2): <c>=</c>, touched (content same, metadata differs), <c>Δ</c>, <c>−</c>, <c>+</c>, <c>∅</c>.</summary>
internal enum Change
{
    Unchanged,
    Touched,
    Modified,
    Deleted,
    Created,
    Absent,

    /// <summary>Not known (incomplete listing, unreadable file): no decision for this key.</summary>
    Unknown,
}

/// <summary>
/// One planner run. The working dictionaries are a simulation: phase-0 ops (moves, type-conflict renames,
/// parents of move targets) are applied to them, so the decision pass sees the state after those ops.
/// </summary>
internal sealed class PlannerRun
{
    private readonly PlannerSettings _s;
    private readonly Dictionary<string, LocalEntry> _l;
    private readonly Dictionary<string, RemoteEntry> _r;
    private readonly Dictionary<string, BaseEntry> _b;
    private readonly HashSet<string> _listedL;
    private readonly HashSet<string> _listedR;
    private readonly IReadOnlySet<string> _skippedL;
    private readonly IReadOnlySet<string> _skippedR;
    private readonly List<string> _unionRoots = [];
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private readonly List<SyncOp> _early = [];
    private readonly List<SyncOp> _ops = [];
    private readonly List<string> _warnings = [];
    private readonly Dictionary<string, (bool L, bool R)> _childExists = new(StringComparer.Ordinal);

    /// <summary>Keys moved by the remote side: the server may assign new etags on MOVE, so identity is (size, mtime).</summary>
    private readonly HashSet<string> _movedRemotely = new(StringComparer.Ordinal);

    private readonly HashSet<string> _keepUsed = new(StringComparer.Ordinal);

    /// <summary>Files whose content hash differs from the base although size and mtime are equal (§5.3).</summary>
    private readonly HashSet<string> _silentChanges = new(StringComparer.Ordinal);

    public PlannerRun(PlannerInput input)
    {
        _s = input.Settings;
        _l = input.Local.Entries.Where(kv => _s.Filter.IsSynced(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        _r = input.Remote.Entries.Where(kv => _s.Filter.IsSynced(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        _b = input.Base.Where(kv => kv.Key.Length > 0 && _s.Filter.IsSynced(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        _listedL = new HashSet<string>(input.Local.ListedFolders, StringComparer.Ordinal);
        _listedR = new HashSet<string>(input.Remote.ListedFolders, StringComparer.Ordinal);
        _skippedL = input.Local.SkippedKeys;
        _skippedR = input.Remote.SkippedKeys;
    }

    public Plan Run()
    {
        DetectTypeConflicts();
        DetectLocalMoves();
        DetectRemoteMoves();
        DetectCaseRenames();
        Decide();
        foreach (var key in _s.KeepSide.Keys.Where(k => !_keepUsed.Contains(k)).Order(StringComparer.Ordinal))
        {
            _warnings.Add($"Keep {_s.KeepSide[key].ToString().ToLowerInvariant()} '{key}': not a file present on both sides with different content; ignored.");
        }

        return new Plan(Order(), _warnings);
    }

    // ---------------------------------------------------------------- phase 0: type conflicts

    private void DetectTypeConflicts()
    {
        foreach (var key in _l.Keys.Where(_r.ContainsKey).OrderBy(PathKey.Depth).ThenBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (!_l.TryGetValue(key, out var l) || !_r.TryGetValue(key, out var r) || l.IsDir == r.IsDir)
            {
                continue;
            }

            // Folder vs file (§6.2, §13 #18): the file side's item is renamed to a conflict name and copied over;
            // the folder subtree is merged in union mode, so nothing is deleted.
            var fileSide = l.IsDir ? Side.Remote : Side.Local;
            var filePath = fileSide == Side.Local ? l.Path : r.Path;
            var copyName = ConflictName(PathKey.Parent(key), PathKey.Name(filePath), fileSide);
            var copyKey = PathKey.Combine(PathKey.Parent(key), PathKey.FromPath(copyName));
            _reserved.Add(copyKey);
            _early.Add(new SyncOp
            {
                Type = OpType.Conflict,
                Conflict = ConflictKind.FileVsFolder,
                Key = key,
                IsDir = false,
                Reason = fileSide == Side.Local ? "local file vs remote folder" : "remote file vs local folder",
                LocalPath = l.Path,
                RemotePath = r.Path,
                NewKey = copyKey,
                NewLocalPath = PathKey.Combine(PathKey.Parent(l.Path), copyName),
                NewRemotePath = PathKey.Combine(PathKey.Parent(r.Path), copyName),
                Local = l,
                Remote = r,
                Base = _b.GetValueOrDefault(key),
                Loser = fileSide,
            });
            if (fileSide == Side.Local)
            {
                _l.Remove(key);
            }
            else
            {
                _r.Remove(key);
            }

            AddUnionRoot(key);
        }

        foreach (var (key, b) in _b.ToList())
        {
            if ((_l.TryGetValue(key, out var l) && l.IsDir != b.IsDir) || (_r.TryGetValue(key, out var r) && r.IsDir != b.IsDir))
            {
                AddUnionRoot(key);
            }
        }
    }

    // ---------------------------------------------------------------- phase 0: moves

    private void DetectLocalMoves()
    {
        if (!_s.TrustLocalFileIds)
        {
            return;
        }

        var byId = new Dictionary<ulong, List<string>>();
        foreach (var (key, l) in _l)
        {
            if (l.FileId is { } id && !_b.ContainsKey(key))
            {
                (byId.TryGetValue(id, out var list) ? list : byId[id] = []).Add(key);
            }
        }

        if (byId.Count == 0)
        {
            return;
        }

        // Shallow first: a folder move rekeys its subtree, so descendants are no longer candidates.
        var candidates = _b.Values
            .Where(e => e.LocalFileId is { } id && byId.ContainsKey(id) && !_l.ContainsKey(e.Key))
            .OrderBy(e => PathKey.Depth(e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var candidate in candidates)
        {
            if (!_b.TryGetValue(candidate.Key, out var b) || !ReferenceEquals(b, candidate) || _l.ContainsKey(b.Key) ||
                IgnoreBase(b.Key) || !KnownAbsent(_l, _listedL, b.Key))
            {
                continue;
            }

            var targets = byId[b.LocalFileId!.Value]
                .Where(c => _l.TryGetValue(c, out var t) && t.IsDir == b.IsDir && !t.Locked && !_b.ContainsKey(c) && !PathKey.IsUnder(c, b.Key) &&
                            ConfirmsLocalMove(b, t))
                .ToList();
            var target = PickUnique(targets, b.Key);
            if (target is null)
            {
                if (targets.Count > 1)
                {
                    _warnings.Add($"Ambiguous local move of '{b.LocalPath}' ({targets.Count} candidates): treated as delete + create.");
                }

                continue;
            }

            if (!_r.TryGetValue(b.Key, out var r) || r.IsDir != b.IsDir || _r.ContainsKey(target) || !KnownAbsent(_r, _listedR, target))
            {
                continue;
            }

            var l = _l[target];
            var parentPath = RemoteFolderPath(PathKey.Parent(target), createEarly: true);
            if (parentPath is null)
            {
                continue;
            }

            var newRemotePath = PathKey.Combine(parentPath, PathKey.Name(l.Path));
            _early.Add(new SyncOp
            {
                Type = OpType.MoveRemote,
                Key = b.Key,
                IsDir = b.IsDir,
                Reason = b.IsDir ? "folder moved locally" : "moved locally",
                LocalPath = b.LocalPath,
                RemotePath = r.Path,
                NewKey = target,
                NewLocalPath = l.Path,
                NewRemotePath = newRemotePath,
                Local = l,
                Remote = r,
                Base = b,
            });
            ApplyRemoteMove(b.Key, target, r.Path, newRemotePath, l.Path);
            byId[b.LocalFileId.Value].Remove(target);
        }
    }

    private void DetectRemoteMoves()
    {
        var fresh = _r.Values.Where(r => !_b.ContainsKey(r.Key) && !_l.ContainsKey(r.Key) && !_reserved.Contains(r.Key)).ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        var byName = fresh.ToLookup(r => PathKey.Name(r.Key), StringComparer.Ordinal);
        var byParent = fresh.ToLookup(r => PathKey.Parent(r.Key), StringComparer.Ordinal);
        var candidates = _b.Values
            .Where(e => !_r.ContainsKey(e.Key) && _l.TryGetValue(e.Key, out var l) && l.IsDir == e.IsDir && !l.Locked)
            .OrderBy(e => PathKey.Depth(e.Key)).ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToList();
        foreach (var candidate in candidates)
        {
            if (!_b.TryGetValue(candidate.Key, out var b) || !ReferenceEquals(b, candidate) || _r.ContainsKey(b.Key) ||
                !_l.ContainsKey(b.Key) || IgnoreBase(b.Key) || !KnownAbsent(_r, _listedR, b.Key))
            {
                continue;
            }

            var bName = PathKey.Name(b.Key);
            var bParent = PathKey.Parent(b.Key);
            var scored = byName[bName].Concat(byParent[bParent]).Distinct()
                .Where(r => _r.TryGetValue(r.Key, out var current) && ReferenceEquals(current, r) &&
                            !_b.ContainsKey(r.Key) && !_l.ContainsKey(r.Key))
                .Where(r => r.IsDir == b.IsDir && !PathKey.IsUnder(r.Key, b.Key))
                .Select(r => (Entry: r, Tier: PathKey.Name(r.Key) == bName ? 0 : 1, Score: b.IsDir ? FolderScore(b, r) : (RemoteMatches(b, r) ? 1.0 : 0.0)))
                .Where(x => x.Score >= 0.5)
                .OrderBy(x => x.Tier).ThenByDescending(x => x.Score)
                .ToList();
            if (scored.Count == 0)
            {
                continue;
            }

            if (scored.Count > 1 && scored[1].Tier == scored[0].Tier && Math.Abs(scored[1].Score - scored[0].Score) < 1e-9)
            {
                _warnings.Add($"Ambiguous remote move of '{b.RemotePath}' ({scored.Count} candidates): treated as delete + create.");
                continue;
            }

            var t = scored[0].Entry;
            if (_l.ContainsKey(t.Key) || !KnownAbsent(_l, _listedL, t.Key))
            {
                continue;
            }

            var parentPath = LocalFolderPath(PathKey.Parent(t.Key), createEarly: true);
            if (parentPath is null)
            {
                continue;
            }

            var l = _l[b.Key];
            var newLocalPath = PathKey.Combine(parentPath, PathKey.Name(t.Path));
            _early.Add(new SyncOp
            {
                Type = OpType.MoveLocal,
                Key = b.Key,
                IsDir = b.IsDir,
                Reason = b.IsDir ? "folder moved remotely" : "moved remotely",
                LocalPath = l.Path,
                RemotePath = b.RemotePath,
                NewKey = t.Key,
                NewLocalPath = newLocalPath,
                NewRemotePath = t.Path,
                Local = l,
                Remote = t,
                Base = b,
            });
            ApplyLocalMove(b.Key, t.Key, l.Path, newLocalPath, t.Path);
        }
    }

    private void DetectCaseRenames()
    {
        foreach (var key in _b.Keys.OrderBy(PathKey.Depth).ThenBy(k => k, StringComparer.Ordinal).ToList())
        {
            if (IgnoreBase(key) || !_b.TryGetValue(key, out var b) ||
                !_l.TryGetValue(key, out var l) || !_r.TryGetValue(key, out var r) ||
                l.IsDir != b.IsDir || r.IsDir != b.IsDir)
            {
                continue;
            }

            var localRenamed = IsRealRename(PathKey.Name(l.Path), PathKey.Name(b.LocalPath));
            var remoteRenamed = IsRealRename(PathKey.Name(r.Path), PathKey.Name(b.RemotePath));
            if (localRenamed && !remoteRenamed)
            {
                var newRemotePath = PathKey.Combine(PathKey.Parent(r.Path), PathKey.Name(l.Path));
                _early.Add(new SyncOp
                {
                    Type = OpType.MoveRemote,
                    Key = key,
                    IsDir = b.IsDir,
                    Reason = "renamed locally (case only)",
                    LocalPath = b.LocalPath,
                    RemotePath = r.Path,
                    NewKey = key,
                    NewLocalPath = l.Path,
                    NewRemotePath = newRemotePath,
                    Local = l,
                    Remote = r,
                    Base = b,
                });
                ApplyRemoteMove(key, key, r.Path, newRemotePath, l.Path);
            }
            else if (remoteRenamed && !localRenamed)
            {
                var newLocalPath = PathKey.Combine(PathKey.Parent(l.Path), PathKey.Name(r.Path));
                _early.Add(new SyncOp
                {
                    Type = OpType.MoveLocal,
                    Key = key,
                    IsDir = b.IsDir,
                    Reason = "renamed remotely (case only)",
                    LocalPath = l.Path,
                    RemotePath = b.RemotePath,
                    NewKey = key,
                    NewLocalPath = newLocalPath,
                    NewRemotePath = r.Path,
                    Local = l,
                    Remote = r,
                    Base = b,
                });
                ApplyLocalMove(key, key, l.Path, newLocalPath, r.Path);
            }
        }

        static bool IsRealRename(string name, string baseName) =>
            !string.Equals(name, baseName, StringComparison.Ordinal) &&
            !string.Equals(name.Normalize(NormalizationForm.FormC), baseName.Normalize(NormalizationForm.FormC), StringComparison.Ordinal);
    }

    private void ApplyRemoteMove(string oldKey, string newKey, string oldRemotePath, string newRemotePath, string newLocalPath)
    {
        foreach (var (key, r) in _r.Where(kv => PathKey.IsSelfOrUnder(kv.Key, oldKey)).ToList())
        {
            _r.Remove(key);
            _r[PathKey.Rebase(key, oldKey, newKey)] = r with
            {
                Key = PathKey.Rebase(key, oldKey, newKey),
                Path = PathKey.Rebase(r.Path, oldRemotePath, newRemotePath),
            };
        }

        RekeySet(_listedR, oldKey, newKey);
        RebaseBase(oldKey, newKey, newLocalPath, newRemotePath);
    }

    private void ApplyLocalMove(string oldKey, string newKey, string oldLocalPath, string newLocalPath, string newRemotePath)
    {
        foreach (var (key, l) in _l.Where(kv => PathKey.IsSelfOrUnder(kv.Key, oldKey)).ToList())
        {
            _l.Remove(key);
            _l[PathKey.Rebase(key, oldKey, newKey)] = l with
            {
                Key = PathKey.Rebase(key, oldKey, newKey),
                Path = PathKey.Rebase(l.Path, oldLocalPath, newLocalPath),
            };
        }

        RekeySet(_listedL, oldKey, newKey);
        RebaseBase(oldKey, newKey, newLocalPath, newRemotePath);
        _movedRemotely.UnionWith(_b.Keys.Where(k => PathKey.IsSelfOrUnder(k, newKey)));
    }

    /// <summary>Same transformation as <c>StateStore.RebaseSubtree</c> applies after the move.</summary>
    private void RebaseBase(string oldKey, string newKey, string newLocalPath, string newRemotePath)
    {
        if (!_b.TryGetValue(oldKey, out var root))
        {
            return;
        }

        var subtree = _b.Values.Where(e => PathKey.IsSelfOrUnder(e.Key, oldKey)).ToList();
        foreach (var e in subtree)
        {
            _b.Remove(e.Key);
        }

        foreach (var e in subtree)
        {
            var moved = e.Rebase(root, newKey, newLocalPath, newRemotePath);
            _b[moved.Key] = moved;
        }
    }

    private static void RekeySet(HashSet<string> set, string oldKey, string newKey)
    {
        foreach (var key in set.Where(k => PathKey.IsSelfOrUnder(k, oldKey)).ToList())
        {
            set.Remove(key);
            set.Add(PathKey.Rebase(key, oldKey, newKey));
        }
    }

    private static string? PickUnique(List<string> candidates, string oldKey)
    {
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var sameName = candidates.Where(c => PathKey.Name(c) == PathKey.Name(oldKey)).ToList();
        if (sameName.Count == 1)
        {
            return sameName[0];
        }

        var sameParent = candidates.Where(c => PathKey.Parent(c) == PathKey.Parent(oldKey)).ToList();
        return sameParent.Count == 1 ? sameParent[0] : null;
    }

    /// <summary>
    /// A matching FileId alone is weak evidence (MFT records of deleted files are reused, other file systems have
    /// weaker ids), so a move also needs (§6.3): for a file the same size and mtime or the same name; for a folder
    /// the same name or most of its content.
    /// </summary>
    private bool ConfirmsLocalMove(BaseEntry b, LocalEntry candidate)
    {
        if (PathKey.Name(candidate.Key) == PathKey.Name(b.Key))
        {
            return true;
        }

        if (!b.IsDir)
        {
            return candidate.Size == b.LocalSize && Mtime.Equal(candidate.MtimeUtc, b.LocalMtimeUtc);
        }

        int total = 0, matched = 0;
        foreach (var e in _b.Values)
        {
            if (e.IsDir || !PathKey.IsUnder(e.Key, b.Key))
            {
                continue;
            }

            total++;
            if (_l.TryGetValue(PathKey.Rebase(e.Key, b.Key, candidate.Key), out var l) && !l.IsDir && l.Size == e.LocalSize)
            {
                matched++;
            }
        }

        return total > 0 && matched * 2 >= total;
    }

    /// <summary>Remote file move criterion (§6.3): same size and same hash, or same etag, or same mtime.</summary>
    private static bool RemoteMatches(BaseEntry b, RemoteEntry r)
    {
        if (r.Size != b.RemoteSize)
        {
            return false;
        }

        if (r.Hash is { } h && b.Hash is { } bh)
        {
            return h == bh;
        }

        return (r.ETag is not null && r.ETag == b.RemoteETag) || (r.MtimeUtc is not null && Mtime.Equal(r.MtimeUtc, b.RemoteMtimeUtc));
    }

    /// <summary>Share of the base folder's files found under the candidate with the same relative key and size.</summary>
    private double FolderScore(BaseEntry b, RemoteEntry candidate)
    {
        int total = 0, matched = 0;
        foreach (var e in _b.Values)
        {
            if (e.IsDir || !PathKey.IsUnder(e.Key, b.Key))
            {
                continue;
            }

            total++;
            if (_r.TryGetValue(PathKey.Rebase(e.Key, b.Key, candidate.Key), out var r) && !r.IsDir && r.Size == e.RemoteSize)
            {
                matched++;
            }
        }

        return total == 0 ? 0 : (double)matched / total;
    }

    // ---------------------------------------------------------------- decision pass

    private void Decide()
    {
        foreach (var skipped in _skippedL)
        {
            MarkChild(PathKey.Parent(skipped), local: true, remote: false);
        }

        foreach (var skipped in _skippedR)
        {
            MarkChild(PathKey.Parent(skipped), local: false, remote: true);
        }

        var keys = new HashSet<string>(_l.Keys, StringComparer.Ordinal);
        keys.UnionWith(_r.Keys);
        keys.UnionWith(_b.Keys);
        keys.ExceptWith(_reserved);
        keys.Remove(string.Empty);

        foreach (var key in keys.OrderByDescending(PathKey.Depth).ThenBy(k => k, StringComparer.Ordinal))
        {
            _l.TryGetValue(key, out var l);
            _r.TryGetValue(key, out var r);
            var isDir = l?.IsDir ?? r?.IsDir ?? _b[key].IsDir;
            var (existsL, existsR) = isDir ? DecideFolder(key, l, r) : DecideFile(key, l, r);
            MarkChild(PathKey.Parent(key), existsL, existsR);
        }
    }

    private void MarkChild(string parent, bool local, bool remote)
    {
        var current = _childExists.GetValueOrDefault(parent);
        _childExists[parent] = (current.L || local, current.R || remote);
    }

    /// <summary>Returns whether the item exists on each side after the plan.</summary>
    private (bool L, bool R) DecideFile(string key, LocalEntry? l, RemoteEntry? r)
    {
        var realBase = _b.GetValueOrDefault(key);
        var b = IgnoreBase(key) ? null : realBase;
        var lc = ClassifyLocal(key, l, b);
        var rc = ClassifyRemote(key, r, b);
        if (lc == Change.Unknown || rc == Change.Unknown)
        {
            return (l is not null, r is not null);
        }

        if (l is not null && r is not null && _s.KeepSide.TryGetValue(key, out var keep) && !SameContent(l, r, b, lc, rc))
        {
            _keepUsed.Add(key);
            if (keep == Side.Local)
            {
                Add(OpType.Upload, key, l, r, b, "keep local (user choice): overwrite remote", overwrite: true);
            }
            else
            {
                Add(OpType.Download, key, l, r, b, "keep remote (user choice): overwrite local", overwrite: true);
            }

            return (true, true);
        }

        var union = _s.UnionMode || IgnoreBase(key);
        if (b is null)
        {
            switch (l, r)
            {
                case (not null, null):
                    Add(OpType.Upload, key, l, r, null, "local new", remotePath: RemoteTargetPath(key));
                    return (true, true);
                case (null, not null):
                    Add(OpType.Download, key, l, r, null, "remote new", localPath: LocalTargetPath(key));
                    return (true, true);
                case (not null, not null):
                    CompareOrConflict(key, l, r, null, ConflictKind.BothCreated, "created on both sides");
                    return (true, true);
                default:
                    if (realBase is not null)
                    {
                        Add(OpType.ForgetBase, key, null, null, realBase, "deleted on both sides");
                    }

                    return (false, false);
            }
        }

        var lSame = lc is Change.Unchanged or Change.Touched;
        var rSame = rc is Change.Unchanged or Change.Touched;
        if (lSame && rSame)
        {
            if (lc == Change.Touched || rc == Change.Touched || BaseNeedsRefresh(b, l!, r!))
            {
                Add(OpType.UpdateBase, key, l, r, b, rc == Change.Touched && !b.RemoteConfirmed ? "adopt remote identity (imported base)" : "metadata changed, content same");
            }

            return (true, true);
        }

        switch (lc, rc)
        {
            case (_, Change.Modified) when lSame:
                Add(OpType.Download, key, l, r, b, "remote modified", overwrite: true);
                return (true, true);
            case (_, Change.Deleted) when lSame:
                if (union)
                {
                    Add(OpType.Upload, key, l, null, b, "remote deleted before initial sync completed: restore", remotePath: RemoteTargetPath(key));
                    return (true, true);
                }

                Add(OpType.DeleteLocal, key, l, null, b, "remote deleted");
                return (false, false);
            case (Change.Modified, _) when rSame:
                Add(OpType.Upload, key, l, r, b, "local modified", overwrite: true);
                return (true, true);
            case (Change.Modified, Change.Modified):
                CompareOrConflict(key, l!, r!, b, ConflictKind.BothModified, "modified on both sides");
                return (true, true);
            case (Change.Modified, Change.Deleted):
                Add(OpType.Upload, key, l, null, b, "local modified, remote deleted: edit wins", remotePath: RemoteTargetPath(key));
                _warnings.Add($"'{l!.Path}' was deleted remotely but modified locally: uploaded again.");
                return (true, true);
            case (Change.Deleted, _) when rSame:
                if (union)
                {
                    Add(OpType.Download, key, null, r, b, "local deleted before initial sync completed: restore", localPath: LocalTargetPath(key));
                    return (true, true);
                }

                Add(OpType.DeleteRemote, key, null, r, b, "local deleted");
                return (false, false);
            case (Change.Deleted, Change.Modified):
                Add(OpType.Download, key, null, r, b, "remote modified, local deleted: edit wins", localPath: LocalTargetPath(key));
                _warnings.Add($"'{r!.Path}' was deleted locally but modified remotely: downloaded again.");
                return (true, true);
            case (Change.Deleted, Change.Deleted):
                Add(OpType.ForgetBase, key, null, null, b, "deleted on both sides");
                return (false, false);
            default:
                _warnings.Add($"No rule for '{key}' (local {lc}, remote {rc}); skipped.");
                return (l is not null, r is not null);
        }
    }

    private (bool L, bool R) DecideFolder(string key, LocalEntry? l, RemoteEntry? r)
    {
        var realBase = _b.GetValueOrDefault(key);
        var b = IgnoreBase(key) ? null : realBase;
        var children = _childExists.GetValueOrDefault(key);
        var lAbsent = l is null && KnownAbsent(_l, _listedL, key);
        var rAbsent = r is null && KnownAbsent(_r, _listedR, key);
        if ((l is null && !lAbsent) || (r is null && !rAbsent))
        {
            return (l is not null || children.L, r is not null || children.R);
        }

        var union = _s.UnionMode || IgnoreBase(key);
        if (l is not null && r is not null)
        {
            if (b is null || BaseNeedsRefresh(b, l, r))
            {
                Add(OpType.UpdateBase, key, l, r, b, b is null ? "folder exists on both sides" : "folder metadata changed");
            }

            return (true, true);
        }

        if (l is not null)
        {
            if (b is null || union || children.R)
            {
                var reason = b is null ? "local new folder" : union ? "remote deleted before initial sync completed: restore" : "remote deleted, but contains new or changed items";
                Add(OpType.MkdirRemote, key, l, null, b, reason, remotePath: RemoteTargetPath(key));
                if (b is not null && !union)
                {
                    _warnings.Add($"Folder '{l.Path}' was deleted remotely but contains new or changed items: re-created.");
                }

                return (true, true);
            }

            if (children.L)
            {
                _warnings.Add($"Folder '{l.Path}' was deleted remotely but contains items that are not synced: kept locally.");
                return (true, false);
            }

            Add(OpType.DeleteLocal, key, l, null, b, "remote deleted");
            return (false, false);
        }

        if (r is not null)
        {
            if (b is null || union || children.L)
            {
                var reason = b is null ? "remote new folder" : union ? "local deleted before initial sync completed: restore" : "local deleted, but contains new or changed items";
                Add(OpType.MkdirLocal, key, null, r, b, reason, localPath: LocalTargetPath(key));
                if (b is not null && !union)
                {
                    _warnings.Add($"Folder '{r.Path}' was deleted locally but contains new or changed items: re-created.");
                }

                return (true, true);
            }

            if (children.R)
            {
                _warnings.Add($"Folder '{r.Path}' was deleted locally but contains remote items that are not synced: kept on the remote.");
                return (false, true);
            }

            Add(OpType.DeleteRemote, key, null, r, b, "local deleted");
            return (false, false);
        }

        if (realBase is not null)
        {
            Add(OpType.ForgetBase, key, null, null, realBase, "deleted on both sides");
        }

        return (false, false);
    }

    private void CompareOrConflict(string key, LocalEntry l, RemoteEntry r, BaseEntry? b, ConflictKind kind, string reason)
    {
        if (l.Size == r.Size && l.Hash is { } lh && r.Hash is { } rh)
        {
            if (lh == rh)
            {
                Add(OpType.UpdateBase, key, l, r, b, "same content on both sides");
                return;
            }
        }
        else if (l.Size == r.Size && r.Hash is null && r.Size <= _s.MaxCompareDownloadBytes)
        {
            AddConflict(OpType.Compare, key, l, r, b, kind, "same size, remote hash unknown: download and compare");
            return;
        }

        AddConflict(OpType.Conflict, key, l, r, b, kind, reason);
    }

    private void AddConflict(OpType type, string key, LocalEntry l, RemoteEntry r, BaseEntry? b, ConflictKind kind, string reason)
    {
        var loser = _s.ConflictWinner switch
        {
            ConflictWinner.Remote => Side.Local,
            ConflictWinner.Newer => (r.MtimeUtc ?? DateTime.MinValue) > l.MtimeUtc ? Side.Local : Side.Remote,
            _ => Side.Remote,
        };
        var loserName = PathKey.Name(loser == Side.Local ? l.Path : r.Path);
        var copyName = ConflictName(PathKey.Parent(key), loserName, loser);
        var copyKey = PathKey.Combine(PathKey.Parent(key), PathKey.FromPath(copyName));
        _reserved.Add(copyKey);
        _ops.Add(new SyncOp
        {
            Type = type,
            Conflict = kind,
            Key = key,
            IsDir = false,
            Reason = reason,
            LocalPath = l.Path,
            RemotePath = r.Path,
            NewKey = copyKey,
            NewLocalPath = PathKey.Combine(PathKey.Parent(l.Path), copyName),
            NewRemotePath = PathKey.Combine(PathKey.Parent(r.Path), copyName),
            Local = l,
            Remote = r,
            Base = b,
            Loser = loser,
        });
    }

    private void Add(
        OpType type,
        string key,
        LocalEntry? l,
        RemoteEntry? r,
        BaseEntry? b,
        string reason,
        string? localPath = null,
        string? remotePath = null,
        bool overwrite = false)
    {
        _ops.Add(new SyncOp
        {
            Type = type,
            Key = key,
            IsDir = l?.IsDir ?? r?.IsDir ?? b?.IsDir ?? false,
            Reason = reason,
            LocalPath = localPath ?? l?.Path ?? b?.LocalPath,
            RemotePath = remotePath ?? r?.Path ?? b?.RemotePath,
            Local = l,
            Remote = r,
            Base = b,
            Overwrite = overwrite,
        });
    }

    /// <summary>Both versions are known to be equal: by hash, otherwise both unchanged vs the base.</summary>
    private static bool SameContent(LocalEntry l, RemoteEntry r, BaseEntry? b, Change lc, Change rc) =>
        l.Hash is { } lh && r.Hash is { } rh
            ? l.Size == r.Size && lh == rh
            : b is not null && lc is Change.Unchanged or Change.Touched && rc is Change.Unchanged or Change.Touched;

    private static bool BaseNeedsRefresh(BaseEntry b, LocalEntry l, RemoteEntry r) =>
        !string.Equals(l.Path, b.LocalPath, StringComparison.Ordinal) ||
        !string.Equals(r.Path, b.RemotePath, StringComparison.Ordinal) ||
        (l.FileId is not null && l.FileId != b.LocalFileId) ||
        !b.RemoteConfirmed;

    // ---------------------------------------------------------------- classification

    private Change ClassifyLocal(string key, LocalEntry? l, BaseEntry? b)
    {
        if (l is null)
        {
            return KnownAbsent(_l, _listedL, key) ? (b is null ? Change.Absent : Change.Deleted) : Change.Unknown;
        }

        if (b is null)
        {
            return Change.Created;
        }

        if (l.IsDir)
        {
            return Change.Unchanged;
        }

        if (l.Size == b.LocalSize && Mtime.Equal(l.MtimeUtc, b.LocalMtimeUtc))
        {
            // A hash is known for an unchanged size and mtime only when the file was re-hashed (replaced file with a new
            // FileId, or the rolling verify): a different content is an edit that kept the time, or corruption.
            if (l.Hash is { } lh && b.LocalHash is { } blh && lh != blh)
            {
                _silentChanges.Add(key);
                return Change.Modified;
            }

            return Change.Unchanged;
        }

        if (l.Locked)
        {
            return Change.Unknown;
        }

        return l.Hash is { } h && b.Hash is { } bh && h == bh ? Change.Touched : Change.Modified;
    }

    private Change ClassifyRemote(string key, RemoteEntry? r, BaseEntry? b)
    {
        if (r is null)
        {
            return KnownAbsent(_r, _listedR, key) ? (b is null ? Change.Absent : Change.Deleted) : Change.Unknown;
        }

        if (b is null)
        {
            return Change.Created;
        }

        if (r.IsDir)
        {
            return Change.Unchanged;
        }

        if (r.Size != b.RemoteSize)
        {
            return Change.Modified;
        }

        if (r.Hash is { } h && b.Hash is { } bh)
        {
            return h != bh ? Change.Modified : SameRemoteIdentity(r, b) ? Change.Unchanged : Change.Touched;
        }

        if (!b.RemoteConfirmed)
        {
            return _s.ImportedBaseMatch == ImportedBaseMatch.SizeOnly || Mtime.Equal(r.MtimeUtc, b.RemoteMtimeUtc)
                ? Change.Touched
                : Change.Modified;
        }

        if (SameRemoteIdentity(r, b))
        {
            return Change.Unchanged;
        }

        return _movedRemotely.Contains(key) && Mtime.Equal(r.MtimeUtc, b.RemoteMtimeUtc) ? Change.Touched : Change.Modified;
    }

    /// <summary>Remote identity of a version (§4.3): etag if present on both, otherwise (size, mtime).</summary>
    private static bool SameRemoteIdentity(RemoteEntry r, BaseEntry b) =>
        r.ETag is not null && b.RemoteETag is not null
            ? r.ETag == b.RemoteETag
            : r.Size == b.RemoteSize && Mtime.Equal(r.MtimeUtc, b.RemoteMtimeUtc);

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// "Known absent" (§6.8): the nearest existing ancestor is a completely listed folder, or a file.
    /// </summary>
    private static bool KnownAbsent<T>(Dictionary<string, T> entries, HashSet<string> listed, string key)
        where T : ISnapshotEntry
    {
        if (entries.ContainsKey(key))
        {
            return false;
        }

        var parent = key;
        while (parent.Length > 0)
        {
            parent = PathKey.Parent(parent);
            if (listed.Contains(parent))
            {
                return true;
            }

            if (parent.Length > 0 && entries.TryGetValue(parent, out var e))
            {
                return !e.IsDir;
            }
        }

        return false;
    }

    private void AddUnionRoot(string key)
    {
        if (!_unionRoots.Any(u => PathKey.IsSelfOrUnder(key, u)))
        {
            _unionRoots.Add(key);
        }
    }

    /// <summary>Base is not trusted under a union root (type change, §6.2 folder rules).</summary>
    private bool IgnoreBase(string key) => _unionRoots.Any(u => PathKey.IsSelfOrUnder(key, u));

    /// <summary>Remote path of a folder; optionally plans an early MKCOL so a move target's parent exists.</summary>
    private string? RemoteFolderPath(string key, bool createEarly)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (_r.TryGetValue(key, out var r))
        {
            return r.IsDir ? r.Path : null;
        }

        if (!createEarly || !_l.TryGetValue(key, out var l) || !l.IsDir || !KnownAbsent(_r, _listedR, key))
        {
            return null;
        }

        var parent = RemoteFolderPath(PathKey.Parent(key), createEarly);
        if (parent is null)
        {
            return null;
        }

        var path = PathKey.Combine(parent, PathKey.Name(l.Path));
        var created = new RemoteEntry(key, path, true, 0, null, null);
        _early.Add(new SyncOp { Type = OpType.MkdirRemote, Key = key, IsDir = true, Reason = "parent of a moved item", LocalPath = l.Path, RemotePath = path, Local = l });
        _r[key] = created;
        _listedR.Add(key);
        _b[key] = BaseEntry.From(key, l, created, null, DateTime.MinValue);
        return path;
    }

    private string? LocalFolderPath(string key, bool createEarly)
    {
        if (key.Length == 0)
        {
            return string.Empty;
        }

        if (_l.TryGetValue(key, out var l))
        {
            return l.IsDir ? l.Path : null;
        }

        if (!createEarly || !_r.TryGetValue(key, out var r) || !r.IsDir || !KnownAbsent(_l, _listedL, key))
        {
            return null;
        }

        var parent = LocalFolderPath(PathKey.Parent(key), createEarly);
        if (parent is null)
        {
            return null;
        }

        var path = PathKey.Combine(parent, PathKey.Name(r.Path));
        var created = new LocalEntry(key, path, true, 0, default, null);
        _early.Add(new SyncOp { Type = OpType.MkdirLocal, Key = key, IsDir = true, Reason = "parent of a moved item", LocalPath = path, RemotePath = r.Path, Remote = r });
        _l[key] = created;
        _listedL.Add(key);
        _b[key] = BaseEntry.From(key, created, r, null, DateTime.MinValue);
        return path;
    }

    /// <summary>Remote path for a new item: the remote parent's real name (case) plus the local name.</summary>
    private string RemoteTargetPath(string key)
    {
        if (_r.TryGetValue(key, out var r))
        {
            return r.Path;
        }

        var name = _l.TryGetValue(key, out var l) ? PathKey.Name(l.Path) : PathKey.Name(_b.GetValueOrDefault(key)?.LocalPath ?? key);
        var parent = PathKey.Parent(key);
        return parent.Length == 0 ? name : PathKey.Combine(RemoteTargetPath(parent), name);
    }

    private string LocalTargetPath(string key)
    {
        if (_l.TryGetValue(key, out var l))
        {
            return l.Path;
        }

        var name = _r.TryGetValue(key, out var r) ? PathKey.Name(r.Path) : PathKey.Name(_b.GetValueOrDefault(key)?.RemotePath ?? key);
        var parent = PathKey.Parent(key);
        return parent.Length == 0 ? name : PathKey.Combine(LocalTargetPath(parent), name);
    }

    /// <summary><c>name (conflict YYYY-MM-DD HHmmss side).ext</c>, unique within the parent (§6.4).</summary>
    private string ConflictName(string parentKey, string name, Side loser)
    {
        var label = loser == Side.Remote ? "cloud" : _s.MachineName;
        var stamp = _s.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        if (stem.Length == 0)
        {
            (stem, ext) = (name, string.Empty);
        }

        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? string.Empty : $" {i}";
            var candidate = $"{stem} (conflict {stamp} {label}{suffix}){ext}";
            var key = PathKey.Combine(parentKey, PathKey.FromPath(candidate));
            if (!_l.ContainsKey(key) && !_r.ContainsKey(key) && !_b.ContainsKey(key) && !_reserved.Contains(key))
            {
                return candidate;
            }
        }
    }

    // ---------------------------------------------------------------- ordering (§6.5)

    private List<SyncOp> Order()
    {
        var deletesLocal = _ops.Where(o => o is { Type: OpType.DeleteLocal, IsDir: true }).Select(o => o.Key).ToList();
        var deletesRemote = _ops.Where(o => o is { Type: OpType.DeleteRemote, IsDir: true }).Select(o => o.Key).ToList();
        var forgotten = _ops.Where(o => o is { Type: OpType.ForgetBase, IsDir: true }).Select(o => o.Key).ToList();

        bool CoveredBy(SyncOp op, List<string> folders) => folders.Any(f => PathKey.IsUnder(op.Key, f));

        var ops = _ops
            .Where(o => !(o.Type == OpType.DeleteLocal && CoveredBy(o, deletesLocal)))
            .Where(o => !(o.Type == OpType.DeleteRemote && CoveredBy(o, deletesRemote)))
            .Where(o => !(o.Type == OpType.ForgetBase && (CoveredBy(o, deletesLocal) || CoveredBy(o, deletesRemote) || CoveredBy(o, forgotten))))
            .Select(o => o.IsDelete && o.IsDir ? o with { Affected = _b.Keys.Count(k => PathKey.IsSelfOrUnder(k, o.Key)) } : o)
            .ToList();

        static int Phase(SyncOp o) => o.Type switch
        {
            OpType.MkdirLocal or OpType.MkdirRemote => 1,
            OpType.UpdateBase => 2,
            OpType.Upload or OpType.Download or OpType.Compare or OpType.Conflict => 3,
            OpType.DeleteLocal or OpType.DeleteRemote => 4,
            _ => 5,
        };

        var ordered = ops
            .OrderBy(Phase)
            .ThenBy(o => Phase(o) == 1 ? PathKey.Depth(o.Key) : Phase(o) == 4 ? -PathKey.Depth(o.Key) : 0)
            .ThenBy(o => o.Bytes)
            .ThenBy(o => o.Key, StringComparer.Ordinal);
        return [.. _early.Select(o => o with { Early = true }), .. ordered.Select(Review)];

        // Same size and time, other content: may be disk corruption, so a human decides in every mode (§10).
        SyncOp Review(SyncOp o) =>
            _silentChanges.Contains(o.Key) && !o.IsBaseOnly
                ? o with { NeedsReview = true, Reason = o.Reason + "; content differs, size and time unchanged" }
                : o;
    }
}
