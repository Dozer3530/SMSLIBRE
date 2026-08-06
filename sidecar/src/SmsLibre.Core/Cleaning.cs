// SMSLIBRE — basic yield-point cleaning.
//
// Raw combine logs carry non-harvest readings (zeros at headland turns), stray
// GPS fixes, and extreme outliers. This is a first approximation of what SMS's
// native ALP_PreprocessorDll does; it makes raw data legible without claiming to
// reproduce SMS's exact algorithm (that lives in the native core — see the
// salvage ledger). Refine here as the real cleaner is understood.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SmsLibre.Core;

public static class Cleaning
{
    public static IReadOnlyList<YieldPoint> Clean(
        IReadOnlyList<YieldPoint> pts,
        double valueClipLoPct = 2, double valueClipHiPct = 98,
        double spatialClipPct = 0.5)
    {
        // Drop non-positive values and null-island coordinates.
        var kept = pts.Where(p => p.Value > 0 &&
                                  (Math.Abs(p.Lon) > 1e-6 || Math.Abs(p.Lat) > 1e-6))
                      .ToList();
        if (kept.Count == 0) return kept;

        // Spatial outlier clip (stray GPS fixes stretch the extent).
        double lonLo = Pct(kept.Select(p => p.Lon), spatialClipPct);
        double lonHi = Pct(kept.Select(p => p.Lon), 100 - spatialClipPct);
        double latLo = Pct(kept.Select(p => p.Lat), spatialClipPct);
        double latHi = Pct(kept.Select(p => p.Lat), 100 - spatialClipPct);
        kept = kept.Where(p => p.Lon >= lonLo && p.Lon <= lonHi &&
                               p.Lat >= latLo && p.Lat <= latHi).ToList();

        // Value outlier clip.
        double vLo = Pct(kept.Select(p => p.Value), valueClipLoPct);
        double vHi = Pct(kept.Select(p => p.Value), valueClipHiPct);
        return kept.Where(p => p.Value >= vLo && p.Value <= vHi).ToList();
    }

    private static double Pct(IEnumerable<double> values, double pct)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        if (sorted.Length == 0) return 0;
        double pos = Math.Clamp(pct / 100.0, 0, 1) * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
