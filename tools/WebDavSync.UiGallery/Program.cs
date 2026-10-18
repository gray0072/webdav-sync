using System.Globalization;
using System.Runtime.InteropServices;
using WebDavSync.App;
using WebDavSync.Core.Config;
using WebDavSync.Core.Engine;
using WebDavSync.Core.Execution;
using WebDavSync.Core.Model;
using WebDavSync.Core.Planning;
using WebDavSync.Core.Remote;
using WebDavSync.Core.Updates;

namespace WebDavSync.UiGallery;

/// <summary>
/// Development tool, not shipped: opens every window of the tray app with sample data and saves a PNG of each, to
/// check layouts at the current display scaling (and at 100 % with <c>--dpi-unaware</c>, larger text with
/// <c>--font-scale</c>). Usage: <c>WebDavSync.UiGallery &lt;output dir&gt; [--dpi-unaware] [--font-scale 1.5]</c>.
/// </summary>
internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            MessageBox.Show("Usage: WebDavSync.UiGallery <output dir> [--dpi-unaware] [--font-scale 1.5]");
            return 1;
        }

        var output = Directory.CreateDirectory(args[0]).FullName;
        if (args.Contains("--test-swap"))
        {
            // Run from a disposable copy only: downloads the latest release, swaps it in for this exe and starts it.
            using var http = new HttpClient();
            var latest = new UpdateChecker(http).GetLatestAsync(CancellationToken.None).GetAwaiter().GetResult()!;
            var exe = Updater.DownloadAsync(http, latest, new Progress<string>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            Updater.SwapAndRestart(exe, Path.Combine(output, "data"));
            return 0;
        }

        if (args.Contains("--test-update"))
        {
            // Real check against GitHub and a real download + checksum + extraction (the swap itself is not done).
            return TestUpdateAsync(output).GetAwaiter().GetResult();
        }

        Application.SetHighDpiMode(args.Contains("--dpi-unaware") ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var scale = Array.IndexOf(args, "--font-scale") is var i and >= 0 ? float.Parse(args[i + 1], CultureInfo.InvariantCulture) : 1f;
        ScaledForm.FontScale = scale;

        var context = new ApplicationContext();
        var timer = new System.Windows.Forms.Timer { Interval = 10 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try
            {
                await CaptureAllAsync(output);
            }
            finally
            {
                context.ExitThread();
            }
        };
        timer.Start();
        Application.Run(context);
        return 0;
    }

    private static async Task<int> TestUpdateAsync(string output)
    {
        using var http = new HttpClient();
        var lines = new List<string> { $"running {WebDavSync.Core.Updates.UpdateChecker.CurrentDescription}" };
        try
        {
            var latest = await new WebDavSync.Core.Updates.UpdateChecker(http).GetLatestAsync(CancellationToken.None);
            lines.Add($"latest {latest?.Version} published {latest?.PublishedUtc:u} zip {latest?.ZipUrl}");
            var exe = await Updater.DownloadAsync(http, latest!, new Progress<string>(m => lines.Add("  " + m)), CancellationToken.None);
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            lines.Add($"extracted {exe}: {new FileInfo(exe).Length} bytes, product version {info.ProductVersion}");
            File.Delete(exe);
            return 0;
        }
        catch (Exception ex)
        {
            lines.Add("FAILED: " + ex);
            return 1;
        }
        finally
        {
            File.WriteAllLines(Path.Combine(output, "update.txt"), lines);
        }
    }

    private static async Task CaptureAllAsync(string output)
    {
        var temp = Directory.CreateTempSubdirectory("wds-gallery-");
        var local = Directory.CreateDirectory(Path.Combine(temp.FullName, "Cloud")).FullName;
        File.WriteAllText(Path.Combine(local, "notes.txt"), "sample");
        Directory.CreateDirectory(Path.Combine(local, "Documents"));
        Directory.CreateDirectory(Path.Combine(local, "Local only folder"));
        var pair = new PairSettings { Name = "cloud", LocalRoot = local, RemoteUrl = "https://webdav.cloud.mail.ru/", User = "you@mail.ru", ExcludedFolders = ["Photos"] };
        var remote = new SampleRemote();

        using var host = new AppHost(new AppPaths(Path.Combine(temp.FullName, "data")));
        await CaptureAsync(output, "approvals", () =>
        {
            var form = new ApprovalsForm(pair, new SampleApprovals());
            form.Reload();
            return form;
        });
        await CaptureAsync(output, "folders", () => new FoldersForm(pair, remote));
        await CaptureAsync(output, "cloud-folder", () => new RemoteFolderDialog(remote));
        await CaptureAsync(output, "password", () => new PasswordForm("App password for you@mail.ru at https://webdav.cloud.mail.ru/"));
        var notes = string.Join('\n', Enumerable.Range(1, 12).Select(i => $"- Change number {i}: something useful was improved in this release."));
        await CaptureAsync(output, "update", () => new UpdateForm(
            new ReleaseInfo(new Version(9, 9, 9), "v9.9.9", DateTime.UtcNow, "https://example.org", "**What's new**\n\n" + notes, "https://example.org/a.zip", "https://example.org/a.zip.sha256"),
            new HttpClient()));
        for (var page = 0; page < 3; page++)
        {
            var index = page;
            await CaptureAsync(output, $"wizard-{index + 1}", () =>
            {
                var wizard = new SetupWizard(host, firstRun: true);
                wizard.PreviewPage(index, local);
                return wizard;
            });
        }

        temp.Delete(recursive: true);
    }

    private static async Task CaptureAsync(string output, string name, Func<Form> create)
    {
        using var form = create();
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(40, 40);
        form.Show();
        await Task.Delay(900);
        GetWindowRect(form.Handle, out var rect);
        using var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, 2);
            g.ReleaseHdc(hdc);
        }

        bitmap.Save(Path.Combine(output, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);

        // Layout check: every visible control must fit into its parent's client area (scrolling panels excepted).
        var report = new List<string> { $"{name}: window {form.Bounds.Size}, client {form.ClientSize}, DPI {form.DeviceDpi}" };
        Check(form, report);
        File.AppendAllLines(Path.Combine(output, "layout.txt"), report);
        form.Close();
    }

    private static void Check(Control parent, List<string> report)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            var area = parent.ClientRectangle;
            if (parent is not ScrollableControl { AutoScroll: true } && (child.Right > area.Right + 1 || child.Bottom > area.Bottom + 1 || child.Left < -1 || child.Top < -1))
            {
                report.Add($"  OVERFLOW {Describe(child)} {child.Bounds} in {Describe(parent)} client {area.Size}");
            }

            if (child is ListView list)
            {
                var columns = list.Columns.Cast<ColumnHeader>().Sum(c => c.Width);
                report.Add($"  list columns {columns} px in client {list.ClientSize.Width} px: {string.Join(", ", list.Columns.Cast<ColumnHeader>().Select(c => $"{c.Text}={c.Width}"))}");
            }

            Check(child, report);
        }
    }

    private static string Describe(Control c) => $"{c.GetType().Name}{(string.IsNullOrEmpty(c.Text) ? string.Empty : $" '{(c.Text.Length > 30 ? c.Text[..30] + "…" : c.Text)}'")}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);

    /// <summary>A pending approval with every kind of op, long paths and a guard trip.</summary>
    private sealed class SampleApprovals : IApprovalSource
    {
        public PendingApproval? Pending { get; } = Create();

        public SyncMode Mode => SyncMode.ApproveAll;

        public Task<ExecutionResult?> ApproveAsync(IReadOnlyCollection<SyncOp> ops, bool acceptGuards) => Task.FromResult<ExecutionResult?>(null);

        public Task RejectAsync(IReadOnlyCollection<SyncOp> ops) => Task.CompletedTask;

        private static PendingApproval Create()
        {
            var now = DateTime.UtcNow;
            SyncOp Op(OpType type, string path, string reason, long size = 0, bool overwrite = false) => new()
            {
                Type = type,
                Key = PathKey.FromPath(path),
                IsDir = size < 0,
                Reason = reason,
                LocalPath = path,
                RemotePath = path,
                Local = new LocalEntry(PathKey.FromPath(path), path, false, Math.Max(size, 0), now, 1),
                Remote = new RemoteEntry(PathKey.FromPath(path), path, false, Math.Max(size, 0), now, "e"),
                Overwrite = overwrite,
            };
            var ops = new List<SyncOp>
            {
                Op(OpType.Download, "Photos/2026/Summer holiday in Lapland/IMG_20260714_183015.jpg", "remote new", 4_812_345),
                Op(OpType.Upload, "Documents/Taxes/2025/Deklaration och kvitton (slutlig version).pdf", "local new", 1_234_567),
                Op(OpType.Upload, "Work/Reports/Квартальный отчёт Q3 — финальная версия для руководства.docx", "local modified", 845_000, overwrite: true),
                Op(OpType.DeleteRemote, "Old projects/2019", "local deleted", -1) with { Affected = 312 },
                Op(OpType.Conflict, "Shared/plan.xlsx", "modified on both sides", 52_000),
                Op(OpType.MkdirLocal, "Music/Новые альбомы", "remote new folder", -1),
            };
            for (var i = 0; i < 30; i++)
            {
                ops.Add(Op(OpType.Download, $"Camera Uploads/2026-10/IMG_{i:0000}.heic", "remote new", 2_000_000 + i));
            }

            var plan = new Plan(ops, []);
            var guards = new GuardReport(["Mass delete: 312 items (7.1 % of 4 400 tracked) exceed the limits (50 items / 5 %)."], []);
            var cycle = new CyclePlan("sample", plan, guards, new LocalSnapshot(new Dictionary<string, LocalEntry>(), new HashSet<string>()),
                new RemoteSnapshot(new Dictionary<string, RemoteEntry>(), new HashSet<string>()), 4400, false, false, false, []);
            return new PendingApproval(cycle, ops, true, now);
        }
    }

    /// <summary>A cloud with a few folders, for the folder dialogs.</summary>
    private sealed class SampleRemote : IRemoteStore
    {
        private static readonly string[] Top = ["Camera Uploads", "Documents", "Music", "Photos", "Shared", "Work", "Документы", "Фото с телефона"];

        public Task<RemoteListing> ListAsync(string path, CancellationToken ct)
        {
            var dir = path.TrimEnd('/');
            var names = dir.Length == 0 ? Top : ["2024", "2025", "2026"];
            var children = names.Select(n => new RemoteItem(dir + "/" + n + "/", true, 0, DateTime.UtcNow, null, null, null)).ToList();
            return Task.FromResult(new RemoteListing(new RemoteItem(dir + "/", true, 0, null, null, null, null), children, true, []));
        }

        public Task<RemoteItem?> StatAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteDownload> GetAsync(string path, long offset, CancellationToken ct) => throw new NotSupportedException();

        public Task PutAsync(string path, Stream content, long length, DateTime? mtimeUtc, CancellationToken ct) => throw new NotSupportedException();

        public Task MkdirAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task MoveAsync(string from, string to, bool overwrite, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string path, CancellationToken ct) => throw new NotSupportedException();

        public Task<RemoteQuota?> GetQuotaAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
