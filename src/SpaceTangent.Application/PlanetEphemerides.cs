// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Orbits;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>Optional text PCK frames and per-system SPK center corrections for the planetary catalogue.</summary>
public sealed class PlanetEphemerides
{
    private readonly Dictionary<int, SpicePolynomialBodyFrame> _frames = new();
    private readonly Dictionary<int, SpkKernel> _centers = new();
    private readonly Dictionary<int, KernelBackedTrajectory> _planetCenters = new();
    private readonly Dictionary<ObjectId, SampledStateTrajectory> _sampled = new();
    private readonly SpkKernel? _saturnMoons;

    public PlanetEphemerides(string root, string? fallbackPck = null)
    {
        string pck = Path.Combine(root, "pck00011.tpc");
        if (!File.Exists(pck) && fallbackPck is not null)
            pck = fallbackPck;
        bool hasFrames = File.Exists(pck);
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            if (hasFrames && planet.NaifId != 399)
                _frames.Add(planet.NaifId, new(pck, planet.NaifId));
            string kernel = Path.Combine(root, $"center-{planet.NaifId / 100}.bsp");
            if (planet.Id != planet.SystemId && File.Exists(kernel))
            {
                int system = planet.NaifId / 100;
                SpkKernel center = new(kernel);
                _centers.Add(system, center);
                _planetCenters.Add(planet.NaifId, new(center, planet.NaifId, system, _ => default, 86400));
            }
        }
        string saturn = Path.Combine(root, "moons-6.bsp");
        if (hasFrames)
            _frames.Add(301, new(pck, 301));
        if (File.Exists(saturn))
            _saturnMoons = new(saturn);
        string pluto = Path.Combine(root, "center-9.bsp");
        if (File.Exists(pluto))
            _centers.Add(9, new(pluto));
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            if (hasFrames && body.Kind != CatalogueKind.Comet &&
                body.NaifId is not (NaturalBodyCatalogue.HyperionNaifId or NaturalBodyCatalogue.PhoebeNaifId or
                    NaturalBodyCatalogue.ProteusNaifId or NaturalBodyCatalogue.NereidNaifId))
                _frames.Add(body.NaifId, new(pck, body.NaifId));
            if (body.SampledOrbit is not { } file)
                continue;
            string path = Path.Combine(root, file);
            if (File.Exists(path))
                _sampled.Add(body.Id, SampledStateTrajectory.ReadXyzv(path, new("icrf3")));
        }
    }

    public bool HasOrientation(int naifId) => _frames.ContainsKey(naifId);

    internal static void AddPlanet(WorldGraphBuilder graph, PlanetDefinition planet, PlanetEphemerides? source)
    {
        if (planet.Id != planet.SystemId)
        {
            KernelBackedTrajectory? center = source?._planetCenters.GetValueOrDefault(planet.NaifId);
            graph.AddTrajectory(planet.Id, epoch => center?.Sample(epoch) ?? default(CartesianState), planet.SystemId);
        }
        if (planet.Id == SolarSystemSample.EarthId)
            graph.AddMeanEarthRotation();
        else
            AddFrame(graph, planet.Id, planet.NaifId, source);
    }

    internal static void AddNaturalBody(WorldGraphBuilder graph, NaturalBodyDefinition body, PlanetEphemerides? source)
    {
        if (body.SampledOrbit is not null)
        {
            if (source?._sampled.GetValueOrDefault(body.Id) is { Start: { } start, End: { } end } trajectory)
                graph.AddTrajectory(body.Id, epoch => epoch.CompareTo(start) >= 0 && epoch.CompareTo(end) <= 0 &&
                    trajectory.TrySample(epoch, out CartesianState state) ? state : null, SolarSystemSample.SunId);
            else
                graph.AddTrajectory(body.Id, _ => null, SolarSystemSample.SunId);
        }
        else
        {
            int system = body.NaifId / 100;
            SpkKernel? kernel = system == 6 ? source?._saturnMoons : source?._centers.GetValueOrDefault(system);
            if (body.Theory is { } theory)
            {
                PlanetDefinition planet = PlanetCatalogue.Find(body.Parent)!;
                KernelBackedTrajectory? center = source?._planetCenters.GetValueOrDefault(planet.NaifId);
                CartesianState Fallback(JulianInstant epoch) => AnalyticSatelliteOrbit.Sample(theory, epoch) +
                    (center?.Sample(epoch) ?? default(CartesianState));
                Func<JulianInstant, CartesianState> trajectory = kernel is null ? Fallback :
                    new KernelBackedTrajectory(kernel, body.NaifId, system, Fallback, 86400).Sample;
                graph.AddTrajectory(body.Id, epoch => trajectory(epoch), planet.SystemId);
            }
            else if (CatalogueMoonOrbit.Find(body.NaifId) is { } orbit)
            {
                var parent = PlanetCatalogue.Find(body.Parent)!;
                KernelBackedTrajectory? center = source?._planetCenters.GetValueOrDefault(parent.NaifId);
                SpicePolynomialBodyFrame? mars = source?._frames.GetValueOrDefault(parent.NaifId);
                CartesianState Fallback(JulianInstant epoch)
                {
                    CartesianState local = orbit.SampleLocal(epoch);
                    FrameKinematics axes = orbit.UsesMarsMeanEquator
                        ? mars?.SampleMeanEquator(epoch) ?? new(SpinAxes(37.1135, 407.68143, 0), default(Vector3d))
                        : new(EclipticToEquatorial, default(Vector3d));
                    Vector3d position = axes.ToWorld.Transform(local.PositionKilometers);
                    Vector3d velocity = axes.ToWorld.Transform(local.VelocityKilometersPerSecond) +
                        Vector3d.Cross(axes.AngularVelocityRadiansPerSecond ?? default, position);
                    return new CartesianState(position, velocity) + (center?.Sample(epoch) ?? default);
                }

                Func<JulianInstant, CartesianState> trajectory = kernel is null ? Fallback :
                    new KernelBackedTrajectory(kernel, body.NaifId, system, Fallback,
                        body.NaifId is 401 or 402 ? 3600 : 86400).Sample;
                graph.AddTrajectory(body.Id, epoch => trajectory(epoch), parent.SystemId);
            }
            else
            {
                ObjectId barycenter = system == 9 ? SolarSystemSample.PlutoBarycenterId
                    : PlanetCatalogue.Find(body.Parent)!.SystemId;
                graph.AddTrajectory(body.Id, epoch => kernel is not null && kernel.TryState(body.NaifId, system, epoch, out var state)
                    ? state : body.Id == NaturalBodyCatalogue.PlutoId ? default(CartesianState) : null, barycenter);
            }
        }
        if (body.NaifId == NaturalBodyCatalogue.PhoebeNaifId)
            graph.AddOrientation(body.Id, epoch => CatalogueSpin(epoch, .3864022083333333, 12.2, 86.9, 125.56));
        else if (body.NaifId == NaturalBodyCatalogue.ProteusNaifId)
            graph.AddOrientation(body.Id, epoch => CatalogueSpin(epoch, 1.12231477, 47.57, 29.21, 93.42));
        else if (body.NaifId == NaturalBodyCatalogue.NereidNaifId)
            graph.AddOrientation(body.Id, epoch => CatalogueSpin(epoch, .4830833333333333, 58, 50, 0, ecliptic: true));
        else
            AddFrame(graph, body.Id, body.NaifId, source);
    }

    private const double Degrees = Math.PI / 180;
    private static readonly Basis3d EclipticToEquatorial = SpinAxes(23.4392911, 0, 0);

    private static FrameKinematics CatalogueSpin(JulianInstant epoch, double periodDays,
        double tilt, double node, double longitude, bool ecliptic = false)
    {
        double period = periodDays * JulianInstant.SecondsPerDay;
        double days = (epoch.Day - JulianInstant.J2000Day) + epoch.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        double turns = Math.IEEERemainder(days, periodDays) / periodDays;
        Basis3d frame = SpinAxes(tilt, node, longitude + 360 * turns);
        if (ecliptic)
            frame = new(EclipticToEquatorial.Transform(frame.X), EclipticToEquatorial.Transform(frame.Y),
                EclipticToEquatorial.Transform(frame.Z));
        return new(frame, frame.Z * (Math.Tau / period));
    }

    private static Basis3d SpinAxes(double tilt, double node, double longitude)
    {
        var (st, ct) = Math.SinCos(tilt * Degrees);
        var (sn, cn) = Math.SinCos(node * Degrees);
        var (sl, cl) = Math.SinCos(longitude * Degrees);
        return new(new(cn * cl - sn * ct * sl, sn * cl + cn * ct * sl, st * sl),
            new(-cn * sl - sn * ct * cl, -sn * sl + cn * ct * cl, st * cl),
            new(sn * st, -cn * st, ct));
    }

    internal static void AddMoonFrame(WorldGraphBuilder graph, PlanetEphemerides? source) =>
        AddFrame(graph, SolarSystemSample.MoonId, 301, source);

    private static void AddFrame(WorldGraphBuilder graph, ObjectId id, int naifId, PlanetEphemerides? source)
    {
        if (source?._frames.GetValueOrDefault(naifId) is { } frame)
            graph.AddOrientation(id, epoch => frame.Sample(epoch));
        else
            graph.AddOrientation(id, _ => null);
    }
}
