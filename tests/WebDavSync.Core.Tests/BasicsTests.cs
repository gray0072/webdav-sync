using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WebDavSync.Core.Config;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Tests;

public sealed class ContentHashTests
{
    [Fact]
    public void Empty_file_hash_is_forty_zeros() =>
        Assert.Equal(new string('0', 40), ContentHash.Compute([]).Hex);

    [Fact]
    public void Small_files_are_zero_padded_hex()
    {
        Assert.Equal("616263" + new string('0', 34), ContentHash.Compute("abc"u8).Hex);
        var twenty = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        Assert.Equal(Convert.ToHexString(twenty), ContentHash.Compute(twenty).Hex);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(100_000)]
    public void Large_files_use_salted_sha1_with_size_suffix(int size)
    {
        var content = Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray();
        var expected = SHA1.HashData([.. "mrCloud"u8.ToArray(), .. content, .. Encoding.ASCII.GetBytes(size.ToString(CultureInfo.InvariantCulture))]);
        Assert.Equal(Convert.ToHexString(expected), ContentHash.Compute(content).Hex);
    }

    [Fact]
    public async Task Streaming_in_chunks_matches_one_shot()
    {
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        using var hasher = new ContentHasher();
        foreach (var chunk in content.Chunk(7))
        {
            hasher.Append(chunk);
        }

        var streamed = await ContentHash.ComputeAsync(new MemoryStream(content), CancellationToken.None);
        Assert.Equal(ContentHash.Compute(content), hasher.Finish());
        Assert.Equal(ContentHash.Compute(content), streamed);
    }

    [Fact]
    public void Parse_normalizes_case_and_rejects_garbage()
    {
        Assert.Equal(new string('A', 40), ContentHash.Parse(new string('a', 40)).Hex);
        Assert.False(ContentHash.TryParse("xyz", out _));
        Assert.Null(ContentHash.ParseOrNull(new string('G', 40)));
    }

    [Fact]
    public async Task File_hash_is_the_same_with_low_io_priority()
    {
        using var temp = new Support.TempDir();
        var content = new string('x', 100_000);
        var path = temp.File("big.bin", content);
        Local.LocalFs.LowIoPriority = true;
        try
        {
            Assert.Equal(ContentHash.Compute(Encoding.UTF8.GetBytes(content)), await ContentHash.ComputeFileAsync(path, CancellationToken.None));
        }
        finally
        {
            Local.LocalFs.LowIoPriority = false;
        }
    }
}

public sealed class PathKeyTests
{
    [Fact]
    public void Key_is_nfc_lowercase()
    {
        Assert.Equal(PathKey.FromPath("Café/Файл.TXT"), PathKey.FromPath("Café/ФАЙЛ.txt"));
        Assert.Equal("café/файл.txt", PathKey.FromPath("Café/ФАЙЛ.txt"));
    }

    [Fact]
    public void Path_helpers()
    {
        Assert.Equal("a/b", PathKey.Parent("a/b/c"));
        Assert.Equal(string.Empty, PathKey.Parent("a"));
        Assert.Equal("c", PathKey.Name("a/b/c"));
        Assert.True(PathKey.IsUnder("a/b", "a"));
        Assert.False(PathKey.IsUnder("ab", "a"));
        Assert.False(PathKey.IsUnder("a", "a"));
        Assert.True(PathKey.IsUnder("a", string.Empty));
        Assert.Equal("x/y/c", PathKey.Rebase("a/b/c", "a/b", "x/y"));
        Assert.Equal("x", PathKey.Rebase("a", "a", "x"));
        Assert.Equal(3, PathKey.Depth("a/b/c"));
        Assert.Equal("/root/a b", PathKey.ToRemoteFull("/root/", "a b"));
        Assert.Equal("/", PathKey.ToRemoteFull("/", string.Empty));
    }

    [Fact]
    public void Mtime_equality_tolerates_second_precision_of_one_side()
    {
        var precise = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        Assert.True(Mtime.Equal(precise, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.False(Mtime.Equal(precise, precise.AddTicks(1)));
        Assert.False(Mtime.Equal(precise, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc)));
    }
}

public sealed class NameRulesTests
{
    [Theory]
    [InlineData("a<b")]
    [InlineData("a:b")]
    [InlineData("a|b")]
    [InlineData("a?")]
    [InlineData("star*")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("LPT9.log")]
    [InlineData("tab\tname")]
    public void Edge2_Invalid_windows_names_are_reported(string name) =>
        Assert.NotNull(NameRules.WindowsInvalidReason(name));

    [Theory]
    [InlineData("normal.txt")]
    [InlineData("Отчёт 2026 #1 + 50%.docx")]
    [InlineData("CONSOLE.txt")]
    [InlineData(".hidden")]
    public void Edge6_Valid_names_pass(string name) =>
        Assert.Null(NameRules.WindowsInvalidReason(name));
}

public sealed class SyncFilterTests
{
    [Theory]
    [InlineData(".webdavsync")]
    [InlineData(".cloud.db")]
    [InlineData(".cloud_ss")]
    [InlineData(".cloud.db-journal")]
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData("~$report.docx")]
    [InlineData("$RECYCLE.BIN")]
    [InlineData("System Volume Information")]
    public void Built_in_ignore_list(string name) =>
        Assert.Equal(FilterVerdict.Ignore, SyncFilter.Empty.Check(PathKey.FromPath("x/" + name), name));

    [Fact]
    public void Excluded_folders_and_patterns()
    {
        var filter = new SyncFilter(["Photos", "Docs/Private"], ["*.bak", "build/*"]);
        Assert.Equal(FilterVerdict.Exclude, filter.Check("photos", "Photos"));
        Assert.Equal(FilterVerdict.Exclude, filter.Check("photos/a.jpg", "a.jpg"));
        Assert.Equal(FilterVerdict.Exclude, filter.Check("docs/private/x", "x"));
        Assert.Equal(FilterVerdict.Include, filter.Check("docs/public", "public"));
        Assert.Equal(FilterVerdict.Exclude, filter.Check("a/b/old.BAK", "old.BAK"));
        Assert.Equal(FilterVerdict.Exclude, filter.Check("build/out.dll", "out.dll"));
        Assert.Equal(FilterVerdict.Include, filter.Check("src/build/out.dll", "out.dll"));
        Assert.False(filter.IsSynced("photos/2026/a.jpg"));
        Assert.True(filter.IsSynced("music/a.mp3"));
    }
}
