// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseBodySurfaceObserver(string directory)
    {
        string restore = Path.Combine(directory, "before-body-surface.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            var initialMode = System.Environment.GetEnvironmentVariable("SPACETANGENT_NO_EARTH_MAPS_SMOKE") == "1" ? CameraMode.Solar : CameraMode.Surface;
            var left = new ObserverView(TimeConversion.FromUtc(new(new(2024, 10, 18)), TimeScale.Tdb), initialMode,
                SolarSystemSample.EarthId, false) { EarthObserver = EarthObserverCamera.Moscow };
            var right = new ObserverView(JulianInstant.FromJulianDate(2455754.35, TimeScale.Tdb), CameraMode.Satellites, Jason, false);
            string path = Path.Combine(directory, "body-surface.stscene");
            ScenarioFile.Save(path, new([left, right], false, 0, 0, DeltaTModel.MorrisonStephenson2004));
            Require(_scene.LoadScenario(path), "The body-observer and Jason-1 scene must open.");
            foreach (string name in new[] { "Moon", "Mars", "Titan" })
            {
                await Click(Find<Button>("LocationTab"));
                var bodies = Find<OptionButton>("ObserverBody");
                int index = Enumerable.Range(0, bodies.ItemCount).Single(i => bodies.GetItemText(i) == name);
                await Click(bodies);
                await ChooseMenuItem(bodies.GetPopup(), index);
                Require(Session[0] is { Mode: CameraMode.Surface, Navigation: null, EarthObserver.IsEarth: false } &&
                    Session.World.Catalogue.Find(Session[0].EarthObserver!.Value.SurfaceBody)?.Name == name,
                    "Choosing a body must retain a structured surface observer, not turn it into an orbit camera.");
                await FillField("ObserverHeight", "0.02");
                await FillField("ObserverLatitude", "45");
                await FillField("ObserverAzimuth", "0");
                await FillField("ObserverElevation", "3");
                await Click(Find<Button>("ApplyObserver"));
                await Click(Find<Button>("SpaceMode"));
                await Click(Find<Button>("ObservatoryMode"));
                if (!Session[0].EarthObserver!.Value.HorizonGrid) await Click(Find<CheckButton>("HorizonGrid"));
                if (!Session[0].EarthObserver!.Value.CardinalPoints) await Click(Find<CheckButton>("CardinalPoints"));
                Require(_scene.Renderer(0).NavigationCameraCurrent && _scene.Renderer(0).BodyHorizonVisible && Session[1] == right,
                    "The body-fixed view and its horizon must render without moving Jason-1.");
                Require(Math.Abs(_scene.Renderer(0).NavigationCameraNode.Fov - Session[0].EffectiveFieldOfView) < .01 &&
                    _scene.Renderer(0).BodyGroundVisible, "The transient render camera must retain the saved surface FOV and analytic ground.");
                var viewportSize = _scene.Renderer(0).GetViewport().GetVisibleRect().Size;
                var pointer = _scene.Renderer(0).PointerDirection(viewportSize / 2)?.Horizon;
                Require(pointer is { } sky && Math.Abs(sky.AltitudeDegrees - 3) < .001 &&
                    sky.AzimuthDegrees is { } az && Math.Min(az, 360 - az) < .001,
                    "The pointer must use the current body's local horizon.");
                Session.SetSkyLayers(0, Session[0].Sky with { Markings = Session[0].Sky.Markings | SkyMarkings.SelectedBodyPath | SkyMarkings.CircumpolarCircles });
                _scene.Seek(0, Session[0].Epoch);
                double day = Math.Floor(Session[0].Epoch.ToJulianDate() - .5) + .5;
                await Until(() => _scene.Renderer(0).SelectedBodyPathDay == day && !_scene.Renderer(0).HasPendingPaths,
                    "Sampling the selected body's path from the current observing body", 20);
                Require(Session[1] == right, "The surface path must not change the other observer.");
                Require(Session.World.Graph.Evaluate(Session[0].Epoch, Session.DeltaT).TryOrientation(
                    Session[0].EarthObserver!.Value.SurfaceBody, out var axes), "The observing body's rotation must be available.");
                var expectedPole = new Vector3((float)axes.ToWorld.Z.X, (float)axes.ToWorld.Z.Y, (float)axes.ToWorld.Z.Z);
                Require(_scene.Renderer(0).SpaceCultureOverlay?.LocalRotationPole is { } pole && pole.DistanceTo(expectedPole) < 1e-5,
                    "Circumpolar circles must use the observing body's spin pole rather than the Earth's pole of date.");
                if (name == "Moon" && DisplayServer.GetName() != "headless")
                {
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var ground = _scene.Renderer(0).GetViewport().GetTexture().GetImage();
                    var skyPixel = ground.GetPixel(ground.GetWidth() / 3, ground.GetHeight() / 5);
                    var groundPixel = ground.GetPixel(ground.GetWidth() / 3, ground.GetHeight() * 95 / 100);
                    Require(groundPixel.R + groundPixel.G + groundPixel.B > skyPixel.R + skyPixel.G + skyPixel.B + .12,
                        "The illuminated lunar ground must cover the downward ray and leave the sky clear.");
                }
                await Click(Find<Button>("EphemeridesTab"));
                Require(Find<Label>("CivilTwilight").Text.Contains("reference ellipsoid", StringComparison.Ordinal),
                    "Non-Earth sites must not report Earth civil twilight or weather.");
                var saved = Session.CaptureScenario(0);
                Require(_scene.SaveScenario(path) && _scene.LoadScenario(path), "The structured body observer must save and reopen.");
                RequireScenario(saved, Session.CaptureScenario(0), "Body observer round-trip");
                Require(await _scene.ReloadAddOns(null), "Reloading the body observer data.");
                RequireScenario(saved, Session.CaptureScenario(0), "Body observer reload");
                if (DisplayServer.GetName() != "headless" &&
                    System.Environment.GetEnvironmentVariable("SPACETANGENT_SURFACE_OBSERVER_OUTPUT") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    using var image = GetViewport().GetTexture().GetImage();
                    Require(image.SavePng(Path.Combine(output, name.ToLowerInvariant() + "-surface.png")) == Error.Ok, "Saving the body-observer frame.");
                    ScenarioFile.Save(Path.Combine(output, name.ToLowerInvariant() + "-surface.stscene"), saved);
                }
                GD.Print($"UI: selected {name} surface, edited the site and direction, rendered its horizon and preserved both observers and ΔT through save/reload.");
            }
        }
        finally { Require(_scene.LoadScenario(restore), "The previous workspace must reopen after body observers."); }
    }
}
