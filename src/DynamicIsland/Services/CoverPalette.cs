using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Settings;

namespace DynamicIsland.Services;

/// <summary>Extracts a three-colour gradient palette from album art.</summary>
public static class CoverPalette
{
    private const int SampleSize = 32;
    private const int HueBuckets = 12;
    private static readonly uint[] MonoColors = [0xFFFFFF, 0x8E8E93, 0xD1D1D6];

    /// <summary>
    /// Returns the cover's dominant vivid colours (RGB) and a peak alpha for them, a soft grey
    /// palette for black-and-white covers, or null when there is no usable image.
    /// </summary>
    public static (uint[] Colors, byte Alpha)? Extract(BitmapSource? source)
    {
        if (source is null || source.PixelWidth == 0 || source.PixelHeight == 0) return null;
        try
        {
            BitmapSource bmp = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            bmp = new TransformedBitmap(bmp, new ScaleTransform(
                (double)SampleSize / bmp.PixelWidth, (double)SampleSize / bmp.PixelHeight));
            int stride = bmp.PixelWidth * 4;
            var px = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(px, stride, 0);

            // Bucket pixels by hue, weighting vivid ones more, and average the colour per bucket.
            var weight = new double[HueBuckets];
            var sumR = new double[HueBuckets];
            var sumG = new double[HueBuckets];
            var sumB = new double[HueBuckets];
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                var (hue, sat, val) = ToHsv(r, g, b);
                if (sat < 0.25 || val < 0.2) continue;

                int k = (int)(hue / 360 * HueBuckets) % HueBuckets;
                double w = sat * sat * val;
                weight[k] += w;
                sumR[k] += r * w;
                sumG[k] += g * w;
                sumB[k] += b * w;
            }

            double total = weight.Sum();
            if (total < SampleSize * SampleSize * 0.02)
                return (MonoColors, 0x38); // essentially greyscale art

            var picked = Enumerable.Range(0, HueBuckets)
                .Where(k => weight[k] >= total * 0.06)
                .OrderByDescending(k => weight[k])
                .Take(3)
                .Select(k => Vivid(sumR[k] / weight[k], sumG[k] / weight[k], sumB[k] / weight[k]))
                .ToList();

            // Single-hue covers: add neighbouring shades of the dominant colour.
            while (picked.Count < 3)
                picked.Add(ShiftHue(picked[0], picked.Count == 1 ? 28 : -28));

            return (picked.ToArray(), 0x80);
        }
        catch (Exception ex)
        {
            Log.Write(ex);
            return null;
        }
    }

    /// <summary>Lifts dark or washed-out colours so they glow on the black island.</summary>
    private static uint Vivid(double r, double g, double b)
    {
        var (h, s, v) = ToHsv(r, g, b);
        return FromHsv(h, Math.Max(s, 0.55), Math.Max(v, 0.9));
    }

    private static uint ShiftHue(uint rgb, double degrees)
    {
        var (h, s, v) = ToHsv((rgb >> 16 & 0xFF) / 255.0, (rgb >> 8 & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
        return FromHsv((h + degrees + 360) % 360, s, v * 0.92);
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static uint FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static uint Byte(double value) => (uint)Math.Clamp(Math.Round(value * 255), 0, 255);
        return Byte(r + m) << 16 | Byte(g + m) << 8 | Byte(b + m);
    }
}
