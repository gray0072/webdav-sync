using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WebDavSync.Core.Engine;

namespace WebDavSync.App;

/// <summary>Tray icons per state (§14), drawn at startup: a cloud in the state's color plus a badge.</summary>
internal sealed partial class TrayIcons : IDisposable
{
    private readonly Dictionary<(PairState, int), Icon> _icons = [];

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize.Width;
        foreach (var state in Enum.GetValues<PairState>())
        {
            _icons[(state, 0)] = Draw(size, state, 0);
            _icons[(state, 1)] = Draw(size, state, 1);
        }
    }

    /// <summary><paramref name="frame"/> alternates while syncing (animated icon).</summary>
    public Icon Get(PairState state, int frame = 0) => _icons[(state, state == PairState.Syncing ? frame % 2 : 0)];

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            var handle = icon.Handle;
            icon.Dispose();
            DestroyIcon(handle);
        }
    }

    private static Icon Draw(int size, PairState state, int frame)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var cloud = state switch
            {
                PairState.Offline or PairState.Paused => Color.FromArgb(140, 140, 140),
                PairState.Syncing => frame == 0 ? Color.FromArgb(0, 120, 215) : Color.FromArgb(80, 170, 240),
                _ => Color.FromArgb(0, 120, 215),
            };
            var s = size / 16f;
            using (var brush = new SolidBrush(cloud))
            {
                g.FillEllipse(brush, 1 * s, 6 * s, 7 * s, 7 * s);
                g.FillEllipse(brush, 4 * s, 2 * s, 8 * s, 8 * s);
                g.FillEllipse(brush, 8 * s, 5 * s, 7 * s, 7 * s);
                g.FillRectangle(brush, 4 * s, 9 * s, 8 * s, 4 * s);
            }

            switch (state)
            {
                case PairState.NeedsAttention:
                    Badge(g, s, Color.FromArgb(255, 185, 0), "!");
                    break;
                case PairState.Error:
                    Badge(g, s, Color.FromArgb(210, 40, 40), "×");
                    break;
                case PairState.Waiting:
                    using (var dot = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(dot, 6.5f * s, 6.5f * s, 3 * s, 3 * s);
                    }

                    break;
                case PairState.Paused:
                    using (var bars = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(bars, 6 * s, 6 * s, 1.5f * s, 5 * s);
                        g.FillRectangle(bars, 9 * s, 6 * s, 1.5f * s, 5 * s);
                    }

                    break;
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void Badge(Graphics g, float s, Color color, string mark)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 8 * s, 8 * s, 8 * s, 8 * s);
        using var font = new Font(FontFamily.GenericSansSerif, 6 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(mark, font, white, new RectangleF(8 * s, 8 * s, 8 * s, 8 * s), format);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
