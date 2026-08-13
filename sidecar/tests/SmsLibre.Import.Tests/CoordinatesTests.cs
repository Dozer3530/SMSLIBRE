using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// The coordinate rule guards every reader, so it is tested directly rather
/// than only through the data corpus (which is not present in CI). The bad
/// values below are real: they came out of Olds College harvest cards.
/// </summary>
public class CoordinatesTests
{
    [Theory]
    [InlineData(-114.092659, 51.795470)]   // Olds, Alberta — a normal fix
    [InlineData(0.0, 51.795470)]           // prime meridian is a real longitude
    [InlineData(-180.0, -90.0)]            // domain corners are valid
    [InlineData(180.0, 90.0)]
    public void Accepts_real_positions(double lon, double lat)
        => Assert.True(Coordinates.IsPlausible(lon, lat));

    [Theory]
    [InlineData(-40.265241, -114.092659)]  // lat/lon swapped by the logger
    [InlineData(-117.755084, 95.813683)]   // latitude past the pole
    [InlineData(-214.546137, -214.358323)] // both axes corrupt
    public void Rejects_the_corrupt_fixes_found_in_the_vault(double lon, double lat)
        => Assert.False(Coordinates.IsPlausible(lon, lat));

    [Fact]
    public void Rejects_null_island()
    {
        // Displays emit (0,0) before they acquire a satellite lock. It is a
        // valid coordinate but never a valid field position.
        Assert.False(Coordinates.IsPlausible(0.0, 0.0));
        Assert.False(Coordinates.IsPlausible(1e-12, -1e-12));
    }

    [Fact]
    public void Rejects_non_finite_values()
    {
        Assert.False(Coordinates.IsPlausible(double.NaN, 51.0));
        Assert.False(Coordinates.IsPlausible(-114.0, double.NaN));
        Assert.False(Coordinates.IsPlausible(double.PositiveInfinity, 51.0));
        Assert.False(Coordinates.IsPlausible(-114.0, double.NegativeInfinity));
    }
}
