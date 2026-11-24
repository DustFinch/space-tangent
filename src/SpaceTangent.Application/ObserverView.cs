// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public enum ViewSplit { SideBySide, Stacked }

public enum CameraMode { Solar, Lunar, Satellites, Surface, Mission, Timeline, Probe, LunarTerrain, SolarOuter, MarsTerrain }

public readonly record struct LunarTerrainCamera(double LongitudeDegrees, double LatitudeDegrees, double FieldKilometers)
{
    public static LunarTerrainCamera Globe { get; } = new(-20.08, 9.62, 4200);
    public static LunarTerrainCamera Copernicus { get; } = new(-20.08, 9.62, 420);

    public bool IsValid() => LongitudeDegrees is >= -180 and <= 180 && LatitudeDegrees is >= -90 and <= 90 &&
        FieldKilometers is >= 50 and <= 4600;
}

public readonly record struct ObserverView(JulianInstant Epoch, CameraMode Mode, ObjectId? Selection, bool Playing)
{
    public LunarTerrainCamera? Terrain { get; init; }
    public NavigationCamera? Navigation { get; init; }
    public ObjectId? ScriptAimTarget { get; init; }
    public EarthObserverCamera? EarthObserver { get; init; }
    public ObserverPlaceMemory Places { get; init; }
    public EarthObserverCamera ObserverCamera => EarthObserver ?? Places.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
    public EarthObserverCamera CameraForBody(ObjectId body) => ObserverCamera.SurfaceBody == body ? ObserverCamera :
        Places.Find(body) ?? (body == SolarSystemSample.EarthId ? EarthObserverCamera.Default :
            new EarthObserverCamera(new(0, 0, 0), 180, 10) { Body = body });
    public ObserverView WithObserverCamera(EarthObserverCamera camera) => this with
        { EarthObserver = camera, Places = Places.Remember(ObserverCamera).Remember(camera) };
    public SkyLayerSettings Sky { get; init; }
    public BodyReferenceMarks ReferenceMarks { get; init; }
    public double? FieldOfViewDegrees { get; init; }
    public CameraAutoOrbit? AutoOrbit { get; init; }
    public double? MouseSensitivity { get; init; }
    public bool CameraInertia { get; init; }
    public static bool ValidMouseSensitivity(double value) => value is >= .25 and <= 4;
    public double? PlaybackRate { get; init; }
    public bool LightTimeDelayed { get; init; }
    /// <summary>Applied clock displacement, retained across splitting, saving and reloading. Null for older scenes.</summary>
    public long? LightTimeOffsetNanoseconds { get; init; }
    public ObserverOptics? Optics { get; init; }
    public bool SupportsOptics => Mode == CameraMode.Surface || Navigation is not null;
    public VisualOptics? ActiveOptics => SupportsOptics && Optics is { Active: true } optical ? optical.Profile : null;
    public static bool ValidPlaybackRate(double rate) => rate is >= -100_000_000 and <= 100_000_000;
    public double EffectiveFieldOfView => FieldOfViewDegrees ?? (Mode == CameraMode.Surface && Navigation is null ? 10 : 49);
    public static bool ValidFieldOfView(double field) => field is >= 2 and <= 120;
}

