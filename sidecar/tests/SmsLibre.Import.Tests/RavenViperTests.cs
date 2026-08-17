using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// The Viper record layout was recovered from the files, not from a
/// specification, so it is pinned here with a synthetic job built to the same
/// shape. The values below are the real ones from a 2025 spraying job: a fix at
/// the Smart Farm, 1,030 m up, moving at 1.4 m/s.
/// </summary>
public class RavenViperTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-viper-" + Guid.NewGuid().ToString("N"));

    public RavenViperTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static byte[] Position(uint seconds, double latDeg, double lonDeg,
                                   float alt, float speed, float distance)
    {
        var b = new byte[41];
        BitConverter.GetBytes((ushort)41).CopyTo(b, 0);      // length, self-inclusive
        BitConverter.GetBytes((ushort)113).CopyTo(b, 2);     // position record
        BitConverter.GetBytes(seconds).CopyTo(b, 4);
        BitConverter.GetBytes(latDeg * Math.PI / 180).CopyTo(b, 8);   // stored in radians
        BitConverter.GetBytes(lonDeg * Math.PI / 180).CopyTo(b, 16);
        BitConverter.GetBytes(alt).CopyTo(b, 24);
        BitConverter.GetBytes(speed).CopyTo(b, 28);
        BitConverter.GetBytes(distance).CopyTo(b, 32);
        return b;
    }

    private static byte[] Other(ushort type, int length)
    {
        var b = new byte[length];
        BitConverter.GetBytes((ushort)length).CopyTo(b, 0);
        BitConverter.GetBytes(type).CopyTo(b, 2);
        return b;
    }

    /// <summary>A job at …/GFF/grower/farm/field/Jobs/name.jdp.</summary>
    private string Job(string grower, string farm, string field, string name,
                       params byte[][] records)
    {
        string dir = Path.Combine(_dir, "GFF", grower, farm, field, "Jobs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".jdp");

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new BinaryWriter(zip.CreateEntry("{guid}.jdf").Open()))
                foreach (var r in records) w.Write(r);
            using (var w = new StreamWriter(zip.CreateEntry("DDOP.XML").Open()))
                w.Write("<ISO11783_TaskData/>");
        }
        return dir;
    }

    [Fact]
    public void Decodes_a_position_track()
    {
        string dir = Job("OLDS COLLEGE", "SMART FARM", "20W", "1T",
            Position(295946, 51.7921109, -114.0822786, 1030.30f, 1.4f, 49.4761f),
            Position(295947, 51.7921200, -114.0822000, 1030.29f, 1.3f, 50.9089f));

        var layers = RavenViperReader.Import(dir);
        var layer = Assert.Single(layers);

        Assert.Equal(new[] { "elevation", "speed", "distance" }, layer.Channels);
        Assert.Equal(2, layer.Points.Count);

        var p = layer.Points[0];
        Assert.Equal(51.7921109, p.Lat, 6);      // radians converted back
        Assert.Equal(-114.0822786, p.Lon, 6);
        Assert.Equal(1030.30, p.Values[0]!.Value, 2);
        Assert.Equal(1.4, p.Values[1]!.Value, 3);
        Assert.Equal(49.4761, p.Values[2]!.Value, 3);
    }

    [Fact]
    public void Walks_past_record_types_it_does_not_understand()
    {
        // A real job is 31 record types; only one is a fix. The framing has to
        // carry the reader over the rest.
        string dir = Job("OLDS COLLEGE", "SMART FARM", "2W", "mixed",
            Other(155, 18), Other(118, 172),
            Position(1, 51.79, -114.08, 1030f, 1.0f, 1.0f),
            Other(156, 20),
            Position(2, 51.80, -114.09, 1031f, 1.1f, 2.0f));

        Assert.Equal(2, RavenViperReader.Import(dir).Single().Points.Count);
    }

    [Fact]
    public void Takes_grower_farm_and_field_from_the_gff_path()
    {
        string dir = Job("OLDS COLLEGE", "SMART FARM", "20W", "job",
            Position(1, 51.79, -114.08, 1030f, 1f, 1f));

        var layer = RavenViperReader.Import(dir).Single();
        Assert.Equal("OLDS COLLEGE", layer.Grower);
        Assert.Equal("SMART FARM", layer.Farm);
        Assert.Equal("20W", layer.Field);
        Assert.Equal("job", layer.Description);
    }

    [Fact]
    public void Drops_the_placeholders_a_display_writes_when_no_client_is_set_up()
    {
        string dir = Job("No Grower", "No Farm", "No Field", "job",
            Position(1, 51.79, -114.08, 1030f, 1f, 1f));

        var layer = RavenViperReader.Import(dir).Single();
        Assert.Equal("", layer.Grower);
        Assert.Equal("", layer.Farm);
        Assert.Equal("", layer.Field);
    }

    [Fact]
    public void A_truncated_record_stops_the_walk_instead_of_overrunning()
    {
        // Claims 41 bytes but only 20 are present.
        var truncated = Position(1, 51.79, -114.08, 1030f, 1f, 1f).Take(20).ToArray();
        string dir = Job("g", "f", "x", "truncated",
            Position(1, 51.79, -114.08, 1030f, 1f, 1f), truncated);

        Assert.Single(RavenViperReader.Import(dir).Single().Points);
    }

    [Fact]
    public void Leaves_the_other_two_jdp_formats_alone()
    {
        // Slingshot packages and ISOXML-in-a-zip have their own readers.
        string dir = Path.Combine(_dir, "others");
        Directory.CreateDirectory(dir);
        using (var z = ZipFile.Open(Path.Combine(dir, "iso.jdp"), ZipArchiveMode.Create))
            z.CreateEntry("TASKDATA.XML");
        using (var z = ZipFile.Open(Path.Combine(dir, "slingshot.jdp.zip"), ZipArchiveMode.Create))
            z.CreateEntry("job/job_fmis/jobdata.xml");

        Assert.False(RavenViperReader.CanRead(dir));
    }
}
