using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// The archive reader is offered every zip in a vault — 3,203 folders held one
/// in the Olds College sweep, and the overwhelming majority were shapefile
/// exports and document bundles. Claiming those would mean extracting hundreds
/// of megabytes to discover there was never a card, so what counts as a card is
/// decided from the entry list alone and is worth pinning down.
/// </summary>
public class ArchivedCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-arch-" + Guid.NewGuid().ToString("N"));

    public ArchivedCardTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Zip(string name, params string[] entries)
    {
        string path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var e in entries)
        {
            var entry = zip.CreateEntry(e);
            using var w = new StreamWriter(entry.Open());
            w.Write("x");
        }
        return path;
    }

    [Fact]
    public void Claims_an_isoxml_archive()
    {
        Zip("job.jdp", "TASKDATA.XML", "DDOP.XML");
        Assert.True(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void Claims_a_john_deere_card_nested_under_a_machine_folder()
    {
        // The real shape from 2. Saskler: the card is three levels down.
        Zip("JD 9770 #1.zip",
            "9770 #1/GS3_2630/Randy and Stephen/RCD/Applications/CSDRModelOffsets.sdm");
        Assert.True(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void Claims_an_archive_of_gen4_logs()
    {
        Zip("1T_Silage.zip", "2025_08_26_[08_46]_{32991de6}.jdl");
        Assert.True(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void Ignores_the_shapefile_and_document_archives_that_fill_a_vault()
    {
        Zip("Processed-20240102T212031Z-001.zip",
            "fields.shp", "fields.dbf", "fields.shx", "fields.prj");
        Zip("fwdsoiltestresultsfall23.zip", "results.pdf", "notes.docx", "map.png");
        Assert.False(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void Leaves_raven_slingshot_packages_to_their_own_reader()
    {
        // A .jdp.zip is RavenReader's format. Claiming it here would shadow the
        // reader that understands its .tab layout.
        Zip("20W_WHEAT.jdp.zip", "job/job_fmis/jobdata.xml", "job/job_fmis/Product_1.tab");
        Assert.False(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_not_a_card()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.zip"), "this is not a zip");
        Assert.False(ArchivedCard.CanRead(_dir));
    }

    [Fact]
    public void Picks_up_every_card_archive_in_a_folder()
    {
        Zip("a.zip", "9770/GS3_2630/x/RCD/f.sdm");
        Zip("b.zip", "TASKDATA.XML");
        Zip("c.zip", "photos/img.png");           // not a card
        Assert.Equal(2, ArchivedCard.Archives(_dir).Count());
    }
}
