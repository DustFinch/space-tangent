// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application.Tests;
using SpaceTangent.Godot.Simulation;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseArchivedCulling(SolarSystemScene scene, string directory)
    {
        const string name = "Sol/Earth";
        var fixture = new LegacyWireFixture().Names(name, "world/" + name);
        var cases = new[]
        {
            (X:0f, Z:-100f, Radius:10f, Visible:true),
            (X:0f, Z:100f, Radius:10f, Visible:false),
            (X:10000f, Z:-100f, Radius:10f, Visible:false),
            (X:0f, Z:-100f, Radius:1e-12f, Visible:false),
            (X:0f, Z:-.01f, Radius:.001f, Visible:true),
            (X:0f, Z:-1e11f, Radius:1e9f, Visible:true),
            (X:0f, Z:-100f, Radius:10f, Visible:true)
        };
        for (int i = 0; i < cases.Length; i++)
        {
            byte[] body = fixture.Body();
            F(body, 4, cases[i].X); F(body, 12, cases[i].Z); F(body, 16, cases[i].Radius); F(body, 60, 0);
            fixture.Record(3, fixture.Frame(body, sequence: i + 1));
        }
        string recording = Path.Combine(directory, "authored-culling.celframe"); File.WriteAllBytes(recording, fixture.File());
        string restore = Path.Combine(directory, "before-culling.stscene");
        if (!scene.SaveScenario(restore)) throw new IOException("Preserve the observers before archived geometry culling.");
        scene.SetProcess(false);
        try
        {
            if (!scene.StartReplay(recording)) throw new InvalidOperationException("The independently encoded culling sequence must open.");
            var renderer = scene.Renderer(0);
            MeshInstance3D? first = null;
            for (int i = 0; i < cases.Length; i++)
            {
                if (i > 0) scene._Process(1d / 30);
                var node = renderer.ArchivedBody(name);
                bool visible = node is { Visible: true };
                if (visible != cases[i].Visible)
                    throw new InvalidOperationException($"Archived culling case {i}: expected visible={cases[i].Visible}, actual={visible}.");
                if (visible)
                {
                    first ??= node;
                    if (!ReferenceEquals(first, node) || !float.IsFinite(node!.Scale.X) || node.Scale.X <= 0)
                        throw new InvalidOperationException("Near and far archive coordinates must reuse a finite, positive body transform.");
                }
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            }
            scene._Process(1d / 30);
            if (renderer.ArchivedBody(name) is not null || renderer.ArchivedGeometry is not null)
                throw new InvalidOperationException("Completing the culling sequence must clear its body and archive state.");
            GD.Print("Archived culling GPU: front/rear/frustum/subpixel cases, near-plane rescaling, far projection and bounded pool recovery passed.");
        }
        finally
        {
            if (!scene.LoadScenario(restore)) throw new InvalidOperationException("Restore the observers after culling acceptance.");
            scene.SetProcess(true);
        }
    }
}
