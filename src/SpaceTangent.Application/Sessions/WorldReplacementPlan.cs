// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

// Evaluate every observer before the session applies any state or flight changes.
internal sealed record WorldReplacementPlan(ObserverView[] Views, bool[] CancelFlights, IReadOnlyList<int> SurfaceFallbacks)
{
    internal static WorldReplacementPlan? Prepare(SimulationWorld current, SimulationWorld next,
        IReadOnlyList<ObserverView> views, DeltaTModel model)
    {
        if (views.Any(view => !WorldEpochRange.Contains(next.Ephemeris, view.Epoch)))
            return null;

        var fallbacks = new List<int>();
        var updatedViews = new ObserverView[views.Count];
        var cancelFlights = new bool[views.Count];
        for (int index = 0; index < views.Count; index++)
        {
            ObserverView previous = views[index];
            ObjectId? selected = previous.Selection is { } id && next.Catalogue.Find(id) is not null
                ? id
                : null;
            bool lostMode = (previous.Mode == CameraMode.Satellites && next.Satellites.Count == 0) ||
                (previous.Mode == CameraMode.Mission && next.MissionId is null) ||
                (previous.Mode == CameraMode.Timeline && next.TimelineId is null) ||
                (previous.Mode == CameraMode.Probe && next.Probe is null) ||
                (previous.Mode == CameraMode.LunarTerrain && !next.HasLunarTerrain) ||
                (previous.Mode == CameraMode.MarsTerrain && !next.HasMarsTerrain);
            CameraMode mode = lostMode
                ? CameraMode.Solar
                : previous.Mode;
            NavigationCamera? navigation = previous.Navigation;
            if (navigation is { Target: { } target } camera)
            {
                bool lostTarget = next.Catalogue.Find(target) is null;
                bool lostFrame = camera.Placement is { UsesFrame: true } &&
                    !camera.TryFrameBasis(next.Graph.Evaluate(previous.Epoch, model), model, out _);
                if (lostTarget || lostFrame)
                {
                    NavigationPlacement? placement = camera.Placement is { UsesFrame: true }
                        ? camera.ResolvePlacement(current.Graph.Evaluate(previous.Epoch, model), model) : camera.Placement;
                    bool release = lostTarget || camera.Placement?.CatalogueFrame is not null;
                    navigation = camera with { Target = release ? null : target, Tracking = !release && camera.Tracking,
                        Placement = placement };
                    cancelFlights[index] = true;
                }
            }
            ObjectId? aim = previous.ScriptAimTarget is { } tracked && next.Catalogue.Find(tracked) is not null ? tracked : null;
            EarthObserverCamera? site = previous.EarthObserver;
            if (site is { IsEarth: false } surface &&
                !SurfaceObserver.TryFrame(next, surface, next.Graph.Evaluate(previous.Epoch, model), model, out _))
            {
                site = previous.Places.Find(SolarSystemSample.EarthId) ?? EarthObserverCamera.Default;
                if (mode == CameraMode.Surface && navigation is null) mode = CameraMode.Solar;
                fallbacks.Add(index);
            }
            var updated = previous with { Selection = selected, Mode = mode, Navigation = navigation, ScriptAimTarget = aim };
            updatedViews[index] = site is { } location && site != previous.EarthObserver ? updated.WithObserverCamera(location) :
                updated with { EarthObserver = site };
        }
        return new(updatedViews, cancelFlights, fallbacks.AsReadOnly());
    }
}
