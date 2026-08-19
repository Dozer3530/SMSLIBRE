// SMSLIBRE — Raven Viper 4 / Viper 4+ native job files.
//
// The third thing wearing a `.jdp` extension, and the largest unread group in
// the Olds College vault: 382 files, 363 of them this shape. No ADAPT plugin
// reads it and there is no public specification, so the layout below was
// recovered from the files themselves.
//
// A native job is a zip holding
//
//   DDOP.XML        an ISO 11783 device object pool — 139 channels with units
//   <guid>.jdf      the logged data
//   <guid>.jhf      a header, same record format as the .jdf
//   <guid>.ab       guidance lines; <guid>.id, .sct  identity and sections
//
// Both .jdf and .jhf are a flat stream of records:
//
//   uint16 length (including these four bytes)
//   uint16 type
//   payload
//
// Walking that consumed a 814,636-byte file exactly, 20,006 records over 31
// types with no bytes left over, which is what makes the framing trustworthy.
//
// Record type 113 is a position fix, 41 bytes:
//
//   uint32 seconds   monotonic, one per second in the files seen
//   double latitude  RADIANS
//   double longitude RADIANS
//   float  altitude  metres
//   float  speed     m/s      (matches ground speed computed from the fixes)
//   float  distance  metres, cumulative (rises by speed × elapsed)
//   float  unknown   zero throughout the sample
//   byte   unknown
//
// Decoding one gives a 2,640-point track over a 360 m x 200 m field at
// 51.79 N, 114.08 W, 1,030 m altitude — the Smart Farm, which sits at 1,040 m.
//
// The value records were identified by correlating candidate fields against
// ground speed computed from the fixes, then checking magnitudes against the
// DVP unit table in the pool:
//
//   type 111 (58 B)  product event, ~every 14 s: two floats at offsets 18 and
//                    22 are ACTUAL and TARGET application rate. On the sample
//                    job both read 93.540 - exactly 10 US gal/ac in L/ha, and
//                    the DVP table confirms Raven stores SI and converts for
//                    display. The pool declares no volume-rate DPD at all;
//                    rates live only here. Ends with two lat/lon double pairs.
//   type 156 (20 B)  guidance, 1 Hz: heading (radians, 0..2pi), speed (m/s,
//                    correlation 1.00 against computed ground speed), and
//                    cross-track error (m).
//   type 118 (172 B) per-section work state: two length-prefixed byte arrays,
//                    one entry per section (81 on the sample sprayer). The
//                    count of non-zero entries in the first is sections on.
//   type 155         attitude (roll/pitch, +-pi/2); 157 constant mode flags -
//                    both left alone.
//
// Types 111 and 118 carry no timestamp; the stream is chronological, so their
// values are carried forward onto every following position fix.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SmsLibre.Import;

public static class RavenViperReader
{
    public const string FormatName = "Raven Viper 4 job (.jdp)";

    private const int PositionRecord = 113;
    private const int PositionLength = 41;

    /// <summary>Radians to degrees; the fixes are stored in radians.</summary>
    private const double Deg = 180.0 / Math.PI;

    public static bool CanRead(string path) => Jobs(path).Any();

    /// <summary>Native `.jdp` jobs: the file itself, or those directly in a folder.</summary>
    public static IEnumerable<string> Jobs(string path)
    {
        if (File.Exists(path))
        {
            if (IsNativeJob(path)) yield return path;
            yield break;
        }
        if (!Directory.Exists(path)) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(path, "*.jdp", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            if (IsNativeJob(f)) yield return f;
    }

    private static bool IsNativeJob(string file)
    {
        // ".jdp.zip" is the Slingshot format; a `.jdp` holding TASKDATA is
        // ISOXML and ArchivedCard unpacks it. Ours has a .jdf and neither.
        if (file.EndsWith(".jdp.zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (!file.EndsWith(".jdp", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(file);
            bool jdf = false;
            foreach (var e in zip.Entries)
            {
                if (e.Name.Equals("TASKDATA.XML", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (e.Name.EndsWith(".jdf", StringComparison.OrdinalIgnoreCase)) jdf = true;
            }
            return jdf;
        }
        catch { return false; }
    }

    public static List<OperationLayer> Import(string path)
    {
        var layers = new List<OperationLayer>();
        foreach (var job in Jobs(path))
        {
            try
            {
                var layer = ReadJob(job);
                if (layer is not null && layer.Points.Count > 0) layers.Add(layer);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [viper] {Path.GetFileName(job)}: {ex.Message}");
            }
        }
        return layers;
    }

    private static OperationLayer? ReadJob(string jobPath)
    {
        using var zip = ZipFile.OpenRead(jobPath);
        var jdf = zip.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".jdf", StringComparison.OrdinalIgnoreCase));
        if (jdf is null) return null;

        byte[] data;
        using (var s = jdf.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            data = ms.ToArray();
        }

        var (grower, farm, field) = FromGffPath(jobPath);
        var layer = new OperationLayer
        {
            Grower = grower,
            Farm = farm,
            Field = field,
            OperationType = "Raven",
            Description = Path.GetFileNameWithoutExtension(jobPath),
        };
        layer.Channels.AddRange(new[]
        {
            "elevation", "speed", "distance",
            "rate_applied", "rate_target", "sections_on", "heading", "cross_track",
        });
        layer.Units.AddRange(new[] { "m", "m/s", "m", "L/ha", "L/ha", "", "deg", "m" });

        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var s in Samples(data))
        {
            if (!Coordinates.IsPlausible(s.Lon, s.Lat)) continue;
            layer.Points.Add(new LayerPoint
            {
                Lon = s.Lon,
                Lat = s.Lat,
                // The counter is monotonic and one-per-second but its origin is
                // not established, so it is offered as an offset rather than
                // dressed up as a wall-clock time it may not be.
                Timestamp = epoch.AddSeconds(s.Seconds),
                Values = new double?[]
                {
                    s.Alt, s.Speed, s.Distance,
                    s.RateApplied, s.RateTarget, s.SectionsOn, s.Heading, s.CrossTrack,
                },
            });
        }
        return layer;
    }

    private sealed record Sample(
        uint Seconds, double Lat, double Lon, double Alt, double Speed,
        double Distance, double? RateApplied, double? RateTarget,
        double? SectionsOn, double? Heading, double? CrossTrack);

    private const int ProductRecord = 111;
    private const int SectionRecord = 118;
    private const int GuidanceRecord = 156;

    /// <summary>
    /// Every position fix in file order, each carrying the latest value from
    /// the rate, guidance and section records that precede it in the stream.
    /// </summary>
    private static IEnumerable<Sample> Samples(byte[] d)
    {
        double? rateApplied = null, rateTarget = null;
        double? sectionsOn = null, heading = null, crossTrack = null;

        int off = 0;
        while (off + 4 <= d.Length)
        {
            ushort len = BitConverter.ToUInt16(d, off);
            ushort type = BitConverter.ToUInt16(d, off + 2);
            // A length under four cannot advance, and one past the end means the
            // framing is wrong; either way stop rather than loop or overrun.
            if (len < 4 || off + len > d.Length) yield break;

            switch (type)
            {
                case ProductRecord when len >= 26:
                {
                    float applied = BitConverter.ToSingle(d, off + 18);
                    float target = BitConverter.ToSingle(d, off + 22);
                    if (float.IsFinite(applied)) rateApplied = applied;
                    if (float.IsFinite(target)) rateTarget = target;
                    break;
                }
                case GuidanceRecord when len >= 20:
                {
                    float hdg = BitConverter.ToSingle(d, off + 8);
                    float xte = BitConverter.ToSingle(d, off + 16);
                    if (float.IsFinite(hdg)) heading = hdg * Deg;
                    if (float.IsFinite(xte)) crossTrack = xte;
                    break;
                }
                case SectionRecord when len >= 8:
                {
                    // Two length-prefixed byte arrays; the first is one work
                    // state per section. Only trusted when the lengths add up.
                    int n1 = BitConverter.ToUInt16(d, off + 6);
                    if (8 + n1 + 2 <= len)
                    {
                        int on = 0;
                        for (int i = 0; i < n1; i++)
                            if (d[off + 8 + i] != 0) on++;
                        sectionsOn = on;
                    }
                    break;
                }
                case PositionRecord when len == PositionLength:
                {
                    uint seconds = BitConverter.ToUInt32(d, off + 4);
                    double lat = BitConverter.ToDouble(d, off + 8) * Deg;
                    double lon = BitConverter.ToDouble(d, off + 16) * Deg;
                    float alt = BitConverter.ToSingle(d, off + 24);
                    float speed = BitConverter.ToSingle(d, off + 28);
                    float dist = BitConverter.ToSingle(d, off + 32);
                    yield return new Sample(seconds, lat, lon, alt, speed, dist,
                                            rateApplied, rateTarget, sectionsOn,
                                            heading, crossTrack);
                    break;
                }
            }
            off += len;
        }
    }

    /// <summary>
    /// Grower, farm and field from the path. Raven lays jobs out as
    /// `…/GFF/<grower>/<farm>/<field>/Jobs/<job>.jdp`, and the placeholders it
    /// writes when a display has no client set up ("No Grower") are dropped
    /// rather than carried into QGIS as if they were names.
    /// </summary>
    private static (string Grower, string Farm, string Field) FromGffPath(string jobPath)
    {
        var parts = Path.GetFullPath(jobPath)
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int gff = Array.FindLastIndex(parts,
            p => p.Equals("GFF", StringComparison.OrdinalIgnoreCase));
        if (gff < 0 || gff + 3 >= parts.Length) return ("", "", "");

        static string Clean(string s) =>
            s.StartsWith("No ", StringComparison.OrdinalIgnoreCase) ? "" : s;

        return (Clean(parts[gff + 1]), Clean(parts[gff + 2]), Clean(parts[gff + 3]));
    }
}
