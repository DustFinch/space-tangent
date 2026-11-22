// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SurfaceProfileMigrationTests
{
    private static readonly JulianInstant Epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
    private static ObserverView View(double latitude = 0) => new(Epoch, CameraMode.Surface, SolarSystemSample.MoonId, false)
    {
        EarthObserver = new(new(latitude, 20, .1), 90, 15, true, true)
        { LandscapeId = "trees", Conditions = new(5), ApparentPlaces = true, EquatorialMount = true,
            CivilClock = new("Home", "UTC", 0) }
    };
    private static LegacyObserverProfile Profile(ObserverView view, string? body) => new(view, DeltaTModel.FixedOffset) { SiteBody = body };
    private static SimulationWorld World() => new(new SharedClockTests.StaticEphemeris(), []);

    [Theory]
    [InlineData("Sol/Earth")]
    [InlineData("sol/earth")]
    [InlineData("Earth")]
    public void EarthAliasesRestoreStructuredSurfaceAndRemoveOldNavigation(string alias)
    {
        var view = View() with { Mode = CameraMode.Solar, Navigation = new(default, 100, 0, 0, SolarSystemSample.EarthId, true)
            { Placement = new(new(6400, 0, 0), new(0, 1, 0), new(1, 0, 0), BodyFixed: true) } };
        var restored = LegacyObserverProfiles.RestoreSurfaceBody(Profile(view, alias), World());
        Assert.Equal(CameraMode.Surface, restored.Mode);
        Assert.Null(restored.Navigation);
        Assert.Null(restored.EarthObserver!.Value.Body);
        Assert.Equal(view.EarthObserver, restored.EarthObserver);
        Assert.Equal(view.Epoch, restored.Epoch);
        Assert.Equal(view.Selection, restored.Selection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing-body")]
    [InlineData("Sun")]
    [InlineData("Zephyr")]
    public void UnresolvableOrUnsuitableBodiesLeaveTheObserverUntouched(string? body)
    {
        var view = View();
        Assert.Equal(view, LegacyObserverProfiles.RestoreSurfaceBody(Profile(view, body), World()));
        Assert.Equal(view with { EarthObserver = null }, LegacyObserverProfiles.RestoreSurfaceBody(Profile(view with { EarthObserver = null }, "Earth"), World()));
        var flying = view with { Mode = CameraMode.Solar, Navigation = new(default, 100, 0, 0, null, false) };
        Assert.Equal(flying, LegacyObserverProfiles.RestoreSurfaceBody(Profile(flying, "Earth"), World()));
    }

    [Theory]
    [InlineData("Nova", "demo/nova", 0)]
    [InlineData("Pyra", "demo/pyra", -45)]
    [InlineData("Aridus", "demo/aridus", 45)]
    [InlineData("Glacies", "demo/glacies", 90)]
    [InlineData("Selena", "demo/selena", -90)]
    public void ProceduralWorldMigrationBuildsAFiniteBodyFixedCameraAboveTheSurface(string path, string id, double latitude)
    {
        var world = World(); var view = View(latitude);
        var restored = LegacyObserverProfiles.RestoreSurfaceBody(Profile(view, path), world);
        Assert.Equal(CameraMode.Solar, restored.Mode);
        var navigation = Assert.IsType<NavigationCamera>(restored.Navigation);
        Assert.True(navigation.IsValid());
        Assert.Equal(new ObjectId(id), navigation.Target);
        var placement = Assert.IsType<NavigationPlacement>(navigation.Placement);
        Assert.True(placement.BodyFixed);
        Assert.InRange(Math.Abs(placement.Forward.Length - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(placement.Up.Length - 1), 0, 1e-10);
        Assert.InRange(Math.Abs(Vector3d.Dot(placement.Up, placement.Forward)), 0, 1e-10);
        Assert.True(placement.OffsetKilometers.Length >= DemoWorlds.Find(new(id))!.PolarRadiusKilometers);
        Assert.Equal(view.Epoch, restored.Epoch);
        Assert.Equal(view.Selection, restored.Selection);
        Assert.Equal(view.EarthObserver, restored.EarthObserver);
    }
}
