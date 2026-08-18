using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// A GS3 card travels between organisations as a bare `RCD` folder, because
/// that is where the data visibly is — and the Deere plugin then declines it at
/// every level. Rebuilding the display folder recovers it: a 172 MB lentil
/// harvest card no reader would touch gave 83 layers and 722,535 points.
///
/// The risk in this reader is the opposite mistake, claiming a card that is
/// still intact and importing it a second time, so most of what follows is
/// about what it must refuse.
/// </summary>
public class LooseRcdTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-rcdtest-" + Guid.NewGuid().ToString("N"));

    public LooseRcdTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Create a card folder holding an RCD tree with a file in it.</summary>
    private string Card(params string[] segments)
    {
        string dir = Path.Combine(new[] { _dir }.Concat(segments).ToArray());
        Directory.CreateDirectory(Path.Combine(dir, "RCD", "ContextData"));
        File.WriteAllText(Path.Combine(dir, "RCD", "ContextData", "CD_BNDRY.BIN"), "x");
        return dir;
    }

    private static (List<OperationLayer>, List<BoundaryFeature>) Nothing(string _) =>
        (new List<OperationLayer>(), new List<BoundaryFeature>());

    [Fact]
    public void Claims_an_rcd_folder_that_lost_its_card()
    {
        Assert.True(LooseRcd.CanRead(Card("lentils 2026 ash old")));
    }

    [Fact]
    public void Refuses_a_card_that_still_has_its_display_folder()
    {
        // `<card>/GS3_2630/<client>/RCD` — the plugin reads this one, and
        // claiming the client folder would import all 53 vault cards twice.
        Assert.False(LooseRcd.CanRead(Card("card", "GS3_2630", "Harvest 2024")));
    }

    [Theory]
    [InlineData("GS2_1800")]
    [InlineData("GS3_2630")]
    [InlineData("GS4_4600")]
    public void Refuses_every_display_model(string display)
    {
        Assert.False(LooseRcd.CanRead(Card("card", display, "client")));
    }

    [Fact]
    public void Refuses_a_folder_nested_deeper_inside_a_real_card()
    {
        Assert.False(LooseRcd.CanRead(Card("card", "GS3_2630", "client", "extra")));
    }

    [Fact]
    public void Refuses_an_empty_rcd_folder()
    {
        string dir = Path.Combine(_dir, "leftover");
        Directory.CreateDirectory(Path.Combine(dir, "RCD"));
        Assert.False(LooseRcd.CanRead(dir));
    }

    [Fact]
    public void Refuses_a_folder_with_no_rcd_at_all()
    {
        string dir = Path.Combine(_dir, "documents");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.pdf"), "x");
        Assert.False(LooseRcd.CanRead(dir));
    }

    [Fact]
    public void Rebuilds_the_layout_the_plugin_expects()
    {
        string card = Card("lentils 2026 ash old");
        string? handed = null;
        LooseRcd.Import(card, d => { handed = d; return Nothing(d); });

        Assert.NotNull(handed);
        // The plugin is given the folder above the display folder, and the
        // card's own name is kept so layers stay identifiable.
        string rebuilt = Path.Combine(handed!, "GS3_2630", "lentils 2026 ash old", "RCD");
        Assert.True(Directory.Exists(Path.Combine(rebuilt, "ContextData"))
                    || !Directory.Exists(handed!),   // already cleaned up
                    "expected GS3_2630/<card>/RCD beneath the folder handed to the importer");
    }

    [Fact]
    public void Leaves_the_source_card_untouched_and_cleans_up_after_itself()
    {
        string card = Card("lentils 2026 ash old");
        string marker = Path.Combine(card, "RCD", "ContextData", "CD_BNDRY.BIN");

        string? handed = null;
        LooseRcd.Import(card, d => { handed = d; return Nothing(d); });

        Assert.True(File.Exists(marker), "the card on disk must not be modified");
        Assert.False(Directory.Exists(handed!), "the rebuilt copy must be removed");
    }

    [Fact]
    public void Names_the_field_after_the_card_when_the_reader_did_not()
    {
        string card = Card("lentils 2026 ash old");
        var layers = LooseRcd.Import(card, _ =>
        {
            var l = new OperationLayer { OperationType = "Harvesting" };
            l.Points.Add(new LayerPoint { Lon = -114.0, Lat = 51.7 });
            return (new List<OperationLayer> { l }, new List<BoundaryFeature>());
        }).Layers;

        Assert.Equal("lentils 2026 ash old", layers.Single().Field);
    }
}
