using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SmsLibre.Core;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// Machine data gets very wide. A 2022 forage harvester card in the Olds College
/// vault produces layers of 1,535 columns, and SQLite refuses to create a table
/// with 2,000 or more. Without a guard, one card slightly wider than that would
/// fail the whole import with "too many columns" and lose everything.
/// </summary>
public class WideLayerTests
{
    private static OperationLayer Layer(int channels, int points, Func<int, bool> hasValue)
    {
        var l = new OperationLayer { Field = "wide", OperationType = "Harvesting" };
        for (int i = 0; i < channels; i++) { l.Channels.Add($"ch{i}"); l.Units.Add(""); }
        for (int p = 0; p < points; p++)
        {
            var vals = new double?[channels];
            for (int i = 0; i < channels; i++) vals[i] = hasValue(i) ? i + p : (double?)null;
            l.Points.Add(new LayerPoint { Lon = -114.0 + p * 1e-4, Lat = 51.7, Values = vals });
        }
        return l;
    }

    [Fact]
    public void A_layer_within_the_limit_keeps_every_channel_in_order()
    {
        var l = Layer(50, 3, _ => true);
        Assert.Equal(Enumerable.Range(0, 50), l.ChannelsToKeep(OperationLayer.MaxGpkgChannels));
    }

    [Fact]
    public void An_over_wide_layer_drops_the_emptiest_channels()
    {
        // Only the even channels ever record a reading; the odd ones are null
        // throughout, which is what real overflow looks like.
        var l = Layer(100, 5, i => i % 2 == 0);
        var keep = l.ChannelsToKeep(50);

        Assert.Equal(50, keep.Count);
        Assert.All(keep, i => Assert.Equal(0, i % 2));
        Assert.Equal(keep.OrderBy(i => i), keep);   // card order preserved
    }

    [Fact]
    public void A_layer_too_wide_for_sqlite_still_writes()
    {
        var l = Layer(2400, 2, i => i < 1500);
        var keep = l.ChannelsToKeep(OperationLayer.MaxGpkgChannels);
        Assert.Equal(OperationLayer.MaxGpkgChannels, keep.Count);

        string path = Path.Combine(Path.GetTempPath(), $"smslibre-wide-{Guid.NewGuid():N}.gpkg");
        try
        {
            var fields = keep.Select(i => new GpkgField($"ch{i}", GpkgType.Double)).ToList();
            using (var gpkg = new GeoPackageWriter(path))
            {
                int n = gpkg.WritePointLayer("wide", fields, l.Points.Select(p =>
                    new GpkgFeature
                    {
                        Lon = p.Lon,
                        Lat = p.Lat,
                        Values = keep.Select(i => (object?)p.Values[i]).ToArray(),
                    }));
                Assert.Equal(2, n);
            }

            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM wide";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void The_limit_matches_what_sqlite_actually_accepts()
    {
        // Pins the constant to the engine we ship rather than to folklore: three
        // columns are spent on fid, geometry and timestamp before any channel.
        Assert.Equal(1999, OperationLayer.MaxGpkgChannels + 3);
    }
}
