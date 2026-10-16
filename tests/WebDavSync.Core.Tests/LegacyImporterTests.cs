using WebDavSync.Core.Legacy;
using WebDavSync.Core.Model;
using WebDavSync.Core.Tests.Support;

namespace WebDavSync.Core.Tests;

/// <summary>Importer of <c>.cloud.db</c> (§3) on a fixture DB (§19.5 #8).</summary>
public sealed class LegacyImporterTests
{
    private static readonly string HashA = World.Hash("content A").Hex;
    private static readonly string HashB = World.Hash("content B").Hex;

    [Fact]
    public void Builds_both_trees_and_a_base_from_identical_items()
    {
        using var temp = new TempDir();
        var f = new LegacyFixture();
        var lDocs = f.Folder(f.LocalRoot, "Docs", inode: 500);
        f.File(lDocs, "same.txt", 9, HashA, inode: 501);
        f.File(lDocs, "differs.txt", 9, HashA, inode: 502);
        f.File(f.LocalRoot, "local-only.txt", 9, HashA);
        var lPhotos = f.Folder(f.LocalRoot, "Photos", ignore: true);
        f.File(lPhotos, "Отпуск.jpg", 9, HashB, inode: 503);

        var rDocs = f.Folder(f.RemoteRoot, "Docs", inode: 500, tree: 2);
        f.File(rDocs, "same.txt", 9, HashA, time: 1_767_000_000, inode: 501);
        f.File(rDocs, "differs.txt", 9, HashB);
        f.File(f.RemoteRoot, "remote-only.txt", 9, HashA);
        var rPhotos = f.Folder(f.RemoteRoot, "Photos", tree: 2);
        f.File(rPhotos, "Отпуск.jpg", 9, HashB);

        var db = Path.Combine(temp.Path, ".cloud.db");
        f.Write(db);
        var before = File.ReadAllBytes(db);

        var result = LegacyImporter.Import(db, Path.Combine(temp.Path, "tmp"));

        Assert.Equal(before, File.ReadAllBytes(db));
        Assert.Equal(4, result.LocalFiles);
        Assert.Equal(2, result.LocalFolders);
        Assert.Equal(4, result.RemoteFiles);
        Assert.Equal("0518", result.GeneralInfo["db_version"]);
        Assert.Equal(["Photos"], result.IgnoredFolders);

        var keys = result.Base.Select(b => b.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["docs", "docs/same.txt", "photos", "photos/отпуск.jpg"], keys);
        Assert.Equal(1, result.BothDifferent);
        Assert.Equal(1, result.LocalOnly);
        Assert.Equal(1, result.RemoteOnly);

        var same = result.Base.Single(b => b.Key == "docs/same.txt");
        Assert.Equal("Docs/same.txt", same.LocalPath);
        Assert.Equal(501UL, same.LocalFileId);
        Assert.Equal(Mtime.FromUnixSeconds(1_767_261_600), same.LocalMtimeUtc);
        Assert.Equal(Mtime.FromUnixSeconds(1_767_000_000), same.RemoteMtimeUtc);
        Assert.Equal(HashA, same.LocalHash?.Hex);
        Assert.False(same.RemoteConfirmed);
        Assert.Null(same.RemoteETag);
    }

    [Fact]
    public void Mapping_name_is_preferred_for_the_local_name()
    {
        using var temp = new TempDir();
        var f = new LegacyFixture();
        f.File(f.LocalRoot, "stored", 9, HashA, mapping: "Mapped.txt");
        f.File(f.RemoteRoot, "Mapped.txt", 9, HashA);
        var db = Path.Combine(temp.Path, ".cloud.db");
        f.Write(db);

        var result = LegacyImporter.Import(db, Path.Combine(temp.Path, "tmp"));
        Assert.Equal("Mapped.txt", Assert.Single(result.Base).LocalPath);
    }

    [Fact]
    public void Nfd_names_match_nfc_keys()
    {
        using var temp = new TempDir();
        var f = new LegacyFixture();
        f.File(f.LocalRoot, "café.txt", 9, HashA);
        f.File(f.RemoteRoot, "café.txt", 9, HashA);
        var db = Path.Combine(temp.Path, ".cloud.db");
        f.Write(db);

        var entry = Assert.Single(LegacyImporter.Import(db, Path.Combine(temp.Path, "tmp")).Base);
        Assert.Equal("café.txt", entry.LocalPath);
        Assert.Equal("café.txt", entry.RemotePath);
    }
}
