// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Application.Tests;
using SpaceTangent.Contracts;
using SpaceTangent.Godot.Simulation;
using static SpaceTangent.Application.Tests.LegacyWireFixture;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseAuthoredArchivedModels(SolarSystemScene scene, string directory)
    {
        const string variable = "SPACETANGENT_SSC_ADDON_ROOT", name = "Sol/Earth/ModelWitness";
        string? previousRoot = System.Environment.GetEnvironmentVariable(variable);
        string saved = Path.Combine(directory, "before-archive-models.stscene");
        if (!scene.SaveScenario(saved)) throw new IOException("Could not preserve observers before model acceptance.");
        string root = Path.Combine(directory, "authored-model-source");
        string models = Path.Combine(root, "models"); Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(root, "witness.ssc"), """
            "ModelWitness" "Sol/Earth" { Class "minormoon" Radius 10 Mesh "witness.cmod" FixedPosition [10000 0 0] }
            "OpaqueWitness" "Sol/Earth" { Radius 10 Mesh "opaque.cmod" FixedPosition [10000 0 0] }
            "BlendWitness" "Sol/Earth" { Radius 10 Mesh "blend.cmod" FixedPosition [10000 0 0] }
            "StandardWitness" "Sol/Earth" { Radius 10 Mesh "standard.cmod" FixedPosition [10000 0 0] }
            """);
        string modelSource = """
            #celmodel__ascii
            material
            diffuse 0.4 0.5 0.6
            emissive 0.1 0.2 0.3
            specular 0.2 0.3 0.4
            specpower 16
            texture0 "color.png"
            normalmap "normal.png"
            end_material
            mesh
            vertexdesc position f3 normal f3 texcoord0 f2 end_vertexdesc
            vertices 4
            -1 -1 0 0 0 1 0 0
            1 -1 0 0 0 1 1 0
            1 1 0 0 0 1 1 1
            -1 1 0 0 0 1 0 1
            trilist 0 6 0 1 2 0 2 3
            end_mesh
            """;
        File.WriteAllText(Path.Combine(models, "witness.cmod"), modelSource);
        File.WriteAllText(Path.Combine(models, "opaque.cmod"), modelSource);
        File.WriteAllText(Path.Combine(models, "blend.cmod"), modelSource.Replace("end_material", "opacity 0.5\nend_material"));
        File.WriteAllText(Path.Combine(models, "standard.cmod"), modelSource.Replace("emissive 0.1 0.2 0.3", "emissive 0 0 0"));
        using (var color = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        using (var normal = Image.CreateEmpty(8, 8, false, Image.Format.Rgba8))
        {
            color.Fill(new(.8f, .6f, .3f)); normal.Fill(new(.5f, .5f, 1));
            if (color.SavePng(Path.Combine(models, "color.png")) != Error.Ok ||
                normal.SavePng(Path.Combine(models, "normal.png")) != Error.Ok)
                throw new IOException("Could not create authored material textures.");
        }
        scene.SetProcess(false);
        try
        {
            System.Environment.SetEnvironmentVariable(variable, root);
            if (!await scene.ReloadAddOns()) throw new InvalidOperationException("The authored SSC model did not load.");
            ObjectId modelId = scene.Session!.World.SscBodies.Single(body => body.Definition.Name == "ModelWitness").Id;
            if (scene.Renderer(0).ImportedBodyNode(modelId)?.Mesh is not ArrayMesh)
                throw new InvalidOperationException("The authored source must publish a decoded model before replay starts.");
            var fixture = new LegacyWireFixture().Names(name, "world/" + name, "Sol/Earth", "world/Sol/Earth",
                "Sol/Earth/OpaqueWitness", "opaque material", "Sol/Earth/BlendWitness", "blend material",
                "Sol/Earth/StandardWitness", "standard material");
            byte[] model = fixture.Body(); U(model, 0, 0); U(model, 48, 1); F(model, 16, 15); F(model, 60, 0);
            F(model, 40, 0); F(model, 44, 1);
            byte[] earth = fixture.Body(); U(earth, 0, 2); U(earth, 48, 3); F(earth, 16, 15); F(earth, 60, 0);
            fixture.Record(3, fixture.Frame(model, sequence: 1));
            fixture.Record(3, fixture.Frame(earth, sequence: 2));
            fixture.Record(3, fixture.Frame(model, sequence: 3));
            byte[] opaque = (byte[])model.Clone(); U(opaque, 0, 4); U(opaque, 48, 5);
            byte[] blend = (byte[])model.Clone(); U(blend, 0, 6); U(blend, 48, 7);
            fixture.Record(3, fixture.Frame(opaque, sequence: 4));
            fixture.Record(3, fixture.Frame(blend, sequence: 5));
            byte[] standard = (byte[])model.Clone(); U(standard, 0, 8); U(standard, 48, 9);
            fixture.Record(3, fixture.Frame(standard, sequence: 6));
            string path = Path.Combine(directory, "authored-models.celframe"); File.WriteAllBytes(path, fixture.File());
            if (!scene.StartReplay(path)) throw new InvalidOperationException("The authored model recording did not open.");
            var renderer = scene.Renderer(0);
            var first = CheckModel();
            await Drawn();
            using var visible = renderer.GetViewport().GetTexture().GetImage();
            first.Hide(); await Drawn();
            using var hidden = renderer.GetViewport().GetTexture().GetImage();
            int affected = 0;
            for (int y = 0; y < visible.GetHeight(); y += 2)
            for (int x = 0; x < visible.GetWidth(); x += 2)
                if (Math.Abs(visible.GetPixel(x, y).Luminance - hidden.GetPixel(x, y).Luminance) > .01) affected++;
            if (affected < 30) throw new InvalidOperationException($"The archived model changed only {affected} GPU samples.");
            scene._Process(1d / 30);
            if (renderer.ArchivedBody("Sol/Earth") is not { Visible: true, MaterialOverride: ShaderMaterial } globe ||
                !ReferenceEquals(first, globe) || globe.GetSurfaceOverrideMaterial(0) is not null)
                throw new InvalidOperationException("Reusing the archive pool for a globe must clear all model overrides.");
            scene._Process(1d / 30); var restored = CheckModel();
            if (!ReferenceEquals(first, restored)) throw new InvalidOperationException("Model replay must reuse the bounded body pool.");
            scene._Process(1d / 30); CheckUnlit("Sol/Earth/OpaqueWitness", alpha: false); await Drawn();
            scene._Process(1d / 30); CheckUnlit("Sol/Earth/BlendWitness", alpha: true); await Drawn();
            scene._Process(1d / 30); CheckUnlit("Sol/Earth/StandardWitness", alpha: false, emission: false); await Drawn();
            scene._Process(1d / 30);
            if (renderer.ArchivedGeometry is not null || renderer.ArchivedBody(name) is not null || first.Mesh is not null)
                throw new InvalidOperationException("Completing model replay must release its mesh overrides and archived geometry.");
            if (visible.SavePng(Path.Combine(directory, "authored-archived-model.png")) != Error.Ok)
                throw new IOException("Could not retain the archived model image.");
            GD.Print($"Authored archived model GPU: normal/specular/emission maps, opaque/blended materials, model-to-globe pool reuse, clear and recovery; {affected} changed samples.");

            void CheckUnlit(string body, bool alpha, bool emission = true)
            {
                if (renderer.ArchivedBody(body) is not { Visible: true } node || !ReferenceEquals(first, node) ||
                    node.GetSurfaceOverrideMaterial(0) is not ShaderMaterial material ||
                    material.Shader.ResourcePath.EndsWith("_blend.gdshader", StringComparison.Ordinal) != alpha ||
                    Math.Abs(material.GetShaderParameter("tint").AsColor().A - (alpha ? .5 : 1)) > 1e-6 ||
                    !material.GetShaderParameter("has_map").AsBool() || material.GetShaderParameter("has_normal").AsBool() ||
                    (emission ? material.GetShaderParameter("emission_factor").AsVector3().Length() < .01f :
                        material.GetShaderParameter("emission_factor").AsVector3().Length() > 1e-6f))
                    throw new InvalidOperationException($"The archived {body} material lost its emission, opacity or replacement metadata.");
            }

            MeshInstance3D CheckModel()
            {
                var node = renderer.ArchivedBody(name);
                if (node is not { Visible: true, Mesh: ArrayMesh mesh } || mesh.GetSurfaceCount() != 1)
                    throw new InvalidOperationException($"The authored archive model is missing: {node?.Mesh?.GetType().Name}, " +
                        string.Join(",", renderer.ArchivedGeometry!.Bodies.Select(body => body.Name + "=" + body.Id)));
                if (node.GetSurfaceOverrideMaterial(0) is not ShaderMaterial material)
                    throw new InvalidOperationException("The authored model has no archived material override.");
                if (node.MaterialOverride is not null ||
                    !material.GetShaderParameter("has_map").AsBool() || !material.GetShaderParameter("has_normal").AsBool() ||
                    material.GetShaderParameter("packed_normal").AsBool() ||
                    material.GetShaderParameter("specular_color").AsVector3().DistanceTo(new(.2f, .3f, .4f)) > 1e-6 ||
                    Math.Abs(material.GetShaderParameter("specular_power").AsSingle() - 16) > 1e-6 ||
                    material.GetShaderParameter("emission_factor").AsVector3().Length() < .01f ||
                    material.GetShaderParameter("light_color").AsVector3().DistanceTo(new(1, .5f, .25f)) > 1e-6)
                    throw new InvalidOperationException($"Recorded model channels: map={material.GetShaderParameter("has_map")}, " +
                        $"normal={material.GetShaderParameter("has_normal")}, packed={material.GetShaderParameter("packed_normal")}, " +
                        $"specular={material.GetShaderParameter("specular_color")}/{material.GetShaderParameter("specular_power")}, " +
                        $"emission={material.GetShaderParameter("emission_factor")}, light={material.GetShaderParameter("light_color")}.");
                return node;
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, previousRoot);
            if (!await scene.ReloadAddOns() || !scene.LoadScenario(saved))
                throw new InvalidOperationException("Could not restore the original add-ons and observers after archive acceptance.");
            scene.SetProcess(true);
        }

        async Task Drawn()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }
}
