// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

internal readonly record struct CameraModeTransition(ObjectId? Selection, JulianInstant? Epoch);

internal static class CameraModeRules
{
    internal static JulianInstant MissionPreview { get; } = JulianInstant.FromTdbSeconds(141917558.4203589);

    internal static void ValidateRequestedMode(SimulationWorld world, CameraMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == CameraMode.LunarTerrain && !world.HasLunarTerrain)
            throw new InvalidOperationException("The current world has no lunar terrain.");
        if (mode == CameraMode.MarsTerrain && !world.HasMarsTerrain)
            throw new InvalidOperationException("The current world has no Martian terrain.");
    }

    internal static CameraModeTransition Plan(SimulationWorld world, ObserverView previous, CameraMode mode, DeltaTModel model)
    {
        ObjectId? selection = null;
        JulianInstant? epoch = null;
        if (mode == CameraMode.Satellites)
        {
            ScienceSatellite target = world.Satellites.FirstOrDefault(satellite => satellite.Id == previous.Selection)
                ?? world.Satellites.FirstOrDefault()
                ?? throw new InvalidOperationException("The current world has no satellites.");
            selection = target.Id;
        }
        if (mode == CameraMode.Mission)
        {
            ObjectId mission = world.MissionId ?? throw new InvalidOperationException("The current world has no mission.");
            WorldSnapshot snapshot = world.Graph.Evaluate(previous.Epoch, model);
            if (!snapshot.TryState(mission, out _) || !snapshot.TryOrientation(mission, out _))
                epoch = MissionPreview;
        }
        if (mode == CameraMode.Timeline)
        {
            ObjectId id = world.TimelineId ?? throw new InvalidOperationException("The current world has no sampled timeline.");
            if (!world.Graph.Evaluate(previous.Epoch, model).TryState(id, out _))
                epoch = world.TimelineStart!.Value;
        }
        if (mode == CameraMode.Probe)
        {
            var probe = world.Probe ?? throw new InvalidOperationException("The current world has no Huygens track.");
            if (!world.Graph.Evaluate(previous.Epoch, model).TryOrientation(probe.Id, out _))
                epoch = probe.Separation;
        }
        return new(selection, epoch);
    }
}
