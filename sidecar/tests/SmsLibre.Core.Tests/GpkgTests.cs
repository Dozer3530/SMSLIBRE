using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SmsLibre.Core;
using Xunit;

namespace SmsLibre.Tests;

public class GpkgTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "smslibre_gpkg_" + Path.GetRandomFileName() + ".gpkg");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static (List<GpkgField> fields, List<GpkgFeature> feats) Sample()
    {
        var fields = new List<GpkgField>
        {
            new("yield", GpkgType.Double),
            new("moisture", GpkgType.Double),
            new("crop", GpkgType.Text),
        };
        var feats = new List<GpkgFeature>
        {
            new() { Lon = -114.09, Lat = 51.77, Values = new object?[] { 5.15, 13.2, "wheat" } },
            new() { Lon = -114.08, Lat = 51.78, Values = new object?[] { 6.20, 12.8, "wheat" } },
            new() { Lon = -114.07, Lat = 51.79, Values = new object?[] { null, null, null } },
        };
        return (fields, feats);
    }

    [Fact]
    public void Writes_a_readable_geopackage_with_metadata_and_features()
    {
        var (fields, feats) = Sample();
        using (var w = new GeoPackageWriter(_path))
            Assert.Equal(3, w.WritePointLayer("Yield Layer 1", fields, feats, "test"));

        using var db = new SqliteConnection($"Data Source={_path}");
        db.Open();

        // GeoPackage identifies itself via application_id 'GPKG'.
        using (var c = db.CreateCommand())
        {
            c.CommandText = "PRAGMA application_id;";
            Assert.Equal(1196444487L, (long)c.ExecuteScalar()!);
        }

        // Required metadata rows exist and reference the layer.
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT data_type, srs_id FROM gpkg_contents WHERE table_name='Yield_Layer_1';";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal("features", r.GetString(0));
            Assert.Equal(4326, r.GetInt32(1));
        }
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT geometry_type_name FROM gpkg_geometry_columns WHERE table_name='Yield_Layer_1';";
            Assert.Equal("POINT", (string)c.ExecuteScalar()!);
        }

        // Attributes round-trip, including NULLs.
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*), SUM(yield IS NULL) FROM Yield_Layer_1;";
            using var r = c.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(3L, r.GetInt64(0));
            Assert.Equal(1L, r.GetInt64(1));
        }
    }

    [Fact]
    public void Point_geometry_blob_is_a_valid_gpkg_wkb_point()
    {
        byte[] g = GeoPackageWriter.PointGeometry(-114.09, 51.77);
        Assert.Equal((byte)'G', g[0]);
        Assert.Equal((byte)'P', g[1]);
        Assert.Equal(4326, BitConverter.ToInt32(g, 4));
        Assert.Equal(1, g[8]);                                  // little-endian WKB
        Assert.Equal(1u, BitConverter.ToUInt32(g, 9));          // point
        Assert.Equal(-114.09, BitConverter.ToDouble(g, 13), 9);
        Assert.Equal(51.77, BitConverter.ToDouble(g, 21), 9);
    }

    [Theory]
    [InlineData("Field 15-16 Yield", "Field_15_16_Yield")]
    [InlineData("2024/09/26", "_2024_09_26")]   // leading digit gets prefixed
    [InlineData("123abc", "_123abc")]
    [InlineData("", "layer")]
    public void Sanitize_produces_safe_identifiers(string input, string expected)
        => Assert.Equal(expected, GeoPackageWriter.Sanitize(input));
}

/// <summary>
/// Regression cover for channel-rich cards. A John Deere Gen4 seeding file
/// carried 537 similarly-named channels; truncating a name after appending a
/// uniqueness suffix cut the suffix off again and SQLite rejected the table.
/// </summary>
public class ManyChannelTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "smslibre_many_" + Path.GetRandomFileName() + ".gpkg");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Hundreds_of_long_similar_channel_names_all_survive()
    {
        // Names that are identical once truncated to the 60-character cap.
        const string stem = "Average_target_application_rate_mass_per_area_as_harvested_";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<GpkgField>();
        for (int i = 0; i < 300; i++)
        {
            string name = GeoPackageWriter.Sanitize(stem + i);
            // mirror the sidecar's uniquing
            if (name.Length > 60) name = name.Substring(0, 60);
            string candidate = name;
            for (int n = 2; !used.Add(candidate); n++)
            {
                string suffix = "_" + n;
                string s = name.Length + suffix.Length > 60
                    ? name.Substring(0, 60 - suffix.Length) : name;
                candidate = s + suffix;
            }
            fields.Add(new GpkgField(candidate, GpkgType.Double));
        }

        Assert.Equal(300, fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var feats = new[]
        {
            new GpkgFeature { Lon = -114.0, Lat = 51.7, Values = new object?[300] },
        };
        using var w = new GeoPackageWriter(_path);
        Assert.Equal(1, w.WritePointLayer("many_channels", fields, feats));
    }
}
