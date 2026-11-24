// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed record HuygensMission(SscHuygensTrack Track, SpkKernel SaturnSystem, SpicePolynomialBodyFrame TitanFrame);

public sealed partial class SimulationWorld
{
    public SimulationWorld(ISolarSystemEphemeris ephemeris, IEnumerable<ScienceSatellite> satellites,
        SpiceSpacecraft? mission = null, SscSampledSpacecraft? timeline = null, HuygensMission? probe = null,
        SpicePolynomialBodyFrame? moonFrame = null, BrightStarCatalogue? brightStars = null, PlanetEphemerides? planets = null, bool hasMarsTerrain = false, StellarCatalogue? stellar = null, IEnumerable<SscBodyDefinition>? bodyCatalog = null,
        IEnumerable<SscSurfaceLocation>? locations = null, DeepSkyIndex? deepSky = null, ExoplanetCatalogue? exoplanets = null, IEnumerable<SampledMission>? sampledMissions = null, IEnumerable<LocalTleSatellite>? localSatellites = null, EarthPlaceCatalogue? earthPlaces = null, DistantStarIndex? distantStars = null)
    {
        var assembled = new SimulationWorldAssembly(new(
            ephemeris, satellites, mission, timeline, probe, moonFrame, brightStars, planets,
            hasMarsTerrain, stellar, bodyCatalog, locations, deepSky, exoplanets, sampledMissions,
            localSatellites, earthPlaces, distantStars), BlackHoles);
        Ephemeris = assembled.Ephemeris;
        Planets = assembled.Planets;
        SourceWarnings = assembled.SourceWarnings;
        Satellites = assembled.Satellites;
        BrightStars = assembled.BrightStars;
        HasMarsTerrain = assembled.HasMarsTerrain;
        Stellar = assembled.Stellar;
        DeepSky = assembled.DeepSky;
        Exoplanets = assembled.Exoplanets;
        StellarOrigin = assembled.StellarOrigin;
        MissionId = assembled.MissionId;
        MissionName = assembled.MissionName;
        TimelineId = assembled.TimelineId;
        TimelineStart = assembled.TimelineStart;
        Probe = assembled.Probe;
        HasLunarTerrain = assembled.HasLunarTerrain;
        SscBodies = assembled.SscBodies;
        EarthPlaces = assembled.EarthPlaces;
        DistantStars = assembled.DistantStars;
        Catalogue = assembled.Catalogue;
        Graph = assembled.Graph;
        AlmanacGraph = assembled.AlmanacGraph;
        _sscById = assembled.SscById;
        _sampledMissions = assembled.SampledMissions;
        _localSatellites = assembled.LocalSatellites;
        _sitesById = assembled.SitesById;
        _sitesByBody = assembled.SitesByBody;
    }

    public ISolarSystemEphemeris Ephemeris { get; }
    public PlanetEphemerides? Planets { get; }
    public WorldGraph AlmanacGraph { get; }
    internal bool HasSurfaceOrientation(ObjectId id) => !_sscById.TryGetValue(id, out var body) ||
        body.Definition.HasExplicitOrientation || body.Definition.BindTimeline is null &&
        (body.Definition.SampleRotation is not null || body.Definition.Orientation is not null);
    public IReadOnlyList<string> SourceWarnings { get; }

    public IReadOnlyList<ScienceSatellite> Satellites { get; }
    private readonly Dictionary<ObjectId, InstalledSscBody> _sscById;
    private readonly Dictionary<ObjectId, SampledMission> _sampledMissions;
    private readonly Dictionary<ObjectId, LocalTleSatellite> _localSatellites;
    public IReadOnlyCollection<LocalTleSatellite> LocalSatellites => _localSatellites.Values;
    public LocalTleSatellite? FindLocalSatellite(ObjectId id) => _localSatellites.GetValueOrDefault(id);
    public SampledMission? FindSampledMission(ObjectId id) => _sampledMissions.GetValueOrDefault(id);
    public IReadOnlyList<InstalledSscBody> SscBodies { get; }
    public ObjectCatalogue Catalogue { get; }
    public BrightStarCatalogue BrightStars { get; }
    public StellarCatalogue Stellar { get; }
    public DistantStarIndex? DistantStars { get; }
    public DeepSkyIndex DeepSky { get; }
    public EarthPlaceCatalogue EarthPlaces { get; }
    public ExoplanetCatalogue Exoplanets { get; }
    public Vector3d StellarOrigin { get; }

    private readonly Dictionary<ObjectId, SurfaceSite> _sitesById;
    private readonly Dictionary<ObjectId, SurfaceSite[]> _sitesByBody;

    public SurfaceSite? FindSurfaceSite(ObjectId id) => _sitesById.GetValueOrDefault(id);
    public IReadOnlyList<SurfaceSite> SurfaceSitesOn(ObjectId id) => _sitesByBody.GetValueOrDefault(id) ?? [];

    public bool TrySurfaceFrame(SurfaceSite site, WorldSnapshot snapshot, DeltaTModel model,
        out CartesianState body, out Basis3d orientation)
    {
        orientation = default;
        return snapshot.TryState(site.Body, out body) && NavigationCamera.TryBodyBasis(snapshot, site.Body, model, out orientation);
    }

    /// <summary>The state of any navigable target. Earth surface sites depend on the observer's ΔT model.</summary>
    public bool TryNavigationState(ObjectId id, WorldSnapshot snapshot, out CartesianState state, DeltaTModel deltaT)
    {
        if (snapshot.TryState(id, out state))
            return true;
        if (FindSurfaceSite(id) is { } site && TrySurfaceFrame(site, snapshot, deltaT, out CartesianState body, out Basis3d frame))
        {
            Vector3d offset = frame.Transform(site.OffsetKilometers);
            Vector3d spin = snapshot.TryOrientation(site.Body, out var rotation)
                ? rotation.AngularVelocityRadiansPerSecond.GetValueOrDefault()
                : site.Body == SolarSystemSample.EarthId
                    ? SpaceTangent.Core.Coordinates.EarthOrientation.TerrestrialToWorld(snapshot.Epoch, deltaT)
                        .AngularVelocityRadiansPerSecond.GetValueOrDefault() : default;
            state = body + new CartesianState(offset, Vector3d.Cross(spin, offset));
            return true;
        }
        if (Exoplanets.Find(id) is { } planet && FindStellarTarget(planet.Host) is { } host)
        {
            CartesianState relative = planet.ParentRelativeState(snapshot.Epoch);
            for (int depth = 0; planet.Parent != planet.Host; depth++)
            {
                if (depth == 4 || Exoplanets.Find(planet.Parent) is not { } parent)
                    return false;
                relative += parent.ParentRelativeState(snapshot.Epoch);
                planet = parent;
            }
            state = PlaceStar(host, snapshot.Epoch) + relative;
            return true;
        }
        if (FindStellarTarget(id) is { } star)
        {
            state = PlaceStar(star, snapshot.Epoch);
            return true;
        }
        if (DeepSky.Find(id) is { } deepSky)
        {
            state = new(deepSky.PositionKilometers + StellarOrigin, default);
            return true;
        }
        return false;
    }

    public StellarEntry? FindStellarTarget(ObjectId id) => Stellar.Find(Exoplanets.CanonicalHost(id)) ?? Exoplanets.FindHost(id) ?? DistantStars?.Find(id);

    private CartesianState PlaceStar(StellarEntry star, JulianInstant epoch)
    {
        CartesianState relative = star.StateAt(epoch);
        return new(relative.PositionKilometers + StellarOrigin, relative.VelocityKilometersPerSecond);
    }

    public double TargetRadiusKilometers(ObjectId id) =>
        FindSurfaceSite(id)?.ScaleKilometers ?? StarRadius(FindStellarTarget(id)) ?? DeepSky.Find(id)?.RadiusKilometers ??
        Exoplanets.Find(id)?.RadiusKilometers ?? DemoWorlds.Find(id)?.RadiusKilometers ??
        _sscById.GetValueOrDefault(id)?.Definition.RadiusKilometers ??
        MissionNavigationRadius(id) ?? BlackHoles.Find(id)?.SchwarzschildRadiusKilometers ??
        (id == CygnusCompanionId ? CygnusCompanionRadiusKilometers : NavigationCamera.BodyRadiusKilometers(id));
    private double? MissionNavigationRadius(ObjectId id) => _sampledMissions.TryGetValue(id, out var mission)
        ? mission.NavigationRadiusKilometers ?? mission.RadiusKilometers : null;

    private static double? StarRadius(StellarEntry? star) => star is null ? null :
        star.IsBarycenter ? Math.Max(1000, star.SystemRadiusKilometers) : Math.Max(.007, star.RadiusKilometers);

    public WorldGraph Graph { get; }
    public ObjectId? MissionId { get; }
    public string? MissionName { get; }
    public ObjectId? TimelineId { get; }
    public JulianInstant? TimelineStart { get; }
    public SscHuygensTrack? Probe { get; }
    public bool HasLunarTerrain { get; }
    public bool HasMarsTerrain { get; }
}
