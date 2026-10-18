using System.Globalization;
using WebDavSync.Core.Model;

namespace WebDavSync.Core.Remote.MailRu;

/// <summary>
/// Parses the metaserver's folder tree answer (opcode 117, §4.7) into per-folder listings. A folder's listing is used
/// only when the number of its listed children equals the child counts the server reports for it; everything else
/// (depth cut, mounted and shared folders, unexpected data) is left to per-folder listings.
/// </summary>
internal static class MailRuTreeParser
{
    public const ulong OptTotalSpace = 1;
    public const ulong OptChildCounts = 2;
    public const ulong OptFolderSize = 32;
    public const ulong OptUsedSpace = 64;

    /// <summary>The official client's option set (rclone) plus the child counts.</summary>
    public const ulong Options = OptTotalSpace | OptChildCounts | OptFolderSize | OptUsedSpace | 128 | 256;

    private const int StatusOk = 0;
    private const int StatusNotFound = 1;

    private const int OpEnd = 0;
    private const int OpItem = 1;
    private const int OpDescend = 2;
    private const int OpAscend = 3;
    private const int OpSkip = 15;

    private const int TypeMount = 0;
    private const int TypeFile = 1;
    private const int TypeFolder = 2;
    private const int TypeShared = 3;

    private const int TreeIdLength = 12;
    private const int NodeIdLength = 16;
    private const int HashLength = 20;

    public static byte[] Request(string path, ulong depth) =>
        new MailRuRequestWriter().UVar(117).String(path).UVar(depth).UVar(Options).UVar(0).ToArray();

    /// <summary>Returns <c>null</c> when <paramref name="path"/> does not exist.</summary>
    /// <param name="path">The requested absolute path; result paths start with it exactly as given.</param>
    /// <param name="hashKnown">Fill <see cref="RemoteItem.Hash"/> (as the WebDAV listing does with a hash property).</param>
    public static RemoteTree? Parse(byte[] data, string path, bool hashKnown, ulong options = Options)
    {
        var r = new MailRuReader(data);
        var status = r.Byte();
        if (status == StatusNotFound)
        {
            return null;
        }

        if (status != StatusOk)
        {
            throw new MailRuFormatException($"Folder tree status {status}.");
        }

        SkipRevision(r);
        if ((options & OptTotalSpace) != 0)
        {
            r.UVar();
        }

        if ((options & OptUsedSpace) != 0)
        {
            r.UVar();
        }

        var fingerprint = r.UVar();
        if (fingerprint > 1024)
        {
            throw new MailRuFormatException($"Fingerprint of {fingerprint} bytes.");
        }

        r.Bytes((int)fingerprint);

        Node? self = null;
        Node? current = null;
        Node? lastFolder = null;
        var stack = new Stack<Node?>();
        while (true)
        {
            var op = r.Byte();
            if (op == OpEnd)
            {
                break;
            }

            switch (op)
            {
                case OpItem:
                    var node = ReadItem(r, options);
                    if (current is null)
                    {
                        if (self is not null)
                        {
                            throw new MailRuFormatException("More than one top-level item.");
                        }

                        if (!string.Equals(node.Name, PathKey.Name(path.TrimEnd('/')), StringComparison.OrdinalIgnoreCase))
                        {
                            throw new MailRuFormatException($"Top-level item '{node.Name}' is not the requested '{path}'.");
                        }

                        node.Path = path;
                        self = node;
                    }
                    else
                    {
                        if (node.Name.Length == 0 || node.Name.Contains('/', StringComparison.Ordinal) || node.Name.Contains('\0', StringComparison.Ordinal))
                        {
                            throw new MailRuFormatException($"Invalid name '{node.Name}' in '{current.Path}'.");
                        }

                        node.Path = current.Path == "/" ? "/" + node.Name : current.Path + "/" + node.Name;
                        current.Children.Add(node);
                    }

                    if (node.Type != TypeFile)
                    {
                        lastFolder = node;
                    }

                    break;
                case OpDescend:
                    if (lastFolder is null || lastFolder.Descended)
                    {
                        throw new MailRuFormatException("Descend without a new folder.");
                    }

                    lastFolder.Descended = true;
                    stack.Push(current);
                    current = lastFolder;
                    break;
                case OpAscend:
                    // At the top level this is a no-op (as in rclone).
                    if (stack.Count > 0)
                    {
                        current = stack.Pop();
                    }

                    break;
                case OpSkip:
                    var count = r.UVar();
                    for (ulong i = 0; i < count; i++)
                    {
                        r.UVar();
                        r.UVar();
                    }

                    break;
                default:
                    throw new MailRuFormatException($"Unknown record {op} at byte {r.Position - 1}.");
            }
        }

        if (!r.AtEnd)
        {
            throw new MailRuFormatException($"{data.Length - r.Position} bytes after the end of the tree.");
        }

        if (self is null)
        {
            throw new MailRuFormatException("Empty tree.");
        }

        var listings = new Dictionary<string, RemoteListing>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        if (self.Type == TypeFolder)
        {
            Collect(self, hashKnown, listings, problems);
        }
        else
        {
            problems.Add($"'{path}' is not a regular folder.");
        }

        return new RemoteTree(listings, self.Type == TypeFolder ? self.Revision.ToString(CultureInfo.InvariantCulture) : null, problems);
    }

    private static void Collect(Node folder, bool hashKnown, Dictionary<string, RemoteListing> listings, List<string> problems)
    {
        var files = folder.Children.Count(c => c.Type == TypeFile);
        var folders = folder.Children.Count - files;
        if (folder.FolderCount is not { } expectedFolders || folder.FileCount is not { } expectedFiles)
        {
            problems.Add($"'{folder.Path}': no child counts.");
        }
        else if ((ulong)folders != expectedFolders || (ulong)files != expectedFiles)
        {
            problems.Add($"'{folder.Path}': {folders} folders and {files} files listed of {expectedFolders} and {expectedFiles}.");
        }
        else if (folder.Children.Count > 0 && !folder.Descended)
        {
            problems.Add($"'{folder.Path}': children not listed.");
        }
        else
        {
            listings[folder.Path] = new RemoteListing(folder.ToItem(hashKnown), folder.Children.Select(c => c.ToItem(hashKnown)).ToList(), true, []);
        }

        foreach (var child in folder.Children)
        {
            if (child.Type == TypeFolder)
            {
                Collect(child, hashKnown, listings, problems);
            }
            else if (child.Type != TypeFile)
            {
                problems.Add($"'{child.Path}': {(child.Type == TypeMount ? "mounted" : "shared")} folder, listed separately.");
            }
        }
    }

    private static void SkipRevision(MailRuReader r)
    {
        var version = r.Byte();
        switch (version)
        {
            case 0:
                break;
            case 1 or 2:
                r.Bytes(TreeIdLength);
                r.UVar();
                break;
            case 3 or 4:
                r.Bytes(TreeIdLength);
                r.UVar();
                r.Bytes(TreeIdLength);
                r.UVar();
                break;
            case 5:
                r.Bytes(TreeIdLength);
                r.UVar();
                r.Bytes(TreeIdLength);
                break;
            default:
                throw new MailRuFormatException($"Unknown revision format {version}.");
        }
    }

    private static Node ReadItem(MailRuReader r, ulong options)
    {
        var head = r.UInt16();
        var type = head & 3;
        if ((head & 0x1000) != 0)
        {
            r.Bytes(NodeIdLength);
        }

        // One byte: names are at most 255 UTF-8 bytes (the cloud rejects longer ones); rclone's varint is wrong here.
        var name = r.Utf8(r.Byte());
        var node = new Node(name, type) { Revision = r.UVar() };
        switch (type)
        {
            case TypeFile:
                var mtime = r.UVar();
                var size = r.UVar();
                node.Size = size <= long.MaxValue ? (long)size : throw new MailRuFormatException($"Invalid size {size}.");
                node.Hash = Convert.ToHexString(r.Bytes(HashLength));
                node.MtimeUtc = mtime <= 253_402_300_799 ? DateTime.UnixEpoch.AddSeconds(mtime) : throw new MailRuFormatException($"Invalid mtime {mtime}.");
                return node;
            case TypeFolder:
                r.UVar();
                break;
            case TypeMount:
                r.Bytes(TreeIdLength);
                r.UVar();
                r.UVar();
                break;
            case TypeShared:
                r.UVar();
                r.Bytes(TreeIdLength);
                break;
        }

        if ((options & OptChildCounts) != 0)
        {
            node.FolderCount = r.UVar();
            node.FileCount = r.UVar();
        }

        if ((options & OptFolderSize) != 0)
        {
            r.UVar();
        }

        return node;
    }

    private sealed class Node(string name, int type)
    {
        public string Name { get; } = name;

        public int Type { get; } = type;

        public string Path { get; set; } = string.Empty;

        /// <summary>Folders: the subtree revision (<c>grev</c>), the change token.</summary>
        public ulong Revision { get; init; }

        public long Size { get; set; }

        public DateTime? MtimeUtc { get; set; }

        public string? Hash { get; set; }

        public ulong? FolderCount { get; set; }

        public ulong? FileCount { get; set; }

        public bool Descended { get; set; }

        public List<Node> Children { get; } = [];

        /// <summary>The same fields the WebDAV listing gives on Mail.ru: <c>getetag</c> is the content hash, folders have neither etag nor mtime.</summary>
        public RemoteItem ToItem(bool hashKnown) => Type == TypeFile
            ? new RemoteItem(Path, false, Size, MtimeUtc, Hash, hashKnown ? ContentHash.Parse(Hash!) : null, null)
            : new RemoteItem(Path, true, 0, null, null, null, null);
    }
}
