// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

public sealed partial class SimulationSession
{
    private sealed record WorldReplacementState(SimulationWorld World, ObserverView[] Views,
        CameraFlight?[] Flights, IReadOnlyList<int> Fallbacks, int PlacesVersion);

    /// <summary>
    /// Applies a compatible world and synchronously persists its source preferences
    /// before the adapter publishes rendering changes. A persistence failure restores
    /// the original world, observer records and flights without replacing their identities.
    /// The persistence callback must not mutate the session or start asynchronous work.
    /// </summary>
    public bool TryReplaceWorld(SimulationWorld next, Action? persistPreferences)
    {
        var plan = WorldReplacementPlan.Prepare(World, next, _views, DeltaT);
        if (plan is null) return false;
        var rollback = persistPreferences is null ? null : new WorldReplacementState(World,
            _views.ToArray(), _flights.ToArray(), SurfaceObserverFallbacks, ObserverPlacesVersion);
        try
        {
            for (int index = 0; index < _views.Count; index++)
            {
                if (plan.CancelFlights[index]) CancelFlight(index);
                _views[index] = plan.Views[index];
            }
            World = next;
            SurfaceObserverFallbacks = plan.SurfaceFallbacks;
            if (plan.SurfaceFallbacks.Count > 0) ObserverPlacesVersion++;
            persistPreferences?.Invoke();
            return true;
        }
        catch
        {
            if (rollback is not null)
            {
                World = rollback.World;
                for (int index = 0; index < _views.Count; index++)
                {
                    _views[index] = rollback.Views[index];
                    _flights[index] = rollback.Flights[index];
                }
                SurfaceObserverFallbacks = rollback.Fallbacks;
                ObserverPlacesVersion = rollback.PlacesVersion;
            }
            throw;
        }
    }
}
