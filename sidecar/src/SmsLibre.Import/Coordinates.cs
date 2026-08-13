// One place for the rule that decides whether a logged position is real.
//
// Every reader needs this and they must agree. They did not: RavenReader
// range-checked its coordinates while AdaptHost only skipped (0,0), so corrupt
// GPS fixes from the ADAPT plugins flowed straight into the GeoPackage. A
// vault-wide scan found them on three ISOXML harvest cards — one 2021 combine
// card had 6 in 5,200 points, including latitude -214 and latitude 95.8. A
// single bad fix stretches a layer's extent across the globe and ruins every
// map and classification built from it, so the cost of missing one is high and
// the cost of dropping a genuine point is negligible at these volumes.

using System;

namespace SmsLibre.Import;

public static class Coordinates
{
    /// <summary>
    /// True when (lon, lat) is a position a receiver could legitimately report.
    /// Rejects non-finite values, anything outside the WGS 84 domain, and the
    /// (0, 0) "null island" fix that displays emit before they acquire a lock.
    /// </summary>
    public static bool IsPlausible(double lon, double lat)
        => double.IsFinite(lon) && double.IsFinite(lat)
           && Math.Abs(lat) <= 90.0 && Math.Abs(lon) <= 180.0
           && !(Math.Abs(lon) < 1e-9 && Math.Abs(lat) < 1e-9);
}
