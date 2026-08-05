// SMSLIBRE — native yield-map rasterizer.
//
// Clean-room replacement for the part of SMS's native ALV_MapVis that draws a
// classified point map. Input: yield points (lon/lat + value). Output: a BGRA
// pixel buffer plus a legend, both UI-agnostic so the Avalonia app and a
// headless PNG test share one implementation.
//
// Projection: for a single field a cos(latitude)-scaled equirectangular mapping
// gives a correct display aspect ratio without pulling in GDAL/PROJ. (Precise
// area/UTM belongs in the import layer, not the renderer.)

using System;
using System.Collections.Generic;
using System.Linq;

namespace SmsLibre.Core;

public sealed class BgraImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }   // BGRA, row-major, premultiplied-agnostic
    public BgraImage(int w, int h) { Width = w; Height = h; Pixels = new byte[w * h * 4]; }
}

public readonly record struct Rgb(byte R, byte G, byte B);

public sealed class LegendClass
{
    public double Low { get; init; }
    public double High { get; init; }
    public Rgb Color { get; init; }
    public int Count { get; init; }
}

public sealed class YieldRenderResult
{
    public required BgraImage Image { get; init; }
    public required IReadOnlyList<LegendClass> Legend { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public int PointCount { get; init; }
}

public static class YieldRaster
{
    // ColorBrewer RdYlGn (low→high). Sampled to N classes.
    private static readonly Rgb[] RdYlGn =
    {
        new(0xA5, 0x00, 0x26), new(0xD7, 0x30, 0x27), new(0xF4, 0x6D, 0x43),
        new(0xFD, 0xAE, 0x61), new(0xFE, 0xE0, 0x8B), new(0xFF, 0xFF, 0xBF),
        new(0xD9, 0xEF, 0x8B), new(0xA6, 0xD9, 0x6A), new(0x66, 0xBD, 0x63),
        new(0x1A, 0x98, 0x50), new(0x00, 0x68, 0x37),
    };

    public static Rgb Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double f = t * (RdYlGn.Length - 1);
        int i = (int)Math.Floor(f);
        if (i >= RdYlGn.Length - 1) return RdYlGn[^1];
        double u = f - i;
        Rgb a = RdYlGn[i], b = RdYlGn[i + 1];
        return new Rgb(
            (byte)(a.R + (b.R - a.R) * u),
            (byte)(a.G + (b.G - a.G) * u),
            (byte)(a.B + (b.B - a.B) * u));
    }

    /// <summary>Quantile (equal-count) class breaks, the standard for yield maps.</summary>
    public static double[] QuantileBreaks(IReadOnlyList<double> sortedValues, int nClasses)
    {
        var edges = new List<double>();
        int n = sortedValues.Count;
        for (int k = 0; k <= nClasses; k++)
        {
            double q = (double)k / nClasses;
            double pos = q * (n - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, n - 1);
            double frac = pos - lo;
            edges.Add(sortedValues[lo] + (sortedValues[hi] - sortedValues[lo]) * frac);
        }
        // Deduplicate so repeated values don't create empty classes.
        var uniq = new List<double> { edges[0] };
        foreach (var e in edges.Skip(1))
            if (e > uniq[^1]) uniq.Add(e);
        if (uniq.Count < 2) uniq.Add(uniq[0] + 1e-9);
        return uniq.ToArray();
    }

    public static YieldRenderResult Render(
        IReadOnlyList<YieldPoint> points, int width, int height,
        int nClasses = 7, double marginFrac = 0.03, int dotRadius = 1,
        Rgb? background = null)
    {
        if (points.Count == 0)
            throw new ArgumentException("no points to render");

        var img = new BgraImage(width, height);
        FillBackground(img, background ?? new Rgb(255, 255, 255));

        // Classification.
        var values = points.Select(p => p.Value).ToArray();
        Array.Sort(values);
        double[] edges = QuantileBreaks(values, nClasses);
        int classes = edges.Length - 1;
        var colors = new Rgb[classes];
        for (int c = 0; c < classes; c++)
            colors[c] = Ramp(classes == 1 ? 0.5 : (double)c / (classes - 1));

        // Projection extent (metres, cos-lat equirectangular about the centroid).
        double lat0 = points.Average(p => p.Lat);
        double kx = 111_320.0 * Math.Cos(lat0 * Math.PI / 180.0);
        double ky = 110_540.0;
        double minX = double.MaxValue, minY = double.MaxValue,
               maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            double x = p.Lon * kx, y = p.Lat * ky;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        double spanX = Math.Max(maxX - minX, 1e-6);
        double spanY = Math.Max(maxY - minY, 1e-6);

        // Fit the data box into the pixel box preserving aspect; add a margin.
        double avail = 1.0 - 2 * marginFrac;
        double scale = Math.Min(width * avail / spanX, height * avail / spanY);
        double drawW = spanX * scale, drawH = spanY * scale;
        double offX = (width - drawW) / 2.0;
        double offY = (height - drawH) / 2.0;

        var counts = new int[classes];
        foreach (var p in points)
        {
            double x = p.Lon * kx, y = p.Lat * ky;
            int px = (int)(offX + (x - minX) * scale);
            int py = (int)(offY + (maxY - y) * scale);   // flip Y for screen
            int cls = ClassOf(p.Value, edges);
            counts[cls]++;
            PlotDot(img, px, py, colors[cls], dotRadius);
        }

        var legend = new List<LegendClass>();
        for (int c = 0; c < classes; c++)
            legend.Add(new LegendClass { Low = edges[c], High = edges[c + 1],
                                         Color = colors[c], Count = counts[c] });

        return new YieldRenderResult
        {
            Image = img, Legend = legend,
            Min = values[0], Max = values[^1],
            Mean = values.Average(), Median = values[values.Length / 2],
            PointCount = points.Count,
        };
    }

    private static int ClassOf(double v, double[] edges)
    {
        // edges are ascending; last class is inclusive of the max.
        for (int c = 0; c < edges.Length - 1; c++)
            if (v <= edges[c + 1]) return c;
        return edges.Length - 2;
    }

    private static void FillBackground(BgraImage img, Rgb bg)
    {
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = bg.B; img.Pixels[i + 1] = bg.G;
            img.Pixels[i + 2] = bg.R; img.Pixels[i + 3] = 255;
        }
    }

    private static void PlotDot(BgraImage img, int cx, int cy, Rgb color, int r)
    {
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = cx + dx, y = cy + dy;
            if ((uint)x >= (uint)img.Width || (uint)y >= (uint)img.Height) continue;
            int i = (y * img.Width + x) * 4;
            img.Pixels[i] = color.B; img.Pixels[i + 1] = color.G;
            img.Pixels[i + 2] = color.R; img.Pixels[i + 3] = 255;
        }
    }
}
