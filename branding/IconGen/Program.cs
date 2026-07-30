using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Deck.App;

// Both halves of the job live in namespaces that name the same things: System.Drawing draws the mark,
// WPF's imaging stack reads the finished icon back. Aliased rather than fully qualified, so the code
// below stays readable.
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace Deck.IconGen;

/// <summary>
/// Writes every image the product needs from one definition of the mark: the multi-resolution icon
/// the build embeds, the PNGs the readme and the release page use, and the installer's wizard image.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The application icon carries its own ground, and it has to. A near-black mark disappears on a
    /// dark taskbar and a near-white one disappears on a light taskbar, and Windows gives you no say
    /// in which you get - so the tile is the petrol accent, which separates from both, and the mark
    /// is the same cool near-white as the light theme's ground rather than pure white.
    /// </summary>
    private static readonly Color Tile = ColorTranslator.FromHtml("#2A6A70");
    private static readonly Color OnTile = ColorTranslator.FromHtml("#F4F5F3");

    private static readonly Color Ink = ColorTranslator.FromHtml("#16191C");
    private static readonly Color Light = ColorTranslator.FromHtml("#F0F2F0");

    private static int Main()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.Error.WriteLine("Could not find the repository root from " + Environment.CurrentDirectory);
            return 1;
        }

        var app = Path.Combine(root, "src", "Deck.App");
        var plate = Path.Combine(root, "branding", "plate");
        var installer = Path.Combine(root, "installer");

        Directory.CreateDirectory(plate);

        // ---- the icon the executable carries.
        var icon = Path.Combine(app, "Deck.ico");
        WriteIcon(icon, DeckMark.IconSizes, size => DeckMark.Render(size, OnTile, Tile));
        Report(icon, DeckMark.IconSizes);

        // ---- the readme and the release page. Two grounds, because GitHub has two themes.
        Save(Path.Combine(plate, "icon-256.png"), DeckMark.Render(256, OnTile, Tile));
        Save(Path.Combine(plate, "icon-512.png"), DeckMark.Render(512, OnTile, Tile));
        Save(Path.Combine(plate, "mark-ink-512.png"), DeckMark.Render(512, Ink));
        Save(Path.Combine(plate, "mark-light-512.png"), DeckMark.Render(512, Light));

        // ---- the lamp variant, for anything large enough to show it.
        Save(Path.Combine(plate, "icon-512-live.png"),
            DeckMark.Render(512, OnTile, Tile, ColorTranslator.FromHtml("#C93F36")));

        // ---- the proof sheet: every small size magnified with no smoothing, which is the only way to
        //      look at hinting. If a stem is two pixels on one size and three on the next, it shows here
        //      and nowhere else.
        SaveProofSheet(Path.Combine(plate, "hinting-proof.png"), [16, 20, 24, 32, 48], 8);

        // ---- the installer's small wizard image. Inno draws it on the wizard's own white page, so
        //      it is flattened onto white rather than left transparent.
        SaveBmp(Path.Combine(installer, "wizard-small.bmp"), 110, 110, 64);

        // Read the icon back through Windows' own parser rather than trusting the bytes just written.
        // A hand-built container that is subtly wrong still opens in some viewers and then shows up as
        // a blank square in the taskbar, which is not a thing to discover after a release.
        return Verify(icon, DeckMark.IconSizes) ? 0 : 1;
    }

    private static bool Verify(string path, int[] sizes)
    {
        Console.WriteLine();
        Console.WriteLine("Reading it back:");

        var decoder = new IconBitmapDecoder(
            new Uri(path),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var found = decoder.Frames.Select(f => f.PixelWidth).Order().ToArray();
        var ok = found.SequenceEqual(sizes.Order());

        if (!ok)
        {
            Console.WriteLine($"  sizes in the file: {string.Join(", ", found)}");
            Console.WriteLine($"  sizes expected:    {string.Join(", ", sizes.Order())}");
        }

        foreach (var frame in decoder.Frames.OrderBy(f => f.PixelWidth))
        {
            var size = frame.PixelWidth;

            // Probe the geometry rather than guessing at coordinates: the middle of the top bar has to
            // be the mark, and the middle of the counter has to be the ground showing through it.
            // Those two facts are what make the shape a D rather than a slab.
            var cut = DeckMark.CutFor(size);
            var bar = PixelAt(frame, cut.X + (cut.Width / 2), cut.Y + (cut.Thickness / 2));
            var hole = PixelAt(frame, cut.X + (cut.Width / 2), cut.Y + (cut.Height / 2));

            // And the mark must not touch any edge. Eyeballing the magnified sheet cannot settle
            // whether a one-pixel margin is there or not, and a letter bleeding off one side is the
            // kind of thing that looks deliberate until you see it next to the others.
            var clear =
                Same(PixelAt(frame, size / 2, 0), Tile) &&
                Same(PixelAt(frame, size / 2, size - 1), Tile) &&
                Same(PixelAt(frame, 0, size / 2), Tile) &&
                Same(PixelAt(frame, size - 1, size / 2), Tile);

            var square = frame.PixelWidth == frame.PixelHeight;
            var right = square && clear && Same(bar, OnTile) && Same(hole, Tile);

            if (!right) ok = false;

            Console.WriteLine($"  {size,3} px  {(right ? "ok  " : "WRONG")}  " +
                              $"{frame.PixelWidth}x{frame.PixelHeight}, {frame.Format}; " +
                              $"bar #{bar.R:X2}{bar.G:X2}{bar.B:X2}, " +
                              $"counter #{hole.R:X2}{hole.G:X2}{hole.B:X2}");
        }

        Console.WriteLine(ok ? "\nEvery size present and correct." : "\nThe icon is not right.");
        return ok;
    }

    private static Color PixelAt(BitmapSource frame, int x, int y)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];

        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    // ------------------------------------------------------------------ the icon container

    /// <summary>
    /// Writes a Windows icon by hand, because .NET can only save one image per file.
    /// <para>
    /// Every size goes in as an uncompressed 32-bit DIB, including 256. PNG entries are the usual way
    /// to keep a 256-pixel icon small and the shell reads them perfectly well - but nothing else
    /// reliably does. <c>System.Drawing.Icon</c> loads a PNG entry and hands back a broken bitmap, so
    /// the icon could not be read back and checked, and an icon that cannot be verified is one that
    /// gets discovered as a blank square in a taskbar after a release. A quarter of a megabyte in an
    /// installer this size is not worth that.
    /// </para>
    /// </summary>
    private static void WriteIcon(string path, int[] sizes, Func<int, Bitmap> render)
    {
        var images = new List<(int Size, byte[] Data)>();

        foreach (var size in sizes)
        {
            using var bitmap = render(size);
            images.Add((size, DibBytes(bitmap)));
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)images.Count);

        var offset = 6 + (16 * images.Count);

        foreach (var image in images)
        {
            // 256 is written as zero in a single byte, which is how the format says "256".
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)(image.Size >= 256 ? 0 : image.Size));
            writer.Write((byte)0);            // palette size: none, this is 32-bit
            writer.Write((byte)0);            // reserved
            writer.Write((ushort)1);          // colour planes
            writer.Write((ushort)32);         // bits per pixel
            writer.Write(image.Data.Length);
            writer.Write(offset);

            offset += image.Data.Length;
        }

        foreach (var image in images) writer.Write(image.Data);
    }

    /// <summary>Colours match if they are within a shade of each other, to allow for rounding.</summary>
    private static bool Same(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2 && a.A > 250;

    private static byte[] PngBytes(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, ImageFormat.Png);
        return memory.ToArray();
    }

    /// <summary>
    /// A 32-bit DIB as an icon expects it: no file header, a doubled height because the format still
    /// declares room for a 1-bit mask, rows bottom-up, and BGRA rather than RGBA.
    /// </summary>
    private static byte[] DibBytes(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;

        // The mask is obsolete for 32-bit icons - the alpha channel does the work - but it is still
        // part of the structure, so it is written as zeros, which means "opaque" everywhere.
        var maskStride = (width + 31) / 32 * 4;

        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);

        writer.Write(40);                     // BITMAPINFOHEADER size
        writer.Write(width);
        writer.Write(height * 2);             // colour rows plus mask rows
        writer.Write((ushort)1);              // planes
        writer.Write((ushort)32);             // bits per pixel
        writer.Write(0);                      // BI_RGB, uncompressed
        writer.Write(width * height * 4);     // image size
        writer.Write(0);                      // pixels per metre, x
        writer.Write(0);                      // pixels per metre, y
        writer.Write(0);                      // colours used
        writer.Write(0);                      // colours important

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                writer.Write(pixel.B);
                writer.Write(pixel.G);
                writer.Write(pixel.R);
                writer.Write(pixel.A);
            }
        }

        var blankRow = new byte[maskStride];
        for (var y = 0; y < height; y++) writer.Write(blankRow);

        writer.Flush();
        return memory.ToArray();
    }

    // ------------------------------------------------------------------ files

    private static void Save(string path, Bitmap bitmap)
    {
        using (bitmap) bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"  wrote {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Every size blown up with nearest-neighbour sampling and a pixel grid over it, so the hinting can
    /// actually be looked at. Smoothed magnification would hide the exact thing this is for.
    /// </summary>
    private static void SaveProofSheet(string path, int[] sizes, int zoom)
    {
        const int Pad = 16;
        const int Caption = 18;

        var width = Pad + sizes.Sum(s => (s * zoom) + Pad);
        var height = Pad + (sizes.Max() * zoom) + Caption + Pad;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sheet))
        {
            graphics.Clear(ColorTranslator.FromHtml("#101317"));
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            using var font = new Font("Consolas", 8f);
            using var captionBrush = new SolidBrush(ColorTranslator.FromHtml("#939BA2"));
            using var gridPen = new Pen(Color.FromArgb(38, 255, 255, 255));

            var x = Pad;

            foreach (var size in sizes)
            {
                using var mark = DeckMark.Render(size, OnTile, Tile);

                var box = size * zoom;
                var y = Pad + (sizes.Max() * zoom) - box;

                graphics.DrawImage(mark, new Rectangle(x, y, box, box));

                for (var i = 1; i < size; i++)
                {
                    graphics.DrawLine(gridPen, x + (i * zoom), y, x + (i * zoom), y + box);
                    graphics.DrawLine(gridPen, x, y + (i * zoom), x + box, y + (i * zoom));
                }

                graphics.DrawString($"{size} px", font, captionBrush, x, Pad + (sizes.Max() * zoom) + 3);

                x += box + Pad;
            }
        }

        sheet.Save(path, ImageFormat.Png);
        Console.WriteLine($"  wrote {Path.GetFileName(path)}");
    }

    /// <summary>The wizard image: the mark centred on white, at 24-bit, which is all Inno reads.</summary>
    private static void SaveBmp(string path, int width, int height, int markSize)
    {
        using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.White);

            using var mark = DeckMark.Render(markSize, OnTile, Tile);
            graphics.DrawImageUnscaled(mark, (width - markSize) / 2, (height - markSize) / 2);
        }

        canvas.Save(path, ImageFormat.Bmp);
        Console.WriteLine($"  wrote {Path.GetFileName(path)}");
    }

    private static void Report(string path, int[] sizes)
    {
        var length = new FileInfo(path).Length;
        Console.WriteLine($"  wrote {Path.GetFileName(path)} - {sizes.Length} sizes " +
                          $"({string.Join(", ", sizes)}), {length:N0} bytes");
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Deck.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }
}
