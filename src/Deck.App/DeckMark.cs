using System.Globalization;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Media = System.Windows.Media;

namespace Deck.App;

/// <summary>
/// The Deck's mark: a D whose curve is replaced by two 45-degree cuts, with a counter big enough to
/// hold a lamp. One definition, used by everything that draws it - the tray icon at runtime, the
/// multi-resolution <c>.ico</c> the build embeds, and the title bar.
/// <para>
/// The reason this is a table of hand-set cuts rather than one shape scaled: below about 24 pixels a
/// mark is not a drawing any more, it is a decision about which pixels are on. Scaling the 64-unit
/// master down to 16 puts the stem on a half-pixel and the chamfer across three, and the letter turns
/// to grey mush - which is exactly where this icon spends its life, because the notification area is
/// 16 pixels and that is where someone checks whether their station is still on air. So each size
/// gets whole-pixel geometry, and every size below 24 is drawn aliased on purpose: a 45-degree edge
/// on the pixel grid is a clean staircase, while the same edge antialiased is a soft one.
/// </para>
/// </summary>
internal static class DeckMark
{
    /// <summary>
    /// One size's geometry, in whole pixels. <paramref name="Thickness"/> is the stem, the top bar and
    /// the bowl, all of which are the same weight; <paramref name="Chamfer"/> is the 45-degree cut.
    /// </summary>
    internal readonly record struct Cut(int X, int Y, int Width, int Height, int Thickness, int Chamfer)
    {
        /// <summary>
        /// The counter's own chamfer. Not the outer one: insetting a 45-degree edge by the stem
        /// thickness shortens its cut by t(2-&#8730;2), and using the outer figure instead leaves the
        /// bowl visibly thicker across the diagonal than it is anywhere else.
        /// </summary>
        public int InnerChamfer =>
            Math.Max(1, (int)Math.Round(Chamfer - (Thickness * (2 - Math.Sqrt(2))), MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Every size Windows asks for, plus the two the readme and the release page use.
    /// <para>
    /// The family holds two proportions: a margin of an eighth of the box above and below, a little
    /// more at the sides, and a letter about 11 wide for every 12 tall. Both are checked by eye on the
    /// proof sheet the generator writes, which is how the two that were out got found - 16 had half the
    /// vertical margin of everything else so it read as a different, more cramped icon, and 24 came out
    /// exactly square so the D looked squat next to its neighbours. The letter does get squarer as it
    /// gets smaller, which is ordinary practice, but it has to do it gradually.
    /// </para>
    /// </summary>
    private static readonly Dictionary<int, Cut> Cuts = new()
    {
        [16] = new(2, 2, 12, 12, 3, 3),
        [20] = new(3, 2, 14, 16, 4, 4),
        [24] = new(4, 3, 16, 18, 5, 5),
        [32] = new(5, 4, 22, 24, 6, 6),
        [40] = new(6, 5, 28, 30, 8, 8),
        [48] = new(7, 6, 34, 36, 9, 9),
        [64] = new(10, 8, 44, 48, 12, 12),
        [96] = new(15, 12, 66, 72, 18, 18),
        [128] = new(20, 16, 88, 96, 24, 24),
        [256] = new(40, 32, 176, 192, 48, 48),
        [512] = new(80, 64, 352, 384, 96, 96),
    };

    /// <summary>The sizes that go into the icon, smallest first.</summary>
    public static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>
    /// The cut for a size. An unlisted size is scaled from the 64-unit master and rounded, which is
    /// the fallback rather than the rule - anything that matters is in the table.
    /// </summary>
    public static Cut CutFor(int size)
    {
        if (Cuts.TryGetValue(size, out var exact)) return exact;

        var master = Cuts[64];
        var scale = size / 64.0;

        int At(int value) => Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));

        return new Cut(At(master.X), At(master.Y), At(master.Width), At(master.Height),
                       At(master.Thickness), At(master.Chamfer));
    }

    /// <summary>Below this, edges are drawn on the pixel grid rather than antialiased.</summary>
    public static bool DrawsAliased(int size) => size <= 24;

    // ------------------------------------------------------------------ the shape

    private static Drawing.Point[] Outer(Cut c) =>
    [
        new(c.X, c.Y),
        new(c.X + c.Width - c.Chamfer, c.Y),
        new(c.X + c.Width, c.Y + c.Chamfer),
        new(c.X + c.Width, c.Y + c.Height - c.Chamfer),
        new(c.X + c.Width - c.Chamfer, c.Y + c.Height),
        new(c.X, c.Y + c.Height),
    ];

    private static Drawing.Point[] Counter(Cut c)
    {
        var t = c.Thickness;
        var ci = c.InnerChamfer;

        return
        [
            new(c.X + t, c.Y + t),
            new(c.X + c.Width - t - ci, c.Y + t),
            new(c.X + c.Width - t, c.Y + t + ci),
            new(c.X + c.Width - t, c.Y + c.Height - t - ci),
            new(c.X + c.Width - t - ci, c.Y + c.Height - t),
            new(c.X + t, c.Y + c.Height - t),
        ];
    }

    /// <summary>
    /// The lamp that sits in the counter, as a rectangle. Only used where there is room for it to
    /// read - it is dropped from every icon below 32 pixels, where the counter is eight pixels tall
    /// and filling it would turn the letter into a blob.
    /// </summary>
    public static Drawing.Rectangle Lamp(Cut c)
    {
        var t = c.Thickness;
        var gap = Math.Max(1, t / 3);
        var right = Math.Max(gap, c.InnerChamfer);

        return new Drawing.Rectangle(
            c.X + t + gap,
            c.Y + t + gap,
            Math.Max(1, c.Width - (2 * t) - gap - right),
            Math.Max(1, c.Height - (2 * t) - (2 * gap)));
    }

    /// <summary>
    /// The mark as one path, counter included as a second figure. Alternate fill turns the counter
    /// into a hole, so whatever is behind the mark shows through it.
    /// </summary>
    public static Drawing2D.GraphicsPath BuildPath(int size)
    {
        var cut = CutFor(size);
        var path = new Drawing2D.GraphicsPath(Drawing2D.FillMode.Alternate);

        path.AddPolygon(Outer(cut));
        path.AddPolygon(Counter(cut));

        return path;
    }

    /// <summary>
    /// Draws the mark at one size. <paramref name="ground"/> fills the whole box behind it, which is
    /// what an application icon needs - a mark alone in near-black vanishes on a dark taskbar and one
    /// in near-white vanishes on a light one. Leave it null for a transparent mark.
    /// </summary>
    public static Drawing.Bitmap Render(
        int size,
        Drawing.Color form,
        Drawing.Color? ground = null,
        Drawing.Color? lamp = null)
    {
        var bitmap = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = DrawsAliased(size)
                ? Drawing2D.SmoothingMode.None
                : Drawing2D.SmoothingMode.AntiAlias;

            // Without this a filled polygon lands half a pixel off at small sizes, which is the
            // difference between a three-pixel stem and a two-pixel stem with a grey edge.
            graphics.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;

            graphics.Clear(ground ?? Drawing.Color.Transparent);

            using var path = BuildPath(size);
            using var brush = new Drawing.SolidBrush(form);
            graphics.FillPath(brush, path);

            if (lamp is { } lampColour)
            {
                using var lampBrush = new Drawing.SolidBrush(lampColour);
                graphics.FillRectangle(lampBrush, Lamp(CutFor(size)));
            }
        }

        return bitmap;
    }

    /// <summary>
    /// The size the mark is drawn at in the title bar, beside the wordmark. Twenty rather than the
    /// twenty-four the 78-pixel block has room for: next to thirteen-point capitals a larger mark
    /// stops reading as a lock-up and starts reading as a logo with a caption stuck to it.
    /// </summary>
    public const int TitleBarSize = 20;

    /// <summary>
    /// The mark as a WPF geometry, for the title bar. Built from the hinted cut for the exact size it
    /// is drawn at, so it lands on whole pixels rather than being scaled off the grid by a Viewbox.
    /// <para>
    /// <c>F0</c> is not optional. Both figures are wound the same way, so the counter is only a hole
    /// under the even-odd rule; under the nonzero rule the D fills in solid and becomes a slab.
    /// </para>
    /// </summary>
    public static Media.Geometry TitleBarGeometry { get; } = Media.Geometry.Parse("F0 " + PathData(TitleBarSize));

    /// <summary>
    /// The size the mark is drawn at on the mini strip. Forty-eight is what fits: the strip is 56 pixels
    /// tall and fixed, so this is the largest hinted size that leaves the mark room to breathe rather
    /// than running into both edges. The letter inside the box is 34 by 36, which puts ten pixels of
    /// strip above and below it.
    /// </summary>
    public const int MiniStripSize = 48;

    /// <summary>The mark for the mini strip, at its own hinted size rather than a scaled title bar.</summary>
    public static Media.Geometry MiniStripGeometry { get; } = Media.Geometry.Parse("F0 " + PathData(MiniStripSize));

    /// <summary>
    /// The same shape as path data for XAML, so the title bar draws the mark rather than a letter in
    /// a font. Uses the hinted cut for the size it will actually be drawn at, so it lands on whole
    /// pixels instead of being scaled off the grid by a Viewbox.
    /// </summary>
    public static string PathData(int size)
    {
        var cut = CutFor(size);
        return Figure(Outer(cut)) + " " + Figure(Counter(cut));
    }

    /// <summary>The lamp rectangle for XAML, as "x,y,w,h".</summary>
    public static string LampRect(int size)
    {
        var lamp = Lamp(CutFor(size));
        return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
            lamp.X, lamp.Y, lamp.Width, lamp.Height);
    }

    private static string Figure(Drawing.Point[] points)
    {
        var parts = points.Select((p, i) =>
            string.Format(CultureInfo.InvariantCulture, "{0}{1},{2}", i == 0 ? "M" : "L", p.X, p.Y));

        return string.Join(" ", parts) + " Z";
    }
}
