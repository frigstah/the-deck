using System.IO.Compression;

namespace Deck.EncoderCheck;

/// <summary>
/// Just enough PNG to look at a screenshot: eight bits a channel, RGB or RGBA, not interlaced -
/// which is what every picture in this repository is, because they are all written by the same
/// capture.
/// <para>
/// This exists so that a check can look at the pictures on the website rather than at their file
/// names. A screenshot is the one thing on that page which cannot be generated from
/// <c>Palettes.cs</c>, so the only way to know it still shows the product is to open it and measure
/// the colour, and that needs a decoder. Nothing here is a general-purpose reader: anything it does
/// not understand it refuses rather than guesses at, so a picture saved some other way fails loudly
/// instead of being measured wrongly.
/// </para>
/// </summary>
internal static class PngReader
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>How many pixels the picture has of each colour it uses.</summary>
    public static Dictionary<int, int> Histogram(string path)
    {
        var (width, height, pixels) = Read(path);

        var counts = new Dictionary<int, int>();
        for (var i = 0; i < width * height; i++)
        {
            var key = (pixels[i * 3] << 16) | (pixels[(i * 3) + 1] << 8) | pixels[(i * 3) + 2];
            counts[key] = counts.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }

        return counts;
    }

    /// <summary>The colour covering more of the picture than any other, as <c>#AARRGGBB</c>.</summary>
    public static string Dominant(Dictionary<int, int> histogram) =>
        $"#FF{histogram.MaxBy(entry => entry.Value).Key:X6}";

    /// <summary>How many pixels are exactly this colour. Hex in, as <c>#AARRGGBB</c> or <c>#RRGGBB</c>.</summary>
    public static int Count(Dictionary<int, int> histogram, string hex)
    {
        var text = hex.TrimStart('#');
        if (text.Length == 8) text = text[2..];

        return histogram.TryGetValue(Convert.ToInt32(text, 16), out var seen) ? seen : 0;
    }

    /// <summary>The picture as three bytes a pixel, row by row from the top.</summary>
    public static (int Width, int Height, byte[] Rgb) Read(string path)
    {
        var file = File.ReadAllBytes(path);

        for (var i = 0; i < Signature.Length; i++)
        {
            if (file[i] != Signature[i]) throw new Exception($"{Path.GetFileName(path)} is not a PNG");
        }

        var at = Signature.Length;
        int width = 0, height = 0, channels = 0;
        var data = new MemoryStream();

        while (at + 8 <= file.Length)
        {
            var length = Int(file, at);
            var kind = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            var body = at + 8;

            switch (kind)
            {
                case "IHDR":
                    width = Int(file, body);
                    height = Int(file, body + 4);

                    var depth = file[body + 8];
                    var colour = file[body + 9];
                    var interlace = file[body + 12];

                    if (depth != 8) throw new Exception($"{Path.GetFileName(path)} is {depth} bits a channel, not 8");
                    if (interlace != 0) throw new Exception($"{Path.GetFileName(path)} is interlaced");

                    channels = colour switch
                    {
                        2 => 3,
                        6 => 4,
                        _ => throw new Exception($"{Path.GetFileName(path)} is colour type {colour}, not RGB or RGBA"),
                    };
                    break;

                case "IDAT":
                    data.Write(file, body, length);
                    break;
            }

            at = body + length + 4; // and past the CRC, which nothing here needs to verify.
            if (kind == "IEND") break;
        }

        if (width == 0 || height == 0) throw new Exception($"{Path.GetFileName(path)} has no image header");

        data.Position = 0;
        using var inflate = new ZLibStream(data, CompressionMode.Decompress);

        var stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        var read = 0;
        while (read < raw.Length)
        {
            var got = inflate.Read(raw, read, raw.Length - read);
            if (got == 0) throw new Exception($"{Path.GetFileName(path)} ended early");
            read += got;
        }

        return (width, height, Unfilter(raw, width, height, channels));
    }

    /// <summary>
    /// Undoes the per-row filter each scanline was written with. The filters are the whole reason a
    /// screenshot of flat colour compresses to a few kilobytes, and every one of them is defined in
    /// terms of the pixel to the left, the row above, or both.
    /// </summary>
    private static byte[] Unfilter(byte[] raw, int width, int height, int channels)
    {
        var stride = width * channels;
        var lines = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var from = (y * (stride + 1)) + 1;
            var to = y * stride;

            for (var x = 0; x < stride; x++)
            {
                int left = x >= channels ? lines[to + x - channels] : 0;
                int up = y > 0 ? lines[to - stride + x] : 0;
                int upLeft = y > 0 && x >= channels ? lines[to - stride + x - channels] : 0;

                var value = raw[from + x] + filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new Exception($"unknown PNG row filter {filter}"),
                };

                lines[to + x] = (byte)value;
            }
        }

        if (channels == 3) return lines;

        // Drop the alpha. A screenshot of a window is opaque throughout, and every measurement made
        // of one is about the colour.
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3] = lines[i * 4];
            rgb[(i * 3) + 1] = lines[(i * 4) + 1];
            rgb[(i * 3) + 2] = lines[(i * 4) + 2];
        }

        return rgb;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toCorner = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toCorner) return left;
        return toUp <= toCorner ? up : upLeft;
    }

    private static int Int(byte[] bytes, int at) =>
        (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
}
