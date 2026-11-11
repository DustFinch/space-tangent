// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

/// <summary>Optional text PCK frames and per-system SPK center corrections for the planetary catalogue.</summary>
public sealed class PlanetEphemerides
{
    private readonly Dictionary<int, SpicePolynomialBodyFrame> _frames = new();
    private readonly Dictionary<int, SpkKernel> _centers = new();

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
        if (source?._frames.GetValueOrDefault(planet.NaifId) is { } frame)
            graph.AddOrientation(planet.Id, epoch => frame.Sample(epoch));
    }
}
