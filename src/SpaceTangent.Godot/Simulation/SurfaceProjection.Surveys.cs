// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SurfaceProjection
{
    private SkySurveyTiles? _surveySources;
    private SubViewport? _surveyViewport;
    internal SkySurveyCanvas? SurveyCanvas { get; private set; }

    private void DrawSurvey(SkyLayerSettings sky, Vector3 forward, Vector3 right, Vector3 up,
        double julianDate, double limitingMagnitude)
    {
        bool survey = _surveySources?.Find(sky.SurveyId) is not null && sky.EffectiveSurveyBrightness > 0;
        bool photographs = sky.DeepSky && sky.SurveyId is null && _surveySources?.Photographs.Count > 0;
        if (!survey && !photographs)
        {
            HideSurvey();
            return;
        }
        if (_surveyViewport is null)
        {
            _surveyViewport = new SubViewport
            {
                TransparentBg = true, Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
            };
            _surface.AddChild(_surveyViewport);
            SurveyCanvas = new(_surveySources!, additive: false);
            _surveyViewport.AddChild(SurveyCanvas);
            SurveyCanvas.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _surface.Resized += ResizeSurvey;
            ResizeSurvey();
            _material.SetShaderParameter("survey_view", _surveyViewport.GetTexture());
        }
        bool visible = SurveyCanvas!.ShowFrame(survey ? sky.SurveyId : null, forward, right, up, _tanHalfField,
            photographs: photographs, limitingMagnitude: limitingMagnitude, julianDate: julianDate);
        _surveyViewport.RenderTargetUpdateMode = visible ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        _material.SetShaderParameter("show_survey", visible);
        _material.SetShaderParameter("survey_brightness", survey ? (float)sky.EffectiveSurveyBrightness : 1f);
    }

    private void ResizeSurvey()
    {
        Vector2 size = _surface.Size;
        _surveyViewport!.Size = new(Math.Max(2, (int)size.X), Math.Max(2, (int)size.Y));
    }

    private void HideSurvey()
    {
        if (_surveyViewport is null)
            return;
        SurveyCanvas!.ShowFrame(null, default, default, default, 0);
        _surveyViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _material.SetShaderParameter("show_survey", false);
    }
}
