// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class LegacyObserverProfileTests
{
    [ArchiveSettingsFact]
    public void ImportsTheArchivedMoscowProfileAndKeepsTheExistingView()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SETTINGS")!;
        byte[] original = File.ReadAllBytes(path);
        var now = JulianInstant.FromJulianDate(2455755.0, TimeScale.Tdb);
        var satellite = new ObjectId("norad/26997");
        var view = new ObserverView(now, CameraMode.Solar, satellite, false)
        {
            Navigation = new(new Vector3d(1, 2, 3), 42, .12, .2, satellite, true),
            Sky = new(true, false, true, true),
            EarthObserver = EarthObserverCamera.Tromso with
            {
                Conditions = new GroundSkyConditions(9, -10, 900), LandscapeId = "trees"
            }
        };
        var result = LegacyObserverProfiles.Read(path, view, DeltaTModel.EspenakMeeus2006);
        var observer = result.Observer;
        Assert.Equal(now, observer.Epoch);
        Assert.Equal(view.Mode, observer.Mode);
        Assert.Equal(view.Selection, observer.Selection);
        Assert.Equal(view.Navigation, observer.Navigation);
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, result.DeltaT);
        Assert.Equal(50, observer.FieldOfViewDegrees);
        Assert.True(observer.Sky.Stars && observer.Sky.DeepSky && observer.Sky.Labels && observer.Sky.Meteors);
        Assert.False(observer.Sky.EquatorialGrid || observer.Sky.Constellations);
        var camera = Assert.IsType<EarthObserverCamera>(observer.EarthObserver);
        Assert.Equal(new EarthSite(55.7558, 37.6173, .156), camera.Site);
        Assert.True(camera.CardinalPoints && camera.HorizonGrid);
        Assert.Null(camera.LandscapeId);
        var conditions = Assert.IsType<GroundSkyConditions>(camera.Conditions);
        Assert.Equal(4, conditions.Bortle);
        Assert.Equal(10, conditions.TemperatureCelsius);
        Assert.Null(conditions.PressureHectopascals);
        Assert.False(conditions.Scintillation);
        Assert.True(conditions.Refraction);
        Assert.NotNull(observer.Optics);
        Assert.False(observer.Optics.Active);
        Assert.True(observer.Optics.Profile.Inverted);
        Assert.Equal(10, observer.Optics.Profile.Eyepiece.FocalMillimeters);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private sealed class ArchiveSettingsFactAttribute : FactAttribute
    {
        public ArchiveSettingsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_LEGACY_SETTINGS")))
                Skip = "Set the path to the archived settings document.";
        }
    }
}
