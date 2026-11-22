// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Tests;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredArchiveLens(SolarSystemScene scene, string directory)
    {
        string restore = Path.Combine(directory, "before-lens.stscene");
        if (!scene.SaveScenario(restore)) throw new IOException("Preserve the observers before archived lens acceptance.");
        var fixture = new LegacyWireFixture().Names("Sol", "surface", "bh/example");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1); writer.Write(2u);
            Vector(0, 0, -20); writer.Write(1f);
            Vector(0, 0, 1); writer.Write(8f); writer.Write(3000f);
            Vector(1, 0, 0); writer.Write((byte)1); writer.Write(.5f);
            writer.Write((byte)0);
            void Vector(float x, float y, float z) { writer.Write(x); writer.Write(y); writer.Write(z); }
        }
        fixture.Record(3, fixture.Frame(overlays: bytes.ToArray(), sequence: 1));
        fixture.Record(3, fixture.Frame(sequence: 2));
        string path = Path.Combine(directory, "authored-lens.celframe"); File.WriteAllBytes(path, fixture.File());
        scene.SetProcess(false);
        try
        {
            if (!scene.StartReplay(path)) throw new InvalidOperationException("The independently encoded lens recording must open.");
            var renderer = scene.Renderer(0);
            if (!renderer.CompactLensVisible) throw new InvalidOperationException("The archived lens must render without a live compact-object target.");
            var material = renderer.GetChildren().OfType<MeshInstance3D>()
                .Select(node => node.MaterialOverride).OfType<ShaderMaterial>()
                .Single(candidate => candidate.Shader.ResourcePath.EndsWith("/compact_object_lens.gdshader", StringComparison.Ordinal));
            if (material.GetShaderParameter("disk_normal").AsVector3() != Vector3.Back ||
                material.GetShaderParameter("disk_reference").AsVector3() != Vector3.Right ||
                Math.Abs(material.GetShaderParameter("hole_spin").AsSingle() - .5f) > 1e-6f ||
                material.GetShaderParameter("disk_outer").AsSingle() != 8 ||
                material.GetShaderParameter("disk_temperature").AsSingle() != 3000)
                throw new InvalidOperationException("The GPU material must retain the saved disk basis, spin, extent and temperature.");
            await Drawn();
            using var visible = renderer.GetViewport().GetTexture().GetImage();
            renderer.CompactDiskEmission = false; scene.Seek(0, scene.Session![0].Epoch);
            if (material.GetShaderParameter("disk_exposure").AsSingle() != 0)
                throw new InvalidOperationException("Disabling archived disk emission must reach the lens shader.");
            renderer.CompactLensAllowed = false; scene.Seek(0, scene.Session[0].Epoch);
            await Drawn();
            using var hidden = renderer.GetViewport().GetTexture().GetImage();
            int changed = 0;
            for (int y = 0; y < visible.GetHeight(); y += 2)
            for (int x = 0; x < visible.GetWidth(); x += 2)
                if (Math.Abs(visible.GetPixel(x, y).Luminance - hidden.GetPixel(x, y).Luminance) > .005f) changed++;
            if (changed < 20) throw new InvalidOperationException($"The archived lens changed only {changed} GPU samples.");
            renderer.CompactLensAllowed = renderer.CompactDiskEmission = true;
            scene._Process(1d / 30);
            if (renderer.CompactLensVisible || renderer.ArchivedGeometry?.Lens is not null)
                throw new InvalidOperationException("The next frame must clear the archived lens and its material visibility.");
            global::Godot.GD.Print($"Authored archived lens GPU: saved basis/spin/disk parameters, emission toggle and frame cleanup; {changed} changed samples.");
        }
        finally
        {
            if (!scene.LoadScenario(restore)) throw new InvalidOperationException("The previous observers must reopen after archived lens acceptance.");
            scene.Renderer(0).CompactLensAllowed = scene.Renderer(0).CompactDiskEmission = true;
            scene.SetProcess(true);
        }
        async Task Drawn()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }
}
