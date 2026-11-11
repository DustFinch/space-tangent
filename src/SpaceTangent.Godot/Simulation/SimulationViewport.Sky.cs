// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SkyEnvironment = Godot.Environment;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private BrightSkyOverlay? _spaceSky;
    private WorldEnvironment? _skyEnvironment;
    private readonly SkyEnvironment _canvasBackground = new()
    {
        BackgroundMode = SkyEnvironment.BGMode.Canvas,
        BackgroundCanvasMaxLayer = -1
    };

    internal void SetSkyData(BrightSkyData data)
    {
        var layer = new CanvasLayer { Layer = -1 };
        AddChild(layer);
        _spaceSky = new();
        _spaceSky.Configure(data, fillBackground: true);
        layer.AddChild(_spaceSky);
        _spaceSky.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _skyEnvironment = new();
        AddChild(_skyEnvironment);
        _surface?.SetSkyData(data);
    }

    internal bool Draw(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        bool available = DrawScene(observer, snapshot, deltaT);
        if (_spaceSky is not null)
        {
            bool localSky = observer.Sky.Stars || observer.Sky.Labels || observer.Sky.EquatorialGrid || observer.Sky.Constellations;
            bool render = localSky && !SpatialStarFieldVisible &&
                (observer.Mode != CameraMode.Surface || observer.Navigation is not null);
            _spaceSky.Visible = render;
            SkyEnvironment? background = render ? _canvasBackground : null;
            if (_skyEnvironment!.Environment != background)
                _skyEnvironment.Environment = background;
            if (render && GetViewport().GetCamera3D() is { } camera)
            {
                Basis basis = camera.GlobalBasis;
                float tangent = MathF.Tan(Mathf.DegToRad(camera.Fov / 2));
                if (camera.KeepAspect == Camera3D.KeepAspectEnum.Width && _spaceSky.Size.X > 0)
                    tangent *= _spaceSky.Size.Y / _spaceSky.Size.X;
                _spaceSky.ShowFrame(new(observer.Epoch, observer.Sky, observer.Selection,
                    Icrs(-basis.Z), Icrs(basis.X), Icrs(basis.Y), tangent));
            }
        }
        return available;

        static Vector3 Icrs(Vector3 value) => new(value.X, -value.Z, value.Y);
    }

    internal ObjectId? PickStar(Vector2 position) => _surface?.PickStar(position) ?? _spaceSky?.Pick(position);
}
