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
                _centers.Add(planet.NaifId / 100, new(kernel));
        }
        string saturn = Path.Combine(root, "moons-6.bsp");
        if (File.Exists(saturn))
            _saturnMoons = new(saturn);
        string pluto = Path.Combine(root, "center-9.bsp");
        if (File.Exists(pluto))
            _centers.Add(9, new(pluto));
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            if (hasFrames && body.Kind != CatalogueKind.Comet)
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
            int system = planet.NaifId / 100;
            SpkKernel? kernel = source?._centers.GetValueOrDefault(system);
            graph.AddTrajectory(planet.Id, epoch =>
                kernel is not null && kernel.TryState(planet.NaifId, system, epoch, out var state)
                    ? state : default(CartesianState), planet.SystemId);
        }
        AddFrame(graph, planet.Id, planet.NaifId, source);
    }

    internal static void AddNaturalBody(WorldGraphBuilder graph, NaturalBodyDefinition body, PlanetEphemerides? source)
    {
        if (body.SampledOrbit is not null)
        {
            if (source?._sampled.GetValueOrDefault(body.Id) is { Start: { } start, End: { } end } trajectory)
                graph.AddTrajectory(body.Id, epoch => epoch.CompareTo(start) >= 0 && epoch.CompareTo(end) <= 0 &&
                    trajectory.TrySample(epoch, out CartesianState state) ? state : null, SolarSystemSample.SunId);
        }
        else
        {
            int system = body.NaifId / 100;
            SpkKernel? kernel = system == 6 ? source?._saturnMoons : source?._centers.GetValueOrDefault(system);
            ObjectId barycenter = system == 9 ? SolarSystemSample.PlutoBarycenterId
                : PlanetCatalogue.Find(body.Parent)!.SystemId;
            graph.AddTrajectory(body.Id, epoch => kernel is not null && kernel.TryState(body.NaifId, system, epoch, out var state)
                ? state : body.Id == NaturalBodyCatalogue.PlutoId ? default(CartesianState) : null, barycenter);
        }
        AddFrame(graph, body.Id, body.NaifId, source);
    }

    private static void AddFrame(WorldGraphBuilder graph, ObjectId id, int naifId, PlanetEphemerides? source)
    {
        if (source?._frames.GetValueOrDefault(naifId) is { } frame)
            graph.AddOrientation(id, epoch => frame.Sample(epoch));
    }
}
