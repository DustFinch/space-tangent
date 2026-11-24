// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SkyEnvironment = Godot.Environment;

namespace SpaceTangent.Godot.Simulation;

internal readonly record struct SkyPresentationState(Camera3D? Camera, float Tangent, bool Catalogue, bool Orbital, bool Survey);

// Owns canvas/survey presentation. Coordinate and path evaluation stays with the
// caller's captured world frame, between the same rendering stages as before.
internal sealed class SkyPresentation
{
    private readonly Node3D _parent;
    internal BrightSkyOverlay Overlay { get; }
    internal SkySurveyCanvas Survey { get; }
    private readonly WorldEnvironment _environment;
    private readonly SkyEnvironment _canvasBackground = new()
    {
        BackgroundMode = SkyEnvironment.BGMode.Canvas,
        BackgroundCanvasMaxLayer = -1
    };

    internal SkyPresentation(Node3D parent, BrightSkyData data, Action<CanvasLayer> installBackground)
    {
        _parent = parent;
        var layer = new CanvasLayer { Layer = -1 };
        _parent.AddChild(layer);
        installBackground(layer);
        Survey = new(data.Surveys, additive: true);
        layer.AddChild(Survey);
        Survey.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Overlay = new();
        Overlay.Configure(data, fillBackground: true);
        layer.AddChild(Overlay);
        Overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _environment = new();
        _parent.AddChild(_environment);
    }

    internal SkyPresentationState PrepareSurveys(ObserverRenderFrame frame, bool spatialStarsVisible,
        bool compactLensVisible, bool galacticMap)
    {
        var observer = frame.Observer;
        bool localSky = observer.Sky.Stars || observer.Sky.Labels || observer.Sky.EquatorialGrid || observer.Sky.Constellations;
        bool catalogue = localSky && !spatialStarsVisible;
        bool orbital = observer.Mode != CameraMode.Surface || observer.Navigation is not null;
        var camera = _parent.GetViewport().GetCamera3D();
        float tangent = camera is null ? 0 : MathF.Tan(Mathf.DegToRad(camera.Fov / 2));
        if (camera?.KeepAspect == Camera3D.KeepAspectEnum.Width && Overlay.Size.X > 0)
            tangent *= Overlay.Size.Y / Overlay.Size.X;

        Basis orientation = camera?.GlobalBasis ?? Basis.Identity;
        bool survey = Survey!.ShowFrame(orbital && !compactLensVisible ? observer.Sky.SurveyId : null,
            Icrs(-orientation.Z), Icrs(orientation.X), Icrs(orientation.Y), tangent,
            (float)observer.Sky.EffectiveSurveyBrightness, !galacticMap,
            photographs: orbital && !compactLensVisible && observer.Sky.DeepSky && observer.Sky.SurveyId is null,
            julianDate: observer.Epoch.ToJulianDate());
        return new(camera, tangent, catalogue, orbital, survey);
    }

    internal bool UpdateVisibility(ObserverRenderFrame frame, SkyPresentationState state, bool galacticVisible)
    {
        var observer = frame.Observer;
        var camera = state.Camera;
        bool catalogue = state.Catalogue, orbital = state.Orbital, survey = state.Survey;
        bool culture = observer.Sky.ConstellationArt ||
            (observer.Sky.Constellations && observer.Sky.CultureId is not null);
        bool render = (catalogue || culture || observer.Sky.HasAlternateGrids || observer.Sky.ConstellationBoundaries ||
            (observer.Sky.Markings != SkyMarkings.None && camera?.Projection == Camera3D.ProjectionType.Perspective) ||
            (observer.Sky.Stars && Overlay.HasTransientStars)) && orbital;
        Overlay.Visible = render;
        Overlay.SetFillBackground(!galacticVisible && !survey);
        SkyEnvironment? background = render || galacticVisible || survey ? _canvasBackground : null;
        if (_environment!.Environment != background)
            _environment.Environment = background;
        return render;
    }

    internal void Close()
    {
        _environment.Environment = null;
        _canvasBackground.Dispose();
    }

    private static Vector3 Icrs(Vector3 value) => new(value.X, -value.Z, value.Y);
}
