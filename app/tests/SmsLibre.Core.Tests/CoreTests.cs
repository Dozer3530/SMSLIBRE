using System;
using System.Collections.Generic;
using System.Linq;
using SmsLibre.Core;
using Xunit;

namespace SmsLibre.Tests;

public class YieldRasterTests
{
    private static List<YieldPoint> Grid(int n, Func<int, double> value)
    {
        var pts = new List<YieldPoint>();
        for (int i = 0; i < n; i++)
            pts.Add(new YieldPoint(-114.09 + i * 1e-5, 51.77 + i * 1e-5, value(i)));
        return pts;
    }

    [Fact]
    public void QuantileBreaks_are_monotonic_and_have_expected_count()
    {
        var sorted = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
        var edges = YieldRaster.QuantileBreaks(sorted, 7);
        Assert.Equal(8, edges.Length);                    // n+1 edges
        for (int i = 1; i < edges.Length; i++)
            Assert.True(edges[i] > edges[i - 1], "edges must strictly ascend");
        Assert.Equal(0, edges[0]);
        Assert.Equal(99, edges[^1]);
    }

    [Fact]
    public void QuantileBreaks_dedupe_on_near_constant_data()
    {
        var sorted = Enumerable.Repeat(5.0, 50).ToArray();
        var edges = YieldRaster.QuantileBreaks(sorted, 7);
        Assert.True(edges.Length >= 2);                   // never zero-width
        Assert.True(edges[^1] > edges[0]);
    }

    [Fact]
    public void Ramp_endpoints_are_red_low_and_green_high()
    {
        var low = YieldRaster.Ramp(0.0);
        var high = YieldRaster.Ramp(1.0);
        Assert.True(low.R > low.G);                        // low = reddish
        Assert.True(high.G > high.R);                      // high = greenish
    }

    [Fact]
    public void Render_produces_image_of_requested_size_and_correct_stats()
    {
        var pts = Grid(700, i => i % 100);                 // values 0..99 repeating
        var res = YieldRaster.Render(pts, 200, 150, nClasses: 5);
        Assert.Equal(200, res.Image.Width);
        Assert.Equal(150, res.Image.Height);
        Assert.Equal(200 * 150 * 4, res.Image.Pixels.Length);
        Assert.Equal(700, res.PointCount);
        Assert.Equal(0, res.Min);
        Assert.Equal(99, res.Max);
        Assert.True(res.Legend.Count is >= 1 and <= 5);
        Assert.Equal(700, res.Legend.Sum(c => c.Count));   // every point classified
    }

    [Fact]
    public void Render_throws_on_empty_input()
        => Assert.Throws<ArgumentException>(() =>
               YieldRaster.Render(new List<YieldPoint>(), 100, 100));
}

public class CleaningTests
{
    [Fact]
    public void Clean_drops_zeros_nullisland_and_outliers()
    {
        var pts = new List<YieldPoint>();
        for (int i = 0; i < 100; i++)
            pts.Add(new YieldPoint(-114.09 + i * 1e-5, 51.77 + i * 1e-5, 100 + i % 10));
        pts.Add(new YieldPoint(-114.09, 51.77, 0));         // zero value
        pts.Add(new YieldPoint(0, 0, 105));                 // null island
        pts.Add(new YieldPoint(-114.09, 51.77, 9_999_999)); // value outlier
        pts.Add(new YieldPoint(0.0001, 0.0001, 105));       // spatial outlier

        var cleaned = Cleaning.Clean(pts);
        Assert.DoesNotContain(cleaned, p => p.Value <= 0);
        Assert.DoesNotContain(cleaned, p => Math.Abs(p.Lon) < 1e-6 && Math.Abs(p.Lat) < 1e-6);
        Assert.DoesNotContain(cleaned, p => p.Value > 1_000_000);
        Assert.True(cleaned.Count >= 90);                   // keeps the bulk
    }

    [Fact]
    public void Clean_empty_is_empty()
        => Assert.Empty(Cleaning.Clean(new List<YieldPoint>()));
}

public class PngWriterTests
{
    [Fact]
    public void Encode_writes_a_valid_png_header()
    {
        var img = new BgraImage(32, 24);
        byte[] png = PngWriter.Encode(img);

        byte[] sig = { 137, 80, 78, 71, 13, 10, 26, 10 };
        Assert.True(png.Take(8).SequenceEqual(sig), "PNG signature");

        // IHDR chunk immediately follows the signature; width/height are big-endian.
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        Assert.Equal(32, w);
        Assert.Equal(24, h);
        Assert.Equal((byte)'I', png[12]);
        Assert.Equal((byte)'H', png[13]);
    }
}
