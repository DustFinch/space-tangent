// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Spice;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

internal readonly record struct ValidatedObserverScenario(ObserverView[] Views, ObserverLayout Layout, ObserverView Owner);

internal static class ScenarioRestoration
{
    // Validate every observer before the session changes any live state.
    internal static ValidatedObserverScenario Validate(SimulationWorld world, SimulationScenario scenario)
    {
        ObserverView[] views = scenario.Views.ToArray();
        if (views.Where(view => view.Places.HasIdentity).Select(view => view.Places.ViewKey).Distinct().Count() !=
            views.Count(view => view.Places.HasIdentity))
            throw new InvalidDataException("The scenario has duplicate observing place identities.");
        if (views.Length is < 1 or > ObserverLayout.MaximumViews || scenario.ClockOwner < 0 || scenario.ClockOwner >= views.Length ||
            scenario.ActiveView < 0 || scenario.ActiveView >= views.Length)
            throw new InvalidDataException("The scenario refers to an unavailable view.");
        if (!Enum.IsDefined(scenario.DeltaT))
            throw new InvalidDataException("The scenario has an unknown ΔT model.");
        ObserverLayout layout = ObserverLayout.Restore(scenario.Layout, views.Length, scenario.SplitLayout);
        foreach (ObserverView view in views)
        {
            if (!WorldEpochRange.Contains(world.Ephemeris, view.Epoch))
                throw new InvalidDataException("The scenario epoch is outside the current ephemeris.");
            if (!Enum.IsDefined(view.Mode))
                throw new InvalidDataException("The scenario has an unknown camera mode.");
            if (view.Mode == CameraMode.Mission && world.MissionId is null)
                throw new InvalidDataException("The scenario needs the Cassini mission add-on.");
            if (view.Mode == CameraMode.Timeline && world.TimelineId is null)
                throw new InvalidDataException("The scenario needs the Cassini timeline catalogue.");
            if (view.Mode == CameraMode.Probe && world.Probe is null)
                throw new InvalidDataException("The scenario needs the Huygens timeline and Titan kernels.");
            if (view.Mode == CameraMode.LunarTerrain && !world.HasLunarTerrain)
                throw new InvalidDataException("The scenario needs the lunar elevation map and orientation kernel.");
            if (view.Mode == CameraMode.MarsTerrain && !world.HasMarsTerrain)
                throw new InvalidDataException("The scenario needs the Martian elevation map and orientation kernel.");
            if (view.EarthObserver is { } earth && !earth.IsValid())
                throw new InvalidDataException("The scenario has an invalid Earth observer camera.");
            if (!view.Places.IsValid())
                throw new InvalidDataException("The scenario has an invalid observing place memory.");
            if (view.EarthObserver is { IsEarth: false } surface &&
                !SurfaceObserver.TryFrame(world, surface, world.Graph.Evaluate(view.Epoch, scenario.DeltaT), scenario.DeltaT, out _))
                throw new InvalidDataException("The scenario needs the observing body's position and orientation at its saved epoch.");
            if (view.FieldOfViewDegrees is { } field && !ObserverView.ValidFieldOfView(field))
                throw new InvalidDataException("The scenario has an invalid field of view.");
            if (view.Optics is { } optical && !optical.IsValid())
                throw new InvalidDataException("The scenario has an invalid optics profile.");
            if (view.PlaybackRate is { } rate && !ObserverView.ValidPlaybackRate(rate))
                throw new InvalidDataException("The scenario has an invalid playback rate.");
            if (!view.Sky.IsValid() || view.AutoOrbit is { } autoOrbit && !autoOrbit.IsValid() ||
                view.MouseSensitivity is { } sensitivity && !ObserverView.ValidMouseSensitivity(sensitivity))
                throw new InvalidDataException("The scenario has invalid sky or camera settings.");
            if (view.LightTimeOffsetNanoseconds is { } offset && (offset < 0 || !view.LightTimeDelayed))
                throw new InvalidDataException("The scenario has an invalid light-time offset.");
            if ((view.ReferenceMarks & ~BodyReferenceMarks.All) != 0)
                throw new InvalidDataException("The scenario has unsupported reference marks.");
            if (view.Terrain is { } terrain && !terrain.IsValid())
                throw new InvalidDataException("The scenario has invalid lunar camera coordinates or field width.");
            if (view.Mode == CameraMode.Satellites && world.Satellites.Count == 0)
                throw new InvalidDataException("The scenario needs the Earth satellite add-ons.");
            if (view.Selection is { } selection && world.Catalogue.Find(selection) is null)
                throw new InvalidDataException($"The selected object {selection.Value} is missing from the current world.");
            if (view.ScriptAimTarget is { } aim && world.Catalogue.Find(aim) is null)
                throw new InvalidDataException("The scenario has an unavailable script aim target.");
            if (view.Navigation is { } camera &&
                (!camera.IsValid() || (camera.Target is { } target && world.Catalogue.Find(target) is null)))
                throw new InvalidDataException("The scenario has an invalid camera pose or unavailable camera target.");
            if (view.Navigation is { Placement.UsesFrame: true } framed &&
                !framed.TryFrameBasis(world.Graph.Evaluate(view.Epoch, scenario.DeltaT), scenario.DeltaT, out _))
                throw new InvalidDataException("The scene needs an unavailable observer frame.");
        }
        ObserverView owner = views[scenario.ClockOwner];
        if (scenario.Synchronized && views.Any(view => view.Epoch != owner.Epoch || view.Playing != owner.Playing))
            throw new InvalidDataException("Synchronized scenario clocks must agree.");

        return new(views, layout, owner);
    }
}
