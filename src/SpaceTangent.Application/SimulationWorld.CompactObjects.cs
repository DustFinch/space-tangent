// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class SimulationWorld
{
    public static ObjectId CygnusLensId { get; } = new("black-hole/cyg-x1");
    public static ObjectId CygnusCompanionId { get; } = new("black-hole/hde-226868");
    public const double CygnusCompanionRadiusKilometers = 22.3 * 695_700;

    public BlackHoleCatalogue BlackHoles { get; } = BlackHoleCatalogue.Default;

    private void AddCompactObjects(WorldGraphBuilder graph)
    {
        Vector3d binary = default;
        foreach (BlackHole hole in BlackHoles.Entries)
        {
            Vector3d relative = hole.PositionKilometers;
            if (hole.Host is { } host)
            {
                string designation = host.Replace(" ", "", StringComparison.Ordinal);
                var galaxy = DeepSky.Objects.FirstOrDefault(item =>
                    item.Designation.Replace(" ", "", StringComparison.Ordinal).Equals(designation, StringComparison.OrdinalIgnoreCase));
                if (galaxy is not null)
                    relative = galaxy.PositionKilometers;
            }
            Vector3d position = StellarOrigin + relative;
            graph.AddTrajectory(hole.Id, _ => new CartesianState(position, default));
            if (hole.Id == CygnusLensId && hole.DiskAxis() is { } axis)
            {
                var transverse = Vector3d.Cross(axis, hole.Direction * -1);
                binary = position + transverse * (.2441 * 149_597_870.7 / transverse.Length);
            }
        }
        graph.AddTrajectory(CygnusCompanionId, _ => new CartesianState(binary, default));
    }
}
