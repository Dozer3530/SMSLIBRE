using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// Three Saskler PreSeed cards each carry one orphaned .fdl whose .fdd is gone,
/// and the Deere plugin's FileNotFoundException killed the whole import — one
/// missing file costing ~140 intact documents. The repair copies the card to
/// temp without the orphans and retries. What must never happen is the inverse:
/// repairing (and paying a full card copy) for a failure that is not the card's.
/// </summary>
public class CardRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-repairtest-" + Guid.NewGuid().ToString("N"));

    public CardRepairTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Card(params string[] files)
    {
        string card = Path.Combine(_dir, "PreSeed");
        string eic = Path.Combine(card, "GS3_2630", "4940", "RCD", "EIC");
        Directory.CreateDirectory(eic);
        foreach (var f in files)
            File.WriteAllText(Path.Combine(eic, f), "x");
        return card;
    }

    [Fact]
    public void Repairs_when_the_missing_file_is_an_orphan_inside_the_card()
    {
        string card = Card("a.fdd", "a.fdl", "orphan.fdl");   // orphan has no .fdd
        var ex = new FileNotFoundException("missing",
            Path.Combine(card, "GS3_2630", "4940", "RCD", "EIC", "orphan.fdd"));

        Assert.True(CardRepair.CanRepair(card, ex));
    }

    [Fact]
    public void Does_not_repair_when_the_missing_file_is_outside_the_card()
    {
        // A plugin dependency missing beside the executable is not card damage;
        // copying 200 MB of card would be pure waste and hide the real problem.
        string card = Card("a.fdd", "a.fdl", "orphan.fdl");
        var ex = new FileNotFoundException("missing",
            Path.Combine(Path.GetTempPath(), "SomePluginDependency.dll"));

        Assert.False(CardRepair.CanRepair(card, ex));
    }

    [Fact]
    public void Does_not_repair_a_card_with_no_orphans()
    {
        // Every pair intact: removing nothing changes nothing, so a retry would
        // fail identically. Report the original error instead.
        string card = Card("a.fdd", "a.fdl", "b.fdd", "b.fdl");
        var ex = new FileNotFoundException("missing",
            Path.Combine(card, "GS3_2630", "4940", "RCD", "EIC", "a.fdd"));

        Assert.False(CardRepair.CanRepair(card, ex));
    }

    [Fact]
    public void The_retry_sees_the_card_without_its_orphans()
    {
        string card = Card("a.fdd", "a.fdl", "orphan.fdl", "lone.fdd");

        List<string>? seen = null;
        CardRepair.Import(card, temp =>
        {
            seen = Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                            .Select(Path.GetFileName)
                            .OrderBy(x => x)
                            .ToList()!;
            return (new List<OperationLayer>(), new List<BoundaryFeature>());
        });

        // Both halves of the intact pair survive; both kinds of orphan are gone.
        Assert.Equal(new[] { "a.fdd", "a.fdl" }, seen);
        Assert.Equal(2, CardRepair.SkippedFiles.Count);
        Assert.Contains("orphan.fdl", CardRepair.SkippedFiles);
        Assert.Contains("lone.fdd", CardRepair.SkippedFiles);
    }

    [Fact]
    public void The_source_card_is_untouched_and_the_temp_copy_removed()
    {
        string card = Card("a.fdd", "a.fdl", "orphan.fdl");
        string? temp = null;
        CardRepair.Import(card, d =>
        {
            temp = d;
            return (new List<OperationLayer>(), new List<BoundaryFeature>());
        });

        Assert.True(File.Exists(Path.Combine(
            card, "GS3_2630", "4940", "RCD", "EIC", "orphan.fdl")));
        Assert.False(Directory.Exists(temp!));
    }
}
