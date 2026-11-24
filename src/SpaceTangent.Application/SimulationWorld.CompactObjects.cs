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

}
