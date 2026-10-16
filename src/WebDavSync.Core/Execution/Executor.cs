using Microsoft.Extensions.Logging;
using WebDavSync.Core.Config;
using WebDavSync.Core.Local;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.State;

namespace WebDavSync.Core.Execution;

public sealed class ExecutorContext
{
    public required StatePaths State { get; init; }

    public required string RemoteRoot { get; init; }

    public required IRemoteStore Remote { get; init; }

    public required StateStore Store { get; init; }

    public required PairSettings Settings { get; init; }

    public required SyncFilter Filter { get; init; }

    public required ILogger Logger { get; init; }

    public int MaxAttempts { get; init; } = 3;
}

public sealed record OpResult(SyncOp Op, JournalStatus Status, string? Message);

public sealed record ExecutionResult(IReadOnlyList<OpResult> Results, string? AbortReason)
{
    public int Done => Results.Count(r => r.Status == JournalStatus.Done);

    public int Failed => Results.Count(r => r.Status == JournalStatus.Failed);

    public int Skipped => Results.Count(r => r.Status == JournalStatus.Skipped);

    public bool AllDone => AbortReason is null && Results.All(r => r.Status == JournalStatus.Done);
}

/// <summary>The state changed between planning and execution: the op is skipped and re-planned next cycle.</summary>
public sealed class StaleOpException(string message) : Exception(message);

/// <summary>
/// Executes ops sequentially (§6.1 steps 6–7): re-checks the planned state before acting, verifies the result on the
/// target side, and only then commits the base in the same transaction as the journal record (invariant #7).
/// </summary>
public sealed class Executor(ExecutorContext ctx)
{
    private readonly LocalTrash _trash = new(ctx.State, ctx.Settings.LocalDeleteTarget);
    private bool _uploadsStopped;
    private bool _downloadsStopped;

    public async Task<ExecutionResult> ExecuteAsync(IReadOnlyList<SyncOp> ops, string cycleId, IProgress<OpResult>? progress, CancellationToken ct)
    {
        var results = new List<OpResult>();
        var baseBatch = new List<(SyncOp Op, BaseMutation Mutation)>();

        void FlushBaseBatch()
        {
            if (baseBatch.Count == 0)
            {
                return;
            }

            ctx.Store.ApplyBase(baseBatch.Select(b => b.Mutation));
            foreach (var (op, _) in baseBatch)
            {
                var result = new OpResult(op, JournalStatus.Done, null);
                results.Add(result);
                progress?.Report(result);
            }

            baseBatch.Clear();
        }

        foreach (var op in ops)
        {
            if (ct.IsCancellationRequested)
            {
                FlushBaseBatch();
                return new ExecutionResult(results, "cancelled");
            }

            if (op.IsBaseOnly)
            {
                baseBatch.Add((op, op.Type == OpType.ForgetBase ? new RemoveBaseSubtree(op.Key) : new UpsertBase(UpdateBaseEntry(op))));
                continue;
            }

            FlushBaseBatch();
            var outcome = await ExecuteOneAsync(op, cycleId, ct).ConfigureAwait(false);
            results.Add(outcome.Result);
            progress?.Report(outcome.Result);
            if (outcome.Abort is not null)
            {
                return new ExecutionResult(results, outcome.Abort);
            }

            if (op.Early && outcome.Result.Status != JournalStatus.Done)
            {
                return new ExecutionResult(results, $"phase-0 op did not complete ({op}); the rest of the plan depends on it");
            }
        }

        FlushBaseBatch();
        return new ExecutionResult(results, null);
    }

    private async Task<(OpResult Result, string? Abort)> ExecuteOneAsync(SyncOp op, string cycleId, CancellationToken ct)
    {
        if ((_uploadsStopped && UploadsData(op)) || (_downloadsStopped && DownloadsData(op)))
        {
            return (new OpResult(op, JournalStatus.Skipped, _uploadsStopped ? "uploads stopped: remote quota exceeded" : "downloads stopped: local disk full"), null);
        }

        var opId = Guid.NewGuid().ToString("N");
        ctx.Store.JournalStart(opId, cycleId, op.Type.ToString(), op.LocalPath ?? op.RemotePath, op.NewLocalPath ?? op.NewRemotePath);
        var started = DateTime.UtcNow;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (mutations, conflict) = await RunAsync(op, opId, ct).ConfigureAwait(false);
                ctx.Store.JournalFinish(opId, JournalStatus.Done, attempt, null, mutations);
                if (conflict is { } c)
                {
                    ctx.Store.AddConflict(op.Key, c.LocalCopy, c.RemoteCopy);
                }

                Log(op, "done", started, null);
                return (new OpResult(op, JournalStatus.Done, conflict is null ? null : $"conflict copy: {conflict.Value.LocalCopy}"), null);
            }
            catch (StaleOpException ex)
            {
                ctx.Store.JournalFinish(opId, JournalStatus.Skipped, attempt, ex.Message);
                Log(op, "skipped", started, ex.Message);
                return (new OpResult(op, JournalStatus.Skipped, ex.Message), null);
            }
            catch (RemoteException ex) when (ex.IsRetryable && attempt < ctx.MaxAttempts)
            {
                var delay = Backoff.Delay(attempt, ex.RetryAfter);
                ctx.Logger.LogWarning("{Op} failed ({Error}); retry {Attempt} in {Delay}", op, ex.Message, attempt + 1, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (RemoteException ex)
            {
                ctx.Store.JournalFinish(opId, JournalStatus.Failed, attempt, ex.Message);
                Log(op, "failed", started, ex.Message);
                if (ex.Kind == RemoteErrorKind.Quota)
                {
                    _uploadsStopped = true;
                }

                return (new OpResult(op, JournalStatus.Failed, ex.Message), ex.StopsCycle ? $"{ex.Kind}: {ex.Message}" : null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ctx.Store.JournalFinish(opId, JournalStatus.Interrupted, attempt, "cancelled");
                return (new OpResult(op, JournalStatus.Interrupted, "cancelled"), "cancelled");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (ex is IOException io && LocalFs.IsDiskFull(io))
                {
                    _downloadsStopped = true;
                }

                var message = ex is IOException sharing && LocalFs.IsSharingViolation(sharing) ? "file is locked by another process: " + ex.Message : ex.Message;
                ctx.Store.JournalFinish(opId, JournalStatus.Failed, attempt, message);
                Log(op, "failed", started, message);
                return (new OpResult(op, JournalStatus.Failed, message), null);
            }
        }
    }

    private void Log(SyncOp op, string result, DateTime started, string? message) =>
        ctx.Logger.LogInformation(
            "Op {Type} {Path} key={Key} reason='{Reason}' bytes={Bytes} result={Result} duration={Ms}ms {Message}",
            op.Type, op.DisplayPath, op.Key, op.Reason, op.Bytes, result, (int)(DateTime.UtcNow - started).TotalMilliseconds, message);

    private static bool UploadsData(SyncOp op) => op.Type is OpType.Upload or OpType.Conflict or OpType.Compare or OpType.MkdirRemote;

    private static bool DownloadsData(SyncOp op) => op.Type is OpType.Download or OpType.Conflict or OpType.Compare;

    private async Task<(List<BaseMutation> Mutations, (string LocalCopy, string RemoteCopy)? Conflict)> RunAsync(SyncOp op, string opId, CancellationToken ct)
    {
        switch (op.Type)
        {
            case OpType.MkdirLocal:
                return ([MkdirLocal(op)], null);
            case OpType.MkdirRemote:
                return ([await MkdirRemoteAsync(op, ct).ConfigureAwait(false)], null);
            case OpType.Upload:
                return ([await UploadAsync(op, ct).ConfigureAwait(false)], null);
            case OpType.Download:
                return ([await DownloadAsync(op, opId, ct).ConfigureAwait(false)], null);
            case OpType.MoveLocal:
                return (MoveLocal(op, opId), null);
            case OpType.MoveRemote:
                return (await MoveRemoteAsync(op, opId, ct).ConfigureAwait(false), null);
            case OpType.DeleteLocal:
                return ([DeleteLocal(op)], null);
            case OpType.DeleteRemote:
                return ([await DeleteRemoteAsync(op, ct).ConfigureAwait(false)], null);
            case OpType.Compare:
                return await CompareAsync(op, opId, ct).ConfigureAwait(false);
            case OpType.Conflict when op.Conflict == ConflictKind.FileVsFolder:
                return await FileVsFolderAsync(op, opId, ct).ConfigureAwait(false);
            case OpType.Conflict:
                return await ResolveConflictAsync(op, opId, null, ct).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"Unexpected op type {op.Type}.");
        }
    }

    // ---------------------------------------------------------------- helpers

    private string LocalFull(string relativePath) => PathKey.ToLocalFull(ctx.State.LocalRoot, relativePath);

    private string RemoteFull(string relativePath) => PathKey.ToRemoteFull(ctx.RemoteRoot, relativePath);

    private LocalEntry? StatLocal(string relativePath) => LocalFs.Stat(ctx.State.LocalRoot, relativePath);

    private async Task<RemoteEntry?> StatRemoteAsync(string relativePath, CancellationToken ct)
    {
        var item = await ctx.Remote.StatAsync(RemoteFull(relativePath), ct).ConfigureAwait(false);
        return item?.ToEntry(PathKey.Combine(PathKey.Parent(relativePath), item.Name.Length > 0 ? item.Name : PathKey.Name(relativePath)));
    }

    /// <summary>True when the remote item still is the version seen at plan time (etag, else size + mtime).</summary>
    private static bool SameRemote(RemoteEntry? actual, RemoteEntry? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (actual.IsDir || expected.IsDir)
        {
            return actual.IsDir == expected.IsDir;
        }

        if (actual.ETag is not null && expected.ETag is not null)
        {
            return actual.ETag == expected.ETag;
        }

        return actual.Size == expected.Size && Mtime.Equal(actual.MtimeUtc, expected.MtimeUtc);
    }

    private static BaseEntry UpdateBaseEntry(SyncOp op)
    {
        var local = op.Local ?? throw new InvalidOperationException($"UpdateBase without local state: {op.Key}");
        var remote = op.Remote ?? throw new InvalidOperationException($"UpdateBase without remote state: {op.Key}");
        return BaseEntry.From(op.Key, local, remote, local.Hash ?? remote.Hash ?? op.Base?.Hash, DateTime.UtcNow);
    }

    private static BaseEntry Entry(string key, LocalEntry local, RemoteEntry remote, ContentHash? hash) =>
        BaseEntry.From(key, local with { Key = key }, remote with { Key = key }, hash, DateTime.UtcNow);

    private async Task<ContentHash> DownloadToTempAsync(string remoteRelative, string temp, RemoteEntry expected, CancellationToken ct)
    {
        using var hasher = new ContentHasher();
        await using (var download = await ctx.Remote.GetAsync(RemoteFull(remoteRelative), 0, ct).ConfigureAwait(false))
        {
            if (download.ETag is not null && expected.ETag is not null && download.ETag != expected.ETag)
            {
                throw new RemoteException(RemoteErrorKind.Transient, $"'{remoteRelative}' changed during download (etag).");
            }

            await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            int read;
            while ((read = await download.Content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        if (hasher.Length != expected.Size)
        {
            LocalFs.TryDelete(temp);
            throw new RemoteException(RemoteErrorKind.Transient, $"'{remoteRelative}': downloaded {hasher.Length} bytes, expected {expected.Size}.");
        }

        var hash = hasher.Finish();
        if (expected.Hash is { } remoteHash && remoteHash != hash)
        {
            LocalFs.TryDelete(temp);
            throw new RemoteException(RemoteErrorKind.Transient, $"'{remoteRelative}': content hash mismatch after download.");
        }

        if (ctx.Settings.SetLocalMtimeFromRemote && expected.MtimeUtc is { } mtime)
        {
            File.SetLastWriteTimeUtc(temp, mtime);
        }

        return hash;
    }

    /// <summary>PUT and verify the size on the target (§6.6). Returns the verified local and remote states.</summary>
    private async Task<(LocalEntry Local, RemoteEntry Remote, ContentHash Hash)> PutFileAsync(string localRelative, string remoteRelative, LocalEntry expectedLocal, CancellationToken ct)
    {
        using var hasher = new ContentHasher();
        await using (var file = ContentHash.OpenShared(LocalFull(localRelative)))
        await using (var hashing = new HashingStream(file, hasher))
        {
            await ctx.Remote.PutAsync(RemoteFull(remoteRelative), hashing, expectedLocal.Size, expectedLocal.MtimeUtc, ct).ConfigureAwait(false);
        }

        var hash = hasher.Finish();
        var after = StatLocal(localRelative);
        if (!LocalFs.SameFile(after, expectedLocal))
        {
            throw new StaleOpException($"'{localRelative}' changed during upload; re-queued.");
        }

        var remote = await StatRemoteAsync(remoteRelative, ct).ConfigureAwait(false);
        if (remote is null || remote.IsDir || remote.Size != expectedLocal.Size)
        {
            throw new RemoteException(RemoteErrorKind.Transient, $"Upload of '{remoteRelative}' not verified: remote size {remote?.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "missing"}, expected {expectedLocal.Size}.");
        }

        if (remote.Hash is { } remoteHash && remoteHash != hash)
        {
            throw new RemoteException(RemoteErrorKind.Transient, $"Upload of '{remoteRelative}' not verified: content hash mismatch.");
        }

        return (after! with { Hash = hash }, remote with { Hash = remote.Hash ?? hash }, hash);
    }

    private void MoveLocalItem(string from, string to, bool isDir, string opId)
    {
        var fromFull = LocalFull(from);
        var toFull = LocalFull(to);
        Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
        if (string.Equals(PathKey.FromPath(from), PathKey.FromPath(to), StringComparison.Ordinal))
        {
            // Case-only rename on a case-insensitive file system: go through a temp name (§6.3).
            var temp = fromFull + ".wds-" + opId[..8];
            Move(fromFull, temp);
            Move(temp, toFull);
        }
        else
        {
            Move(fromFull, toFull);
        }

        void Move(string a, string b)
        {
            if (isDir)
            {
                Directory.Move(a, b);
            }
            else
            {
                File.Move(a, b);
            }
        }
    }

    // ---------------------------------------------------------------- ops

    private UpsertBase MkdirLocal(SyncOp op)
    {
        var path = op.LocalPath!;
        if (File.Exists(LocalFull(path)))
        {
            throw new StaleOpException($"A local file '{path}' exists where a folder should be created.");
        }

        Directory.CreateDirectory(LocalFull(path));
        var local = StatLocal(path)!;
        var remote = op.Remote ?? throw new InvalidOperationException("MkdirLocal without remote state.");
        return new UpsertBase(Entry(op.Key, local, remote, null));
    }

    private async Task<UpsertBase> MkdirRemoteAsync(SyncOp op, CancellationToken ct)
    {
        var path = op.RemotePath!;
        try
        {
            await ctx.Remote.MkdirAsync(RemoteFull(path), ct).ConfigureAwait(false);
        }
        catch (RemoteException ex) when (ex.Kind == RemoteErrorKind.Conflict)
        {
            // 405: already exists (fine if it is a folder); 409: parent missing — reported below.
        }

        var remote = await StatRemoteAsync(path, ct).ConfigureAwait(false);
        if (remote is not { IsDir: true })
        {
            throw new RemoteException(RemoteErrorKind.Permanent, $"Remote folder '{path}' could not be created.");
        }

        var local = StatLocal(op.LocalPath!) ?? op.Local ?? throw new StaleOpException($"Local folder '{op.LocalPath}' is gone.");
        return new UpsertBase(Entry(op.Key, local, remote, null));
    }

    private async Task<UpsertBase> UploadAsync(SyncOp op, CancellationToken ct)
    {
        var local = StatLocal(op.LocalPath!);
        if (!LocalFs.SameFile(local, op.Local))
        {
            throw new StaleOpException($"'{op.LocalPath}' changed locally since planning.");
        }

        var remoteNow = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (op.Overwrite ? !SameRemote(remoteNow, op.Remote) : remoteNow is not null)
        {
            throw new StaleOpException($"'{op.RemotePath}' changed remotely since planning.");
        }

        var (after, remote, hash) = await PutFileAsync(op.LocalPath!, op.RemotePath!, local!, ct).ConfigureAwait(false);
        return new UpsertBase(Entry(op.Key, after, remote, hash));
    }

    private async Task<UpsertBase> DownloadAsync(SyncOp op, string opId, CancellationToken ct)
    {
        var remoteNow = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (!SameRemote(remoteNow, op.Remote))
        {
            throw new StaleOpException($"'{op.RemotePath}' changed remotely since planning.");
        }

        var localPath = op.LocalPath!;
        var local = StatLocal(localPath);
        if (op.Overwrite ? !LocalFs.SameFile(local, op.Local) : local is not null)
        {
            throw new StaleOpException($"'{localPath}' changed locally since planning.");
        }

        var target = LocalFull(localPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = LocalFs.NewTempPath(ctx.State, opId);
        try
        {
            var hash = await DownloadToTempAsync(op.RemotePath!, temp, remoteNow!, ct).ConfigureAwait(false);
            var current = StatLocal(localPath);
            if (op.Overwrite ? !LocalFs.SameFile(current, local) : current is not null)
            {
                throw new StaleOpException($"'{localPath}' changed locally during download; not overwritten.");
            }

            if (current is not null && ctx.Settings.KeepOverwrittenVersions)
            {
                _trash.Trash(localPath);
            }

            File.Move(temp, target, overwrite: true);
            var after = StatLocal(localPath)!;
            return new UpsertBase(Entry(op.Key, after with { Hash = hash }, remoteNow!, hash));
        }
        finally
        {
            LocalFs.TryDelete(temp);
        }
    }

    private List<BaseMutation> MoveLocal(SyncOp op, string opId)
    {
        var src = StatLocal(op.LocalPath!);
        if (src is null || src.IsDir != op.IsDir || (!op.IsDir && !LocalFs.SameFile(src, op.Local)))
        {
            throw new StaleOpException($"'{op.LocalPath}' changed locally since planning.");
        }

        var sameKey = op.Key == op.NewKey;
        var targetFull = LocalFull(op.NewLocalPath!);
        if (!sameKey && (File.Exists(targetFull) || Directory.Exists(targetFull)))
        {
            throw new StaleOpException($"Local target '{op.NewLocalPath}' already exists.");
        }

        MoveLocalItem(op.LocalPath!, op.NewLocalPath!, op.IsDir, opId);
        return [new RebaseSubtree(op.Key, op.NewKey!, op.NewLocalPath!, op.NewRemotePath!)];
    }

    private async Task<List<BaseMutation>> MoveRemoteAsync(SyncOp op, string opId, CancellationToken ct)
    {
        var src = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (src is null || src.IsDir != op.IsDir)
        {
            throw new StaleOpException($"Remote '{op.RemotePath}' changed since planning.");
        }

        var sameKey = op.Key == op.NewKey;
        if (!sameKey && await StatRemoteAsync(op.NewRemotePath!, ct).ConfigureAwait(false) is not null)
        {
            throw new StaleOpException($"Remote target '{op.NewRemotePath}' already exists.");
        }

        if (sameKey)
        {
            var temp = op.RemotePath + ".wds-" + opId[..8];
            await ctx.Remote.MoveAsync(RemoteFull(op.RemotePath!), RemoteFull(temp), false, ct).ConfigureAwait(false);
            await ctx.Remote.MoveAsync(RemoteFull(temp), RemoteFull(op.NewRemotePath!), false, ct).ConfigureAwait(false);
        }
        else
        {
            await ctx.Remote.MoveAsync(RemoteFull(op.RemotePath!), RemoteFull(op.NewRemotePath!), false, ct).ConfigureAwait(false);
        }

        var moved = await StatRemoteAsync(op.NewRemotePath!, ct).ConfigureAwait(false);
        if (moved is null || moved.IsDir != op.IsDir)
        {
            throw new RemoteException(RemoteErrorKind.Transient, $"Remote move to '{op.NewRemotePath}' not verified.");
        }

        List<BaseMutation> mutations = [new RebaseSubtree(op.Key, op.NewKey!, op.NewLocalPath!, op.NewRemotePath!)];
        if (!op.IsDir && op.Base is { } b && SameRemote(src, new RemoteEntry(b.Key, b.RemotePath, false, b.RemoteSize, b.RemoteMtimeUtc, b.RemoteETag)))
        {
            // The server may assign a new etag/mtime on MOVE: keep the base identity in sync for an unchanged file.
            var rebased = b.Rebase(b, op.NewKey!, op.NewLocalPath!, op.NewRemotePath!);
            mutations.Add(new UpsertBase(rebased with { RemoteETag = moved.ETag, RemoteMtimeUtc = moved.MtimeUtc, RemoteSize = moved.Size }));
        }
        else if (op.IsDir && op.Key != op.NewKey)
        {
            mutations.AddRange(await RefreshMovedSubtreeAsync(op, ct).ConfigureAwait(false));
        }

        return mutations;
    }

    /// <summary>
    /// Some servers assign new etags to everything under a moved collection. Files whose size and mtime are unchanged
    /// adopt the new etag, so the next cycle does not see them as remotely modified.
    /// </summary>
    private async Task<List<BaseMutation>> RefreshMovedSubtreeAsync(SyncOp op, CancellationToken ct)
    {
        var subtree = ctx.Store.GetSubtree(op.Key);
        var root = subtree.FirstOrDefault(e => e.Key == op.Key);
        if (root is null)
        {
            return [];
        }

        var crawl = await new RemoteScanner(ctx.Remote, ctx.Filter, ctx.Logger).CrawlAsync(ctx.RemoteRoot, op.NewRemotePath!, null, ct).ConfigureAwait(false);
        var result = new List<BaseMutation>();
        foreach (var e in subtree.Where(e => !e.IsDir))
        {
            var moved = e.Rebase(root, op.NewKey!, op.NewLocalPath!, op.NewRemotePath!);
            if (crawl.Entries.TryGetValue(moved.Key, out var r) && !r.IsDir && r.Size == moved.RemoteSize &&
                r.ETag != moved.RemoteETag && Mtime.Equal(r.MtimeUtc, moved.RemoteMtimeUtc))
            {
                result.Add(new UpsertBase(moved with { RemoteETag = r.ETag }));
            }
        }

        return result;
    }

    private RemoveBaseSubtree DeleteLocal(SyncOp op)
    {
        var path = op.LocalPath!;
        var actual = StatLocal(path);
        if (actual is null)
        {
            return new RemoveBaseSubtree(op.Key);
        }

        if (actual.IsDir != op.IsDir || (!op.IsDir && !LocalFs.SameFile(actual, op.Local)))
        {
            throw new StaleOpException($"'{path}' changed locally since planning.");
        }

        if (op.IsDir)
        {
            VerifyLocalSubtreeUnchanged(path);
        }

        var where = _trash.Trash(path);
        ctx.Logger.LogInformation("Local '{Path}' moved to {Where}", path, where);
        return new RemoveBaseSubtree(op.Key);
    }

    /// <summary>Invariant #2 for a recursive delete: every file below must still be unchanged vs base.</summary>
    private void VerifyLocalSubtreeUnchanged(string relativeDir)
    {
        var root = LocalFull(relativeDir);
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            var relative = PathKey.Combine(relativeDir, Path.GetRelativePath(root, file.FullName).Replace(Path.DirectorySeparatorChar, '/'));
            var key = PathKey.FromPath(relative);
            if (ctx.Filter.Check(key, file.Name) == FilterVerdict.Ignore)
            {
                continue;
            }

            var b = ctx.Store.Get(key);
            if (b is null || b.IsDir || b.LocalSize != file.Length || !Mtime.Equal(file.LastWriteTimeUtc, b.LocalMtimeUtc))
            {
                throw new StaleOpException($"Folder '{relativeDir}' contains new or changed '{relative}'; not deleted.");
            }
        }
    }

    private async Task<RemoveBaseSubtree> DeleteRemoteAsync(SyncOp op, CancellationToken ct)
    {
        var path = op.RemotePath!;
        var actual = await StatRemoteAsync(path, ct).ConfigureAwait(false);
        if (actual is null)
        {
            return new RemoveBaseSubtree(op.Key);
        }

        if (actual.IsDir != op.IsDir || (!op.IsDir && !SameRemote(actual, op.Remote)))
        {
            throw new StaleOpException($"Remote '{path}' changed since planning.");
        }

        if (op.IsDir)
        {
            await VerifyRemoteSubtreeUnchangedAsync(path, ct).ConfigureAwait(false);
        }

        await ctx.Remote.DeleteAsync(RemoteFull(path), ct).ConfigureAwait(false);
        if (await StatRemoteAsync(path, ct).ConfigureAwait(false) is not null)
        {
            throw new RemoteException(RemoteErrorKind.Transient, $"Remote delete of '{path}' not verified.");
        }

        return new RemoveBaseSubtree(op.Key);
    }

    /// <summary>DELETE of a collection is recursive: re-crawl it and require every file to be unchanged vs base.</summary>
    private async Task VerifyRemoteSubtreeUnchangedAsync(string relativeDir, CancellationToken ct)
    {
        var snapshot = await new RemoteScanner(ctx.Remote, ctx.Filter, ctx.Logger).CrawlAsync(ctx.RemoteRoot, relativeDir, null, ct).ConfigureAwait(false);
        if (!snapshot.ListedFolders.Contains(PathKey.FromPath(relativeDir)) ||
            snapshot.Entries.Values.Any(e => e.IsDir && !snapshot.ListedFolders.Contains(e.Key)) ||
            snapshot.SkippedKeys.Count > 0)
        {
            throw new StaleOpException($"Remote folder '{relativeDir}' could not be verified completely; not deleted.");
        }

        foreach (var entry in snapshot.Entries.Values.Where(e => !e.IsDir))
        {
            var b = ctx.Store.Get(entry.Key);
            var unchanged = b is { IsDir: false } && b.RemoteSize == entry.Size &&
                            (!b.RemoteConfirmed || SameRemote(entry, new RemoteEntry(b.Key, b.RemotePath, false, b.RemoteSize, b.RemoteMtimeUtc, b.RemoteETag)));
            if (!unchanged)
            {
                throw new StaleOpException($"Remote folder '{relativeDir}' contains new or changed '{entry.Path}'; not deleted.");
            }
        }
    }

    private async Task<(List<BaseMutation>, (string, string)?)> CompareAsync(SyncOp op, string opId, CancellationToken ct)
    {
        var local = StatLocal(op.LocalPath!);
        var remoteNow = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (!LocalFs.SameFile(local, op.Local) || !SameRemote(remoteNow, op.Remote))
        {
            throw new StaleOpException($"'{op.Key}' changed since planning.");
        }

        var temp = LocalFs.NewTempPath(ctx.State, opId);
        try
        {
            var remoteHash = await DownloadToTempAsync(op.RemotePath!, temp, remoteNow!, ct).ConfigureAwait(false);
            var localHash = op.Local!.Hash ?? await ContentHash.ComputeFileAsync(LocalFull(op.LocalPath!), ct).ConfigureAwait(false);
            if (localHash == remoteHash)
            {
                return ([new UpsertBase(Entry(op.Key, local! with { Hash = localHash }, remoteNow! with { Hash = remoteNow!.Hash ?? remoteHash }, localHash))], null);
            }

            return await ResolveConflictAsync(op, opId, (temp, remoteHash), ct).ConfigureAwait(false);
        }
        finally
        {
            LocalFs.TryDelete(temp);
        }
    }

    /// <summary>
    /// Keep both (§6.4): the loser's version gets the conflict name on its side, then each version is copied
    /// to the other side. <paramref name="downloaded"/> is the remote version already in a temp file (from Compare).
    /// </summary>
    private async Task<(List<BaseMutation>, (string, string)?)> ResolveConflictAsync(SyncOp op, string opId, (string Temp, ContentHash Hash)? downloaded, CancellationToken ct)
    {
        var local = StatLocal(op.LocalPath!);
        var remoteNow = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (!LocalFs.SameFile(local, op.Local) || !SameRemote(remoteNow, op.Remote))
        {
            throw new StaleOpException($"'{op.Key}' changed since planning.");
        }

        var copyKey = op.NewKey!;
        if (File.Exists(LocalFull(op.NewLocalPath!)) || await StatRemoteAsync(op.NewRemotePath!, ct).ConfigureAwait(false) is not null)
        {
            throw new StaleOpException($"Conflict copy name '{op.NewLocalPath}' is taken.");
        }

        var temp = downloaded?.Temp ?? LocalFs.NewTempPath(ctx.State, opId);
        try
        {
            if (op.Loser == Side.Remote)
            {
                // Local version keeps the name; the remote version becomes the conflict copy on both sides.
                var remoteHash = downloaded?.Hash ?? await DownloadToTempAsync(op.RemotePath!, temp, remoteNow!, ct).ConfigureAwait(false);
                await ctx.Remote.MoveAsync(RemoteFull(op.RemotePath!), RemoteFull(op.NewRemotePath!), false, ct).ConfigureAwait(false);
                var copyRemote = await StatRemoteAsync(op.NewRemotePath!, ct).ConfigureAwait(false) ??
                                 throw new RemoteException(RemoteErrorKind.Transient, $"Remote move to '{op.NewRemotePath}' not verified.");
                File.Move(temp, LocalFull(op.NewLocalPath!));
                var copyLocal = StatLocal(op.NewLocalPath!)!;
                var (after, remote, hash) = await PutFileAsync(op.LocalPath!, op.RemotePath!, local!, ct).ConfigureAwait(false);
                return (
                    [
                        new UpsertBase(Entry(copyKey, copyLocal with { Hash = remoteHash }, copyRemote with { Hash = copyRemote.Hash ?? remoteHash }, remoteHash)),
                        new UpsertBase(Entry(op.Key, after, remote, hash)),
                    ],
                    (op.NewLocalPath!, op.NewRemotePath!));
            }
            else
            {
                // Remote version keeps the name; the local version becomes the conflict copy on both sides.
                MoveLocalItem(op.LocalPath!, op.NewLocalPath!, false, opId);
                var movedLocal = StatLocal(op.NewLocalPath!)!;
                var (copyLocal, copyRemote, copyHash) = await PutFileAsync(op.NewLocalPath!, op.NewRemotePath!, movedLocal, ct).ConfigureAwait(false);
                var remoteHash = downloaded?.Hash ?? await DownloadToTempAsync(op.RemotePath!, temp, remoteNow!, ct).ConfigureAwait(false);
                File.Move(temp, LocalFull(op.LocalPath!));
                var after = StatLocal(op.LocalPath!)!;
                return (
                    [
                        new UpsertBase(Entry(copyKey, copyLocal, copyRemote, copyHash)),
                        new UpsertBase(Entry(op.Key, after with { Hash = remoteHash }, remoteNow! with { Hash = remoteNow!.Hash ?? remoteHash }, remoteHash)),
                    ],
                    (op.NewLocalPath!, op.NewRemotePath!));
            }
        }
        finally
        {
            LocalFs.TryDelete(temp);
        }
    }

    /// <summary>File vs folder (§6.2): the file is renamed to the conflict name on its side and copied over.</summary>
    private async Task<(List<BaseMutation>, (string, string)?)> FileVsFolderAsync(SyncOp op, string opId, CancellationToken ct)
    {
        if (op.Loser == Side.Local)
        {
            var file = StatLocal(op.LocalPath!);
            if (file is null || file.IsDir || !LocalFs.SameFile(file, op.Local))
            {
                throw new StaleOpException($"'{op.LocalPath}' changed locally since planning.");
            }

            MoveLocalItem(op.LocalPath!, op.NewLocalPath!, false, opId);
            var moved = StatLocal(op.NewLocalPath!)!;
            var (copyLocal, copyRemote, hash) = await PutFileAsync(op.NewLocalPath!, op.NewRemotePath!, moved, ct).ConfigureAwait(false);
            return ([new RemoveBaseSubtree(op.Key), new UpsertBase(Entry(op.NewKey!, copyLocal, copyRemote, hash))], (op.NewLocalPath!, op.NewRemotePath!));
        }

        var remote = await StatRemoteAsync(op.RemotePath!, ct).ConfigureAwait(false);
        if (remote is null || remote.IsDir || !SameRemote(remote, op.Remote))
        {
            throw new StaleOpException($"Remote '{op.RemotePath}' changed since planning.");
        }

        await ctx.Remote.MoveAsync(RemoteFull(op.RemotePath!), RemoteFull(op.NewRemotePath!), false, ct).ConfigureAwait(false);
        var copy = await StatRemoteAsync(op.NewRemotePath!, ct).ConfigureAwait(false) ??
                   throw new RemoteException(RemoteErrorKind.Transient, $"Remote move to '{op.NewRemotePath}' not verified.");
        var temp = LocalFs.NewTempPath(ctx.State, opId);
        try
        {
            var downloadedHash = await DownloadToTempAsync(op.NewRemotePath!, temp, copy, ct).ConfigureAwait(false);
            File.Move(temp, LocalFull(op.NewLocalPath!));
            var local = StatLocal(op.NewLocalPath!)!;
            return ([new RemoveBaseSubtree(op.Key), new UpsertBase(Entry(op.NewKey!, local with { Hash = downloadedHash }, copy with { Hash = copy.Hash ?? downloadedHash }, downloadedHash))], (op.NewLocalPath!, op.NewRemotePath!));
        }
        finally
        {
            LocalFs.TryDelete(temp);
        }
    }
}
