using System.Text;
using WebDavSync.Core.Remote.MailRu;

namespace WebDavSync.Core.Tests;

/// <summary>Mail.ru folder tree answer (opcode 117, §4.7): format and the per-folder completeness check.</summary>
public sealed class MailRuTreeParserTests
{
    /// <summary>
    /// A real answer (test account, 2026-10-18) for <c>/wds-fmt</c> with depth 2: folder <c>l0/l1</c> is cut by the depth,
    /// one name is 254 UTF-8 bytes (its length is one byte, not a varint).
    /// </summary>
    private const string RealAnswer =
        "0001386461336430643030303030F1ED018080808020AB01143B855E6950854C1D13290A366DEE040AB61AEF0C0102000777" +
        "64732D666D74EFED01EFED0101047202010200026C30EBED01C4ED0101000302010200026C31EBED01C5ED01010003030101" +
        "00087A3130302E62696EEEED018081E49206643E339CFFBE92C2317735BA5ECB17967D1266F58E010100A8D09ED187D0B5D0" +
        "BDD18C20D0B4D0BBD0B8D0BDD0BDD0BED0B520D0B8D0BCD18F20D184D0B0D0B9D0BBD0B020D0B4D0BBD18F20D0BFD180D0BE" +
        "D0B2D0B5D180D0BAD0B820D0BAD0BED0B4D0B8D180D0BED0B2D0B0D0BDD0B8D18F20D0B4D0BBD0B8D0BDD18B20D0B8D0BCD0" +
        "B5D0BDD0B820D0B220D0B1D0B8D0BDD0B0D180D0BDD0BED0BC20D0BFD180D0BED182D0BED0BAD0BED0BBD0B520D0BED0B1D0" +
        "BBD0B0D0BAD0B02E747874ECED0180A0F8FA05056C6F6E6721000000000000000000000000000000010100FED0AFD0AFD0AF" +
        "D0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AF" +
        "D0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AF" +
        "D0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AF" +
        "D0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AF" +
        "D0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AFD0AF2E747874EFED" +
        "01BDB5D2D6060178000000000000000000000000000000000000000101000D6120622B63256423652E747874EDED0180E2CF" +
        "AA060563686172730000000000000000000000000000000300";

    [Fact]
    public void Real_answer_gives_complete_listings_and_leaves_the_depth_cut_folder_out()
    {
        var tree = MailRuTreeParser.Parse(Convert.FromHexString(RealAnswer), "/wds-fmt", hashKnown: true)!;

        Assert.Equal("30447", tree.ChangeToken);
        Assert.Equal(["/wds-fmt", "/wds-fmt/l0"], tree.Listings.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(tree.Problems, p => p.Contains("'/wds-fmt/l0/l1'", StringComparison.Ordinal));

        var root = tree.Listings["/wds-fmt"];
        Assert.True(root.Complete);
        Assert.Equal("/wds-fmt", root.Self.Path);
        Assert.True(root.Self.IsDir);
        Assert.Equal(5, root.Children.Count);

        var folder = Assert.Single(root.Children, c => c.IsDir);
        Assert.Equal("/wds-fmt/l0", folder.Path);
        Assert.Null(folder.ETag);
        Assert.Null(folder.MtimeUtc);

        var file = Assert.Single(root.Children, c => c.Name == "z100.bin");
        Assert.Equal(100, file.Size);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_650_000_000), file.MtimeUtc);
        Assert.Equal("3E339CFFBE92C2317735BA5ECB17967D1266F58E", file.ETag);
        Assert.Equal(file.ETag, file.Hash!.Value.Hex);

        var longName = new string('Я', 125) + ".txt";
        Assert.Equal(254, Encoding.UTF8.GetByteCount(longName));
        Assert.Contains(root.Children, c => c.Path == "/wds-fmt/" + longName && c.Size == 1);
        Assert.Contains(root.Children, c => c.Name == "a b+c%d#e.txt");
        Assert.Contains(root.Children, c => c.Name.StartsWith("Очень длинное имя", StringComparison.Ordinal));

        Assert.Equal(["/wds-fmt/l0/l1"], tree.Listings["/wds-fmt/l0"].Children.Select(c => c.Path));
    }

    [Fact]
    public void Without_a_hash_property_the_hash_stays_unknown_like_in_the_webdav_listing()
    {
        var tree = MailRuTreeParser.Parse(Convert.FromHexString(RealAnswer), "/wds-fmt", hashKnown: false)!;

        var file = Assert.Single(tree.Listings["/wds-fmt"].Children, c => c.Name == "z100.bin");
        Assert.Null(file.Hash);
        Assert.Equal("3E339CFFBE92C2317735BA5ECB17967D1266F58E", file.ETag);
    }

    [Fact]
    public void Request_is_encoded_as_the_official_client_does()
    {
        Assert.Equal("75032F6100AC02E30300", Convert.ToHexString(MailRuTreeParser.Request("/a", 300)));
    }

    [Fact]
    public void Missing_folder_gives_null()
    {
        Assert.Null(MailRuTreeParser.Parse([1], "/gone", hashKnown: true));
    }

    [Fact]
    public void Root_of_the_account_has_an_empty_name()
    {
        var data = new Answer().Folder("", 7, folders: 0, files: 1).Down().File("a.txt", 1_600_000_000, 3).Up().End();

        var tree = MailRuTreeParser.Parse(data, "/", hashKnown: true)!;

        Assert.Equal("7", tree.ChangeToken);
        Assert.Equal("/a.txt", Assert.Single(tree.Listings["/"].Children).Path);
    }

    [Fact]
    public void Child_counts_that_do_not_match_leave_only_that_folder_out()
    {
        var data = new Answer()
            .Folder("r", 9, folders: 2, files: 0).Down()
            .Folder("good", 5, folders: 0, files: 1).Down().File("x", 1, 1).Up()
            .Folder("bad", 6, folders: 0, files: 2).Down().File("y", 1, 1).Up()
            .Up().End();

        var tree = MailRuTreeParser.Parse(data, "/r", hashKnown: true)!;

        Assert.Equal(["/r", "/r/good"], tree.Listings.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(tree.Problems, p => p.Contains("'/r/bad'", StringComparison.Ordinal));
    }

    [Fact]
    public void Mounted_and_shared_folders_are_listed_in_their_parent_but_left_to_webdav()
    {
        var data = new Answer()
            .Folder("r", 9, folders: 2, files: 0).Down()
            .Mount("m").Shared("s")
            .Up().End();

        var tree = MailRuTreeParser.Parse(data, "/r", hashKnown: true)!;

        var listing = Assert.Single(tree.Listings).Value;
        Assert.Equal(["/r/m", "/r/s"], listing.Children.Select(c => c.Path).Order(StringComparer.Ordinal));
        Assert.All(listing.Children, c => Assert.True(c.IsDir));
        Assert.Equal(2, tree.Problems.Count);
    }

    [Fact]
    public void Folder_with_children_that_were_not_descended_into_is_incomplete()
    {
        var data = new Answer().Folder("r", 9, folders: 0, files: 3).End();

        var tree = MailRuTreeParser.Parse(data, "/r", hashKnown: true)!;

        Assert.Empty(tree.Listings);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("wrong name")]
    [InlineData("two roots")]
    [InlineData("descend into a file")]
    [InlineData("unknown record")]
    [InlineData("bad status")]
    public void Malformed_answers_are_rejected(string what)
    {
        var good = new Answer().Folder("r", 1, folders: 0, files: 1).Down().File("x", 1, 1).Up().End();
        var data = what switch
        {
            "truncated" => good[..^5],
            "trailing" => [.. good, 0],
            "wrong name" => new Answer().Folder("other", 1, 0, 0).End(),
            "two roots" => new Answer().Folder("r", 1, 0, 0).Folder("r", 1, 0, 0).End(),
            "descend into a file" => new Answer().Folder("r", 1, 0, 1).Down().File("x", 1, 1).Down().End(),
            "unknown record" => [.. good[..^1], 9, 0],
            _ => [12],
        };

        Assert.Throws<MailRuFormatException>(() => MailRuTreeParser.Parse(data, "/r", hashKnown: true));
    }

    /// <summary>Builds answers with <see cref="MailRuTreeParser.Options"/>: revision v1, total and used space, empty fingerprint.</summary>
    private sealed class Answer
    {
        private readonly MemoryStream _data = new();

        public Answer()
        {
            _data.WriteByte(0);
            _data.WriteByte(1);
            _data.Write(new byte[12]);
            UVar(5).UVar(8UL << 30).UVar(1000).UVar(0);
        }

        public Answer Folder(string name, ulong grev, ulong folders, ulong files) =>
            Item(2, name, grev).UVar(grev).UVar(folders).UVar(files).UVar(0);

        public Answer Mount(string name) => Item(0, name, 1).Raw(new byte[12]).UVar(1).UVar(1).UVar(0).UVar(0).UVar(0);

        public Answer Shared(string name) => Item(3, name, 1).UVar(1).Raw(new byte[12]).UVar(0).UVar(0).UVar(0);

        public Answer File(string name, ulong mtime, ulong size) =>
            Item(1, name, 1).UVar(mtime).UVar(size).Raw(Enumerable.Repeat((byte)0xAB, 20).ToArray());

        public Answer Down() => Raw([2]);

        public Answer Up() => Raw([3]);

        public byte[] End()
        {
            _data.WriteByte(0);
            return _data.ToArray();
        }

        private Answer Item(ushort type, string name, ulong revision)
        {
            var bytes = Encoding.UTF8.GetBytes(name);
            _data.WriteByte(1);
            _data.Write(BitConverter.GetBytes(type));
            _data.WriteByte((byte)bytes.Length);
            _data.Write(bytes);
            return UVar(revision);
        }

        private Answer UVar(ulong value)
        {
            _data.Write(new MailRuRequestWriter().UVar(value).ToArray());
            return this;
        }

        private Answer Raw(byte[] bytes)
        {
            _data.Write(bytes);
            return this;
        }
    }
}
