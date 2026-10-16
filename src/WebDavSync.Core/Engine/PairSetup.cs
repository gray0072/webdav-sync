using System.Text.Json;
using WebDavSync.Core.Config;
using WebDavSync.Core.Legacy;
using WebDavSync.Core.Local;
using WebDavSync.Core.State;

namespace WebDavSync.Core.Engine;

/// <summary>Pair creation and legacy import into the pair state (§15.2, §3.4).</summary>
public static class PairSetup
{
    /// <summary>Writes <c>.webdavsync\pair.json</c> and creates <c>state.db</c>. Refuses a folder that belongs to another pair (§13 #39).</summary>
    public static void InitializeLocal(PairSettings pair, bool resetState)
    {
        var state = new StatePaths(pair.LocalRoot);
        if (!Directory.Exists(state.LocalRoot))
        {
            throw new PairStoppedException($"Local root '{state.LocalRoot}' does not exist.");
        }

        var existing = state.ReadMarker();
        if (existing is not null && !string.Equals(existing.PairId, pair.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (!resetState)
            {
                throw new PairStoppedException($"'{state.LocalRoot}' already belongs to pair {existing.PairId} (copied folder?). Use --reset-state to start over in union mode.");
            }

            foreach (var file in Directory.EnumerateFiles(state.Dir, "state.db*"))
            {
                File.Delete(file);
            }
        }

        state.WriteMarker(new PairMarker
        {
            PairId = pair.Id,
            CreatedAtUtc = DateTime.UtcNow,
            RemoteUrl = pair.RemoteUrl,
            RemoteRoot = pair.RemoteRootNormalized,
            VolumeSerial = LocalFs.GetVolumeSerial(state.LocalRoot),
        });
        using var store = StateStore.Open(state.StateDb);
        store.SetMeta(MetaKeys.PairId, pair.Id);
    }

    /// <summary>
    /// Imports <c>.cloud.db</c> as the base. The imported baseline counts as a completed initial sync, so changes made
    /// since the legacy client stopped (including deletions) are propagated, under the safety guards (§7.4).
    /// Legacy ignored folders are added to <see cref="PairSettings.ExcludedFolders"/>; the caller saves the settings.
    /// </summary>
    public static LegacyImportResult ImportLegacy(PairSettings pair, bool force)
    {
        var state = new StatePaths(pair.LocalRoot);
        var cloudDb = Path.Combine(state.LocalRoot, LegacyImporter.DbFileName);
        if (!File.Exists(cloudDb))
        {
            throw new PairStoppedException($"'{cloudDb}' not found.");
        }

        using var lockFile = new FileStream(state.EngineLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var result = LegacyImporter.Import(cloudDb, state.TmpDir);
        using var store = StateStore.Open(state.StateDb);
        if (store.BaseCount > 0 && !force)
        {
            throw new PairStoppedException($"The pair already has {store.BaseCount} base entries; use --force to replace them.");
        }

        store.ReplaceBase(result.Base);
        store.InitialSyncDone = true;
        store.SetMeta(MetaKeys.LegacyImport, JsonSerializer.Serialize(new
        {
            At = DateTime.UtcNow,
            result.LocalFiles,
            result.LocalFolders,
            result.RemoteFiles,
            result.RemoteFolders,
            BaseEntries = result.Base.Count,
            result.IgnoredFolders,
        }));

        foreach (var folder in result.IgnoredFolders)
        {
            if (!pair.ExcludedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                pair.ExcludedFolders.Add(folder);
            }
        }

        return result;
    }
}
