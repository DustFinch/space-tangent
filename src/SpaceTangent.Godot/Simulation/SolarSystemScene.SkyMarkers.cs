// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private bool AimAtSkyMarker(string text)
    {
        if (_brightSky is not { } sky || Session is not { } session)
            return false;
        string query = Key(text);
        if (query.Length < 3)
            return false;
        bool Matches(SkyCatalogueMarker marker)
        {
            string name = Key(marker.Name);
            return name == query || name == "PSR" + query || Key(marker.Alias) == query;
        }
        var pulsar = sky.Pulsars.FirstOrDefault(Matches);
        var marker = pulsar ?? sky.Quasars.FirstOrDefault(Matches);
        if (marker is null)
            return false;

        var current = session.CaptureScenario(_active);
        var views = current.Views.ToArray();
        var view = views[_active];
        Vector3d direction = new(marker.Direction.X, marker.Direction.Y, marker.Direction.Z);
        if (view.Mode == CameraMode.Surface && view.Navigation is null &&
            _views[_active].Snapshot?.SolarSystem is { } solar && _surfaceTextures is not null)
        {
            var camera = view.EarthObserver ?? EarthObserverCamera.Moscow;
            var light = EarthSurfaceLighting.Sample(solar, camera.Site, session.DeltaT);
            var place = EarthObserving.HorizontalDirection(light, direction)!.Value;
            view = view with { EarthObserver = camera with
            {
                AzimuthDegrees = place.AzimuthDegrees ?? camera.AzimuthDegrees,
                ElevationDegrees = place.AltitudeDegrees
            } };
        }
        else
        {
            session.OpenSpaceView(_active);
            var pose = session[_active].Navigation!.Value;
            var offset = _views[_active].Snapshot is { } snapshot
                ? pose.ResolvePlacement(snapshot, session.DeltaT).OffsetKilometers : default;
            var north = Math.Abs(direction.Z) < .99 ? new Vector3d(0, 0, 1) : new Vector3d(0, 1, 0);
            var right = Vector3d.Cross(direction, north);
            var up = Vector3d.Cross(right / right.Length, direction);
            view = view with { Mode = CameraMode.Solar, Navigation = pose with
            {
                AnchorKilometers = pose.AnchorKilometers + offset,
                Tracking = false, Target = null, Placement = new(default, direction, up)
            } };
        }
        var flag = pulsar is not null ? SkyMarkings.Pulsars : SkyMarkings.Quasars;
        views[_active] = view with
        {
            Sky = view.Sky with { Markings = view.Sky.Markings | flag },
            FieldOfViewDegrees = Math.Min(5, view.EffectiveFieldOfView)
        };
        ApplyScenario(current with { Views = Array.AsReadOnly(views) });
        _status.Text = pulsar is not null
            ? FormattableString.Invariant($"{marker.Name} · Pulsar · period {marker.Detail:G5} s")
            : FormattableString.Invariant($"{marker.Name} · Quasar · z = {marker.Detail:G5}");
        return true;

        static string Key(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    }
}
