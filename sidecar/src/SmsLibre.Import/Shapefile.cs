// SMSLIBRE — just enough shapefile to read a prescription.
//
// ISOXML carries a variable-rate plan as an external shapefile: TASKDATA names
// the file and the attribute holding the rate, and the polygons live in the
// .shp with their attributes in the .dbf beside it. Both formats are published
// and small, and the alternative — a GDAL dependency — would dwarf the sidecar
// for the sake of two file layouts.
//
// Deliberately partial: polygon geometry (shape types 5, 15 and 25) and the
// dBASE fields a prescription uses. Anything else is skipped rather than
// guessed at, so an unexpected file yields nothing instead of nonsense.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SmsLibre.Import;

/// <summary>One shapefile record: rings plus its attribute row.</summary>
public sealed class ShapeFeature
{
    /// <summary>Rings of (lon, lat). The first is the exterior.</summary>
    public List<List<(double Lon, double Lat)>> Rings { get; } = new();
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Shapefile
{
    private const int Polygon = 5, PolygonZ = 15, PolygonM = 25;

    /// <summary>
    /// Read a .shp and its .dbf. <paramref name="shpPath"/> may name either;
    /// the pair is matched by extension.
    /// </summary>
    public static List<ShapeFeature> Read(string shpPath)
    {
        string stem = Path.Combine(Path.GetDirectoryName(shpPath) ?? "",
                                   Path.GetFileNameWithoutExtension(shpPath));
        var features = ReadShapes(stem + ".shp");
        var rows = ReadDbf(stem + ".dbf");

        // A shapefile pairs the Nth shape with the Nth record; a mismatch means
        // one of the two is truncated, so attributes are attached only as far
        // as both agree rather than shifting every row.
        for (int i = 0; i < Math.Min(features.Count, rows.Count); i++)
            foreach (var kv in rows[i])
                features[i].Attributes[kv.Key] = kv.Value;

        return features;
    }

    private static List<ShapeFeature> ReadShapes(string path)
    {
        var result = new List<ShapeFeature>();
        if (!File.Exists(path)) return result;

        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 100) return result;

        int off = 100;                       // fixed header
        while (off + 8 <= d.Length)
        {
            int contentWords = ReadInt32BE(d, off + 4);
            int next = off + 8 + contentWords * 2;
            if (contentWords <= 0 || next > d.Length) break;

            int p = off + 8;
            int shapeType = BitConverter.ToInt32(d, p);
            if (shapeType is Polygon or PolygonZ or PolygonM)
            {
                var f = new ShapeFeature();
                int numParts = BitConverter.ToInt32(d, p + 36);
                int numPoints = BitConverter.ToInt32(d, p + 40);
                int partsAt = p + 44;
                int pointsAt = partsAt + numParts * 4;

                if (numParts > 0 && numPoints > 0 &&
                    pointsAt + numPoints * 16 <= d.Length)
                {
                    for (int i = 0; i < numParts; i++)
                    {
                        int start = BitConverter.ToInt32(d, partsAt + i * 4);
                        int end = i + 1 < numParts
                            ? BitConverter.ToInt32(d, partsAt + (i + 1) * 4)
                            : numPoints;
                        if (start < 0 || end > numPoints || end - start < 3) continue;

                        var ring = new List<(double, double)>(end - start);
                        for (int k = start; k < end; k++)
                        {
                            double x = BitConverter.ToDouble(d, pointsAt + k * 16);
                            double y = BitConverter.ToDouble(d, pointsAt + k * 16 + 8);
                            if (Coordinates.IsPlausible(x, y)) ring.Add((x, y));
                        }
                        if (ring.Count >= 3) f.Rings.Add(ring);
                    }
                }
                if (f.Rings.Count > 0) result.Add(f);
            }
            off = next;
        }
        return result;
    }

    /// <summary>dBASE III table: fixed-width rows described by a field header.</summary>
    private static List<Dictionary<string, string>> ReadDbf(string path)
    {
        var rows = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return rows;

        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 32) return rows;

        int count = BitConverter.ToInt32(d, 4);
        int headerLen = BitConverter.ToUInt16(d, 8);
        int recordLen = BitConverter.ToUInt16(d, 10);
        if (headerLen <= 32 || recordLen <= 0) return rows;

        var fields = new List<(string Name, int Offset, int Length)>();
        int at = 32, column = 1;             // byte 0 of a row is the deletion flag
        while (at + 32 <= headerLen && at < d.Length && d[at] != 0x0D)
        {
            string name = Encoding.ASCII.GetString(d, at, 11).TrimEnd('\0', ' ');
            int len = d[at + 16];
            if (name.Length > 0) fields.Add((name, column, len));
            column += len;
            at += 32;
        }

        for (int r = 0; r < count; r++)
        {
            int start = headerLen + r * recordLen;
            if (start + recordLen > d.Length) break;
            if (d[start] == (byte)'*') continue;           // deleted row

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, o, len) in fields)
            {
                if (start + o + len > d.Length) break;
                row[name] = Encoding.ASCII.GetString(d, start + o, len).Trim();
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Shapefile record headers are big-endian; everything else is not.</summary>
    private static int ReadInt32BE(byte[] d, int off) =>
        (d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3];

    /// <summary>Parse a dBASE numeric field, which is text.</summary>
    public static double? Number(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;
}
