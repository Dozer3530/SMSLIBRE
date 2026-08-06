// SMSLIBRE — minimal GeoPackage writer.
//
// A GeoPackage is just SQLite with a documented set of metadata tables, so we
// write it directly rather than taking a GDAL dependency in the sidecar. QGIS
// opens the result natively, with every logged sensor value as an attribute.
//
// Spec: OGC GeoPackage 1.3 — we implement the subset needed for 2D point
// feature tables in EPSG:4326.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SmsLibre.Core;

/// <summary>A column in an output feature layer.</summary>
public sealed record GpkgField(string Name, GpkgType Type);

public enum GpkgType { Double, Text, Integer, DateTime }

/// <summary>One point feature: position plus a value per field (by index).</summary>
public sealed class GpkgFeature
{
    public double Lon { get; init; }
    public double Lat { get; init; }
    public object?[] Values { get; init; } = Array.Empty<object?>();
}

/// <summary>A ring of (lon, lat) vertices.</summary>
public sealed class GpkgRing
{
    public List<(double Lon, double Lat)> Points { get; } = new();
}

/// <summary>One polygon: an outer ring plus any holes.</summary>
public sealed class GpkgPolygon
{
    public GpkgRing Exterior { get; set; } = new();
    public List<GpkgRing> Interior { get; } = new();
}

/// <summary>A multipolygon feature (a field boundary and its exclusions).</summary>
public sealed class GpkgPolygonFeature
{
    public List<GpkgPolygon> Polygons { get; } = new();
    public object?[] Values { get; init; } = Array.Empty<object?>();
}

public sealed class GeoPackageWriter : IDisposable
{
    private readonly SqliteConnection _db;

    public GeoPackageWriter(string path, bool overwrite = true)
    {
        if (overwrite && File.Exists(path)) File.Delete(path);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;");
        InitMetadata();
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Create the GeoPackage metadata tables and the required SRS rows.</summary>
    private void InitMetadata()
    {
        // application_id 'GPKG' (0x47504B47) and user_version 10300 identify the file.
        Exec("PRAGMA application_id = 1196444487; PRAGMA user_version = 10300;");

        Exec(@"
CREATE TABLE gpkg_spatial_ref_sys (
  srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY,
  organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL,
  definition TEXT NOT NULL, description TEXT);

CREATE TABLE gpkg_contents (
  table_name TEXT PRIMARY KEY, data_type TEXT NOT NULL,
  identifier TEXT UNIQUE, description TEXT DEFAULT '',
  last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE,
  srs_id INTEGER, CONSTRAINT fk_gc_r_srs_id FOREIGN KEY (srs_id) REFERENCES gpkg_spatial_ref_sys(srs_id));

CREATE TABLE gpkg_geometry_columns (
  table_name TEXT NOT NULL, column_name TEXT NOT NULL, geometry_type_name TEXT NOT NULL,
  srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL,
  CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name));");

        const string wgs84 =
            "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]]," +
            "PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]";
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
INSERT INTO gpkg_spatial_ref_sys VALUES
 ('WGS 84', 4326, 'EPSG', 4326, $wgs84, NULL),
 ('Undefined cartesian SRS', -1, 'NONE', -1, 'undefined', NULL),
 ('Undefined geographic SRS', 0, 'NONE', 0, 'undefined', NULL);";
        cmd.Parameters.AddWithValue("$wgs84", wgs84);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Write one point layer. Returns the number of features written.</summary>
    public int WritePointLayer(string tableName, IReadOnlyList<GpkgField> fields,
                               IEnumerable<GpkgFeature> features, string? description = null)
    {
        string t = Sanitize(tableName);

        var cols = string.Join(",\n  ", fields.Select(
            (f, i) => $"\"{Sanitize(f.Name)}\" {SqlType(f.Type)}"));
        Exec($@"
CREATE TABLE ""{t}"" (
  fid INTEGER PRIMARY KEY AUTOINCREMENT,
  geom BLOB{(fields.Count > 0 ? ",\n  " + cols : "")}
);");

        using var tx = _db.BeginTransaction();
        using var ins = _db.CreateCommand();
        ins.Transaction = tx;
        var names = string.Join(",", fields.Select(f => $"\"{Sanitize(f.Name)}\""));
        var parms = string.Join(",", fields.Select((_, i) => $"$p{i}"));
        ins.CommandText = fields.Count > 0
            ? $"INSERT INTO \"{t}\" (geom,{names}) VALUES ($geom,{parms});"
            : $"INSERT INTO \"{t}\" (geom) VALUES ($geom);";

        var geomParam = ins.Parameters.Add("$geom", SqliteType.Blob);
        var valueParams = new List<SqliteParameter>();
        for (int i = 0; i < fields.Count; i++)
            valueParams.Add(ins.Parameters.Add($"$p{i}", SqliteType.Text));

        int count = 0;
        double minX = double.MaxValue, minY = double.MaxValue,
               maxX = double.MinValue, maxY = double.MinValue;

        foreach (var f in features)
        {
            geomParam.Value = PointGeometry(f.Lon, f.Lat);
            for (int i = 0; i < fields.Count; i++)
            {
                object? v = i < f.Values.Length ? f.Values[i] : null;
                valueParams[i].Value = v ?? DBNull.Value;
            }
            ins.ExecuteNonQuery();
            count++;
            if (f.Lon < minX) minX = f.Lon; if (f.Lon > maxX) maxX = f.Lon;
            if (f.Lat < minY) minY = f.Lat; if (f.Lat > maxY) maxY = f.Lat;
        }
        tx.Commit();

        if (count == 0) { minX = minY = maxX = maxY = 0; }

        using var meta = _db.CreateCommand();
        meta.CommandText = @"
INSERT INTO gpkg_contents (table_name,data_type,identifier,description,min_x,min_y,max_x,max_y,srs_id)
VALUES ($t,'features',$t,$d,$minx,$miny,$maxx,$maxy,4326);
INSERT INTO gpkg_geometry_columns VALUES ($t,'geom','POINT',4326,0,0);";
        meta.Parameters.AddWithValue("$t", t);
        meta.Parameters.AddWithValue("$d", description ?? "");
        meta.Parameters.AddWithValue("$minx", minX);
        meta.Parameters.AddWithValue("$miny", minY);
        meta.Parameters.AddWithValue("$maxx", maxX);
        meta.Parameters.AddWithValue("$maxy", maxY);
        meta.ExecuteNonQuery();

        return count;
    }

    /// <summary>Write a MULTIPOLYGON layer (field boundaries, headlands).</summary>
    public int WritePolygonLayer(string tableName, IReadOnlyList<GpkgField> fields,
                                 IEnumerable<GpkgPolygonFeature> features,
                                 string? description = null)
    {
        string t = Sanitize(tableName);
        var cols = string.Join(",\n  ", fields.Select(f => $"\"{Sanitize(f.Name)}\" {SqlType(f.Type)}"));
        Exec($@"
CREATE TABLE ""{t}"" (
  fid INTEGER PRIMARY KEY AUTOINCREMENT,
  geom BLOB{(fields.Count > 0 ? ",\n  " + cols : "")}
);");

        using var tx = _db.BeginTransaction();
        using var ins = _db.CreateCommand();
        ins.Transaction = tx;
        var names = string.Join(",", fields.Select(f => $"\"{Sanitize(f.Name)}\""));
        var parms = string.Join(",", fields.Select((_, i) => $"$p{i}"));
        ins.CommandText = fields.Count > 0
            ? $"INSERT INTO \"{t}\" (geom,{names}) VALUES ($geom,{parms});"
            : $"INSERT INTO \"{t}\" (geom) VALUES ($geom);";

        var geomParam = ins.Parameters.Add("$geom", SqliteType.Blob);
        var valueParams = new List<SqliteParameter>();
        for (int i = 0; i < fields.Count; i++)
            valueParams.Add(ins.Parameters.Add($"$p{i}", SqliteType.Text));

        int count = 0;
        double minX = double.MaxValue, minY = double.MaxValue,
               maxX = double.MinValue, maxY = double.MinValue;

        foreach (var f in features)
        {
            if (f.Polygons.Count == 0) continue;
            geomParam.Value = MultiPolygonGeometry(f.Polygons);
            for (int i = 0; i < fields.Count; i++)
                valueParams[i].Value = (i < f.Values.Length ? f.Values[i] : null) ?? (object)DBNull.Value;
            ins.ExecuteNonQuery();
            count++;
            foreach (var poly in f.Polygons)
                foreach (var (lon, lat) in poly.Exterior.Points)
                {
                    if (lon < minX) minX = lon; if (lon > maxX) maxX = lon;
                    if (lat < minY) minY = lat; if (lat > maxY) maxY = lat;
                }
        }
        tx.Commit();
        if (count == 0) { minX = minY = maxX = maxY = 0; }

        using var meta = _db.CreateCommand();
        meta.CommandText = @"
INSERT INTO gpkg_contents (table_name,data_type,identifier,description,min_x,min_y,max_x,max_y,srs_id)
VALUES ($t,'features',$t,$d,$minx,$miny,$maxx,$maxy,4326);
INSERT INTO gpkg_geometry_columns VALUES ($t,'geom','MULTIPOLYGON',4326,0,0);";
        meta.Parameters.AddWithValue("$t", t);
        meta.Parameters.AddWithValue("$d", description ?? "");
        meta.Parameters.AddWithValue("$minx", minX);
        meta.Parameters.AddWithValue("$miny", minY);
        meta.Parameters.AddWithValue("$maxx", maxX);
        meta.Parameters.AddWithValue("$maxy", maxY);
        meta.ExecuteNonQuery();
        return count;
    }

    /// <summary>GeoPackage BLOB: "GP" header + little-endian WKB multipolygon.</summary>
    public static byte[] MultiPolygonGeometry(IReadOnlyList<GpkgPolygon> polygons)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'G'); w.Write((byte)'P');
        w.Write((byte)0);            // version
        w.Write((byte)0b0000_0001);  // little-endian, no envelope
        w.Write(4326);

        w.Write((byte)1);            // WKB little-endian
        w.Write((uint)6);            // MultiPolygon
        w.Write((uint)polygons.Count);
        foreach (var poly in polygons)
        {
            w.Write((byte)1);
            w.Write((uint)3);        // Polygon
            var rings = new List<GpkgRing> { poly.Exterior };
            rings.AddRange(poly.Interior);
            w.Write((uint)rings.Count);
            foreach (var ring in rings)
            {
                // WKB requires closed rings.
                var pts = new List<(double Lon, double Lat)>(ring.Points);
                if (pts.Count > 0 && (pts[0].Lon != pts[^1].Lon || pts[0].Lat != pts[^1].Lat))
                    pts.Add(pts[0]);
                w.Write((uint)pts.Count);
                foreach (var (lon, lat) in pts) { w.Write(lon); w.Write(lat); }
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>GeoPackage BLOB: "GP" header + little-endian WKB point.</summary>
    public static byte[] PointGeometry(double lon, double lat)
    {
        var buf = new byte[8 + 21];
        buf[0] = (byte)'G'; buf[1] = (byte)'P';
        buf[2] = 0;                    // version
        buf[3] = 0b0000_0001;          // flags: little-endian, no envelope
        BitConverter.GetBytes(4326).CopyTo(buf, 4);

        int o = 8;
        buf[o] = 1;                                       // WKB byte order: LE
        BitConverter.GetBytes((uint)1).CopyTo(buf, o + 1); // geometry type: Point
        BitConverter.GetBytes(lon).CopyTo(buf, o + 5);
        BitConverter.GetBytes(lat).CopyTo(buf, o + 13);
        return buf;
    }

    private static string SqlType(GpkgType t) => t switch
    {
        GpkgType.Double => "REAL",
        GpkgType.Integer => "INTEGER",
        GpkgType.DateTime => "DATETIME",
        _ => "TEXT",
    };

    /// <summary>Make an identifier safe for SQLite and friendly in the QGIS layer list.</summary>
    public static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "layer";
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var outp = new string(chars).Trim('_');
        while (outp.Contains("__")) outp = outp.Replace("__", "_");
        if (outp.Length == 0) outp = "layer";
        if (char.IsDigit(outp[0])) outp = "_" + outp;
        return outp.Length > 60 ? outp.Substring(0, 60) : outp;
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
