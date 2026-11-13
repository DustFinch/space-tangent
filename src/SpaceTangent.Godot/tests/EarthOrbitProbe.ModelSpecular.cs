// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Assets;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseModelSpecular(string directory, string path)
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var source = CmodMeshDecoder.Decode(root, path);
        var mesh = ScienceMeshLoader.Publish(source, path, litBody: true);
        var material = Enumerable.Range(0, mesh.GetSurfaceCount())
            .Select(mesh.SurfaceGetMaterial)
            .OfType<ShaderMaterial>()
            .First(item => item.GetShaderParameter("specular_power").AsSingle() == 128);
        var viewport = new SubViewport
        {
            Size = new(256, 256),
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        AddChild(viewport);
        var camera = new Camera3D { Position = new(0, 0, 4) };
        viewport.AddChild(camera);
        camera.MakeCurrent();
        viewport.AddChild(new DirectionalLight3D());
        var sphere = new MeshInstance3D { Mesh = new SphereMesh(), MaterialOverride = material };
        viewport.AddChild(sphere);
        using var highlight = await Capture();
        material.SetShaderParameter("specular_power", 0f);
        using var diffuse = await Capture();
        Color lit = highlight.GetPixel(128, 128);
        Color unlit = diffuse.GetPixel(128, 128);
        if (lit.Luminance <= unlit.Luminance + .02f)
            throw new InvalidOperationException($"Expected a visible material highlight, got {lit} and {unlit}.");
        if (highlight.SavePng(Path.Combine(directory, "highlight.png")) != Error.Ok ||
            diffuse.SavePng(Path.Combine(directory, "diffuse.png")) != Error.Ok)
            throw new IOException("Could not save both model highlight captures.");
        GD.Print($"Model specular GPU: original material power 128, center {unlit.Luminance:F3} -> {lit.Luminance:F3}.");

        async Task<Image> Capture()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return viewport.GetTexture().GetImage();
        }
    }
}
