using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// Shapefile and dBASE parsing is fiddly offset arithmetic, and a prescription
/// is a number someone applies to a field — a rate read from the wrong column
/// or scaled wrong is worse than no rate at all. Built here from bytes rather
/// than from a fixture so the layout assumptions are visible.
/// </summary>
public class PrescriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-rx-" + Guid.NewGuid().ToString("N"));

    public PrescriptionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void PutBE(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    /// <summary>A .shp holding one square polygon per entry.</summary>
    private void WriteShp(string stem, params (double Lon, double Lat)[] corners)
    {
        int numPoints = corners.Length;
        int content = 44 + 4 + numPoints * 16;             // header + 1 part + points
        var b = new byte[100 + 8 + content];
        PutBE(b, 0, 9994);                                  // file code
        PutBE(b, 24, (100 + 8 + content) / 2);              // length in 16-bit words
        BitConverter.GetBytes(1000).CopyTo(b, 28);          // version
        BitConverter.GetBytes(5).CopyTo(b, 32);             // shape type: polygon

        PutBE(b, 100, 1);                                   // record number
        PutBE(b, 104, content / 2);
        int p = 108;
        BitConverter.GetBytes(5).CopyTo(b, p);              // record shape type
        BitConverter.GetBytes(1).CopyTo(b, p + 36);         // numParts
        BitConverter.GetBytes(numPoints).CopyTo(b, p + 40);
        BitConverter.GetBytes(0).CopyTo(b, p + 44);         // part 0 starts at point 0
        for (int i = 0; i < numPoints; i++)
        {
            BitConverter.GetBytes(corners[i].Lon).CopyTo(b, p + 48 + i * 16);
            BitConverter.GetBytes(corners[i].Lat).CopyTo(b, p + 48 + i * 16 + 8);
        }
        File.WriteAllBytes(Path.Combine(_dir, stem + ".shp"), b);
    }

    /// <summary>A dBASE III table with one numeric and one character column.</summary>
    private void WriteDbf(string stem, string col, params string[] values)
    {
        const int nameLen = 254, rateLen = 15;
        int headerLen = 32 + 32 * 2 + 1;
        int recordLen = 1 + nameLen + rateLen;
        var b = new List<byte>();
        b.Add(0x03);
        b.AddRange(new byte[] { 24, 1, 1 });                 // date
        b.AddRange(BitConverter.GetBytes(values.Length));
        b.AddRange(BitConverter.GetBytes((ushort)headerLen));
        b.AddRange(BitConverter.GetBytes((ushort)recordLen));
        b.AddRange(new byte[20]);

        void Field(string name, char type, int len)
        {
            var raw = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(raw, 0);
            raw[11] = (byte)type;
            raw[16] = (byte)len;
            b.AddRange(raw);
        }
        Field("Product", 'C', nameLen);
        Field(col, 'N', rateLen);
        b.Add(0x0D);

        foreach (var v in values)
        {
            b.Add((byte)' ');
            var name = new byte[nameLen];
            Encoding.ASCII.GetBytes("Urea").CopyTo(name, 0);
            for (int i = 4; i < nameLen; i++) name[i] = (byte)' ';
            b.AddRange(name);
            var rate = new byte[rateLen];
            for (int i = 0; i < rateLen; i++) rate[i] = (byte)' ';
            Encoding.ASCII.GetBytes(v).CopyTo(rate, rateLen - v.Length);
            b.AddRange(rate);
        }
        File.WriteAllBytes(Path.Combine(_dir, stem + ".dbf"), b.ToArray());
    }

    private void WriteTaskData(string column, string unit = "lb/ac")
        => File.WriteAllText(Path.Combine(_dir, "TASKDATA.XML"), $"""
            <ISO11783_TaskData>
              <PDT A="PDT-1" B="Urea"/>
              <PFD A="PFD-1" C="Field 19"/>
              <TSK A="TSK-1" B="urea 19" E="PFD-1" G="2">
                <TZN A="0">
                  <PDV A="0006" C="PDT-1" P151_rxmap="RXMAP001.ZIP"
                       P151_rxcolumn="{column}" P151_rxunit="{unit}"/>
                </TZN>
              </TSK>
            </ISO11783_TaskData>
            """);

    private static readonly (double, double)[] Square =
    {
        (-114.02, 51.76), (-114.01, 51.76), (-114.01, 51.77), (-114.02, 51.77), (-114.02, 51.76),
    };

    [Fact]
    public void Reads_a_polygon_and_its_attributes()
    {
        WriteShp("rx", Square);
        WriteDbf("rx", "Tgt_Rate_l", "210.0");

        var f = Assert.Single(Shapefile.Read(Path.Combine(_dir, "rx.shp")));
        Assert.Equal(5, f.Rings.Single().Count);
        Assert.Equal("Urea", f.Attributes["Product"]);
        Assert.Equal(210.0, Shapefile.Number(f.Attributes["Tgt_Rate_l"]));
    }

    [Fact]
    public void Takes_the_rate_column_named_by_taskdata()
    {
        WriteShp("rx", Square);
        WriteDbf("rx", "Tgt_Rate_l", "210.0");
        WriteTaskData("Tgt_Rate_l");

        var zone = Assert.Single(PrescriptionReader.Read(_dir));
        Assert.Equal("urea 19", zone.Task);
        Assert.Equal("Field 19", zone.Field);
        Assert.Equal("Urea", zone.Product);
        Assert.Equal(210.0, zone.Rate);
        Assert.Equal("lb/ac", zone.Unit);
        Assert.Equal(5, zone.Rings.Single().Count);
    }

    [Fact]
    public void A_column_the_shapefile_does_not_have_yields_no_zone()
    {
        WriteShp("rx", Square);
        WriteDbf("rx", "Some_Other", "210.0");
        WriteTaskData("Tgt_Rate_l");

        Assert.Empty(PrescriptionReader.Read(_dir));
    }

    [Fact]
    public void A_flat_rate_task_is_not_a_prescription_map()
    {
        // No P151_rxcolumn: the rate is in the XML, there is no zone geometry.
        WriteShp("rx", Square);
        WriteDbf("rx", "Tgt_Rate_l", "210.0");
        File.WriteAllText(Path.Combine(_dir, "TASKDATA.XML"),
            """
            <ISO11783_TaskData>
              <TSK A="TSK-1" B="flat"><TZN A="0"><PDV A="0006" B="11769" C="PDT-1"/></TZN></TSK>
            </ISO11783_TaskData>
            """);

        Assert.Empty(PrescriptionReader.Read(_dir));
    }

    [Fact]
    public void Rejects_a_ring_with_an_implausible_corner()
    {
        // A corrupt vertex must not drag a zone's outline across the globe.
        WriteShp("rx", (-114.02, 51.76), (-214.5, 51.76), (-114.01, 51.77),
                       (-114.02, 51.77), (-114.02, 51.76));
        WriteDbf("rx", "Tgt_Rate_l", "210.0");

        var f = Assert.Single(Shapefile.Read(Path.Combine(_dir, "rx.shp")));
        Assert.Equal(4, f.Rings.Single().Count);          // the bad corner is gone
        Assert.All(f.Rings.Single(), c => Assert.InRange(c.Lon, -180, 180));
    }

    [Fact]
    public void A_missing_dbf_still_yields_geometry()
    {
        WriteShp("rx", Square);

        var f = Assert.Single(Shapefile.Read(Path.Combine(_dir, "rx.shp")));
        Assert.Empty(f.Attributes);
    }
}
