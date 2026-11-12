// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Simulation;

internal static partial class AddonLoader
{
    private static IReadOnlyDictionary<ObjectId, PreparedMissionObservation> PrepareObservations(
        SimulationWorld world, IEnumerable<DecodedSampledMission> sources, CancellationToken cancellation)
    {
        var tracks = sources.ToDictionary(source => source.Track.Id, source => source.Track);
        var result = new Dictionary<ObjectId, PreparedMissionObservation>();
        const int sampleLimit = 8192;
        int remaining = sampleLimit;
        foreach (SampledMission track in tracks.Values)
        {
            if (track.Observation is not { } observation)
                continue;
            remaining -= MissionFootprints.SampleCount(observation);
            if (remaining < 0)
                throw new InvalidDataException($"The installed observations require more than {sampleLimit} footprint samples.");
            double equatorial = NavigationCamera.BodyRadiusKilometers(observation.Target);
            double polar = PlanetCatalogue.Planets.FirstOrDefault(planet => planet.Id == observation.Target)?.PolarRadiusKilometers ?? equatorial;
            var axes = new Vector3d(equatorial, equatorial, polar);
            var samples = MissionFootprints.Calculate(observation, tracks[observation.Sensor], axes, world.Graph, cancellation);
            result.Add(track.Id, new(observation, samples, equatorial, observation.Target == SolarSystemSample.EarthId));
        }
        return result;
    }
}
