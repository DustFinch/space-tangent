// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredSkyAssets(SolarSystemScene scene, string directory)
    {
        var session = scene.Session!;
        var before = session.CaptureScenario(0);
        string root = Path.Combine(directory, "authored-surveys");
        string surveyRoot = Path.Combine(root, "acceptance");
        string photos = Path.Combine(directory, "authored-photos");
        Directory.CreateDirectory(surveyRoot); Directory.CreateDirectory(photos);
        File.WriteAllText(Path.Combine(surveyRoot, "properties"),
            "obs_title=Authored GPU witness\nhips_frame=equatorial\nhips_order=5\nhips_tile_width=64\nhips_tile_format=png\n");
        var survey = LocalSkySurvey.Read(surveyRoot);
        Save(survey.AllSkyPath, 54, 58, new(0, 0, 1));
        const long cell = 5359;
        Save(survey.TilePath(5, cell), 64, 64, new(1, 0, 0));
        Save(Path.Combine(photos, "field.png"), 32, 32, new(0, 1, 0));
        File.WriteAllText(Path.Combine(photos, "textures.json"), """
            {"imageUrl":"field.png","minResolution":0.1,"maxBrightness":18,"birthJD":2451545,
             "worldCoords":[[[359,-1],[1,-1],[1,1],[359,1]]],
             "textureCoords":[[[0,0],[1,0],[1,1],[0,1]]],
             "imageCredits":{"SHORT":"Authored regression fixture"}}
            """);
        using var tiles = new SkySurveyTiles(root, photos);
        tiles.SetOffline(true);
        var viewport = new SubViewport { Size = new(128, 128), Disable3D = true,
            TransparentBg = false, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        var canvas = new SkySurveyCanvas(tiles, additive: false) { Size = new(128, 128) };
        viewport.AddChild(canvas); AddChild(viewport);
        try
        {
            var center = HealpixSkyGrid.Direction(5, cell, .5, .5);
            var forward = new Vector3((float)center.X, (float)center.Y, (float)center.Z).Normalized();
            var right = forward.Cross(Vector3.Back).Normalized();
            var up = right.Cross(forward).Normalized();
            if (!canvas.ShowFrame("acceptance", forward, right, up, Mathf.Tan(Mathf.DegToRad(1))))
                throw new InvalidOperationException("The authored survey must publish a visible local frame.");
            using var detailed = await Drawn();
            Color red = detailed.GetPixel(64, 64);
            if (canvas.DetailPatches < 1 || canvas.FinestSourceOrder != 5 || red.R < red.B + .3)
                throw new InvalidOperationException($"The known red detail cell must reach the GPU, got {red}.");
            int detailCount = canvas.DetailPatches;
            canvas.ShowFrame("acceptance", forward, right, up, Mathf.Tan(Mathf.DegToRad(37.5f)), brightness: .75f);
            using var overview = await Drawn();
            Color blue = overview.GetPixel(64, 64);
            if (canvas.OverviewPatches < 1 || canvas.DetailPatches != 0 || blue.B < blue.R + .3)
                throw new InvalidOperationException($"The coarse view must fall back to the blue overview, got {blue}.");
            if (ReferenceEquals(tiles.Texture(survey.AllSkyPath, survey, allSky: true), null) ||
                !ReferenceEquals(tiles.Texture(survey.AllSkyPath, survey, allSky: true), tiles.Texture(survey.AllSkyPath, survey, allSky: true)))
                throw new InvalidOperationException("Repeated local texture requests must reuse the published cache entry.");

            const float narrow = .008727f;
            canvas.ShowFrame(null, Vector3.Right, Vector3.Up, Vector3.Back, narrow,
                photographs: true, julianDate: 2451544);
            using var unborn = await Drawn();
            if (canvas.PhotographVisible("field.png")) throw new InvalidOperationException("A photograph cannot precede its birth epoch.");
            canvas.ShowFrame(null, Vector3.Right, Vector3.Up, Vector3.Back, narrow,
                photographs: true, julianDate: 2451545);
            using var photograph = await Drawn();
            Color green = photograph.GetPixel(64, 64);
            if (!canvas.PhotographVisible("field.png") || green.G < Math.Max(green.R, green.B) + .3)
                throw new InvalidOperationException($"The seam-crossing green photograph must render after its birth, got {green}.");
            canvas.ShowFrame("acceptance", forward, right, up, narrow, photographs: true, julianDate: 2451545);
            using var replaced = await Drawn();
            if (canvas.PhotographVisible("field.png") || canvas.SurveyId != "acceptance")
                throw new InvalidOperationException("An enabled survey must replace the photographic layer.");
            if (canvas.ShowFrame(null, forward, right, up, 0, photographs: false) || canvas.Visible || canvas.HasSurvey ||
                canvas.DetailPatches != 0 || canvas.OverviewPatches != 0)
                throw new InvalidOperationException("Clearing the sky must remove all stale patches and visibility.");
            if (!session.CaptureScenario(0).Views.SequenceEqual(before.Views) || session.DeltaT != before.DeltaT)
                throw new InvalidOperationException("Local survey component checks must preserve the live observers and DeltaT.");
            if (detailed.SavePng(Path.Combine(directory, "authored-sky-detail.png")) != Error.Ok ||
                photograph.SavePng(Path.Combine(directory, "authored-sky-photograph.png")) != Error.Ok)
                throw new IOException("Could not retain the authored sky GPU evidence.");
            GD.Print($"Authored sky GPU: {detailCount} detail patches, overview fallback, cached textures and dated seam-crossing photograph; independent live cameras preserved.");
        }
        finally
        {
            RemoveChild(viewport); viewport.Free();
        }

        async Task<Image> Drawn()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return viewport.GetTexture().GetImage();
        }
        static void Save(string path, int width, int height, Color color)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
            image.Fill(color);
            if (image.SavePng(path) != Error.Ok) throw new IOException("Could not write an authored sky image.");
        }
    }
}
