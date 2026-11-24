// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Addons;

public sealed record DecodedMissionObservation(MissionObservation Style, IReadOnlyList<MissionFootprint> Footprints,
    double RadiusKilometers, bool SphericalDisplay);
