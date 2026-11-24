// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private SurfaceHorizonOverlay? _bodyHorizon;
    private ColorRect? _bodyGround;
    private ShaderMaterial? _bodyGroundMaterial;
    private NavigationCamera _bodyPose;
    private readonly ObserverBodyPath _bodySkyPath = new();
    internal NavigationCamera? BodySurfacePose => _bodyObserver is null ? null : _bodyPose;
    private (EarthObserverCamera Camera, BodySurfaceFrame Frame)? _bodyObserver;
    internal bool BodyHorizonVisible => _renderResourcesClosed ? false : (_bodyHorizon?.Visible == true);
    internal bool BodyGroundVisible => _renderResourcesClosed ? false : (_bodyGround?.Visible == true);

    private bool PrepareBodyObserver(ref ObserverView observer, WorldSnapshot snapshot, DeltaTModel model)
    {
        _bodyObserver = null;
        _bodyHorizon?.Hide();
        _bodyGround?.Hide();
        if (observer is not { Mode: CameraMode.Surface, Navigation: null, EarthObserver: { IsEarth: false } site })
        { _bodySkyPath.Reset(); return true; }
        if (CatalogueWorld is not { } world || !SurfaceObserver.TryNavigation(world, site, snapshot, model, out var pose) ||
            !SurfaceObserver.TryFrame(world, site, snapshot, model, out var frame)) return false;
        _bodyObserver = (site, frame);
        _bodyPose = pose;
        if (!observer.Sky.Markings.HasFlag(SkyMarkings.SelectedBodyPath) || observer.Selection is null) _bodySkyPath.Reset();
        observer = observer with { Navigation = pose, FieldOfViewDegrees = observer.EffectiveFieldOfView };
        return true;
    }

    private void DrawBodyHorizon(WorldSnapshot snapshot, bool showTerrain)
    {
        if (_bodyObserver is not { } site) return;
        if (_bodyHorizon is null)
        {
            var layer = new CanvasLayer { Layer = 5 };
            AddChild(layer);
            _bodyHorizon = new();
            layer.AddChild(_bodyHorizon);
            _bodyHorizon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        var basis = _navigationCamera.GlobalBasis;
        DrawBodyGround(site.Camera, snapshot, showTerrain);
        _bodyHorizon.ShowFrame(site.Frame, Icrs(-basis.Z), Icrs(basis.X), Icrs(basis.Y),
            _navigationCamera.Fov, site.Camera.HorizonGrid, site.Camera.CardinalPoints);
        static Vector3d Icrs(Vector3 v) => new(v.X, -v.Z, v.Y);
    }

    private void DrawBodyGround(EarthObserverCamera camera, WorldSnapshot snapshot, bool showTerrain)
    {
        // Procedural worlds retain their terrain meshes; this pass is the reference ellipsoid
        // for mapped natural bodies, avoiding kilometre-scale orbit-camera clipping and faceting.
        if (DemoWorlds.Find(camera.SurfaceBody) is not null || !snapshot.TryOrientation(camera.SurfaceBody, out var rotation)) return;
        if (camera.SurfaceBody == SpaceTangent.Core.Ephemerides.SolarSystemSample.MoonId) _navigationMoon.Hide();
        _planetGlobes.GetValueOrDefault(camera.SurfaceBody)?.Hide();
        if (camera is { IsEarth: true } || CatalogueWorld is not { } world) return;
        if (_bodyGround is null)
        {
            _bodyGroundMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/body_observer_ground.gdshader") };
            var layer = new CanvasLayer { Layer = 4 };
            AddChild(layer);
            _bodyGround = new() { Material = _bodyGroundMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(_bodyGround);
            _bodyGround.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        if (!showTerrain) return;
        double radius = world.TargetRadiusKilometers(camera.SurfaceBody);
        double polar = PlanetCatalogue.Find(camera.SurfaceBody)?.PolarRadiusKilometers ?? radius;
        double ratio = radius / polar;
        var fixedAxes = rotation.ToWorld.Transpose();
        Vector3d eye = fixedAxes.Transform(_bodyPose.Placement!.Value.OffsetKilometers) / radius;
        double metric = eye.X * eye.X + eye.Y * eye.Y + eye.Z * eye.Z * ratio * ratio - 1;
        Basis view = _navigationCamera.GlobalBasis;
        Vector3 Convert(Vector3 value) => V(fixedAxes.Transform(new(value.X, -value.Z, value.Y)));
        var material = _bodyGroundMaterial!;
        Texture2D? map = camera.SurfaceBody == SpaceTangent.Core.Ephemerides.SolarSystemSample.MoonId ? _archivedMoonMap :
            _archivedPlanetTextures?.Surfaces.GetValueOrDefault(camera.SurfaceBody);
        material.SetShaderParameter("has_map", map is not null);
        if (map is not null) material.SetShaderParameter("surface_map", map);
        material.SetShaderParameter("surface_color", new Color(PlanetCatalogue.Find(camera.SurfaceBody)?.Color ?? NaturalBodyCatalogue.Find(camera.SurfaceBody)?.Color ?? "777777"));
        material.SetShaderParameter("eye", V(eye));
        material.SetShaderParameter("eye_metric", (float)Math.Max(metric, 1e-12));
        material.SetShaderParameter("polar_ratio", (float)ratio);
        material.SetShaderParameter("camera_forward", Convert(-view.Z));
        material.SetShaderParameter("camera_right", Convert(view.X));
        material.SetShaderParameter("camera_up", Convert(view.Y));
        var sun = snapshot.SolarSystem!.Value.Sun.PositionKilometers - _bodyPose.AnchorKilometers;
        material.SetShaderParameter("sun_direction", V(fixedAxes.Transform(sun / sun.Length)));
        material.SetShaderParameter("tangent_half_field", (float)Math.Tan(_navigationCamera.Fov * Math.PI / 360));
        var size = GetViewport().GetVisibleRect().Size;
        material.SetShaderParameter("aspect_ratio", size.Y > 0 ? size.X / size.Y : 1);
        _bodyGround.Show();
        static Vector3 V(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
    }
}
