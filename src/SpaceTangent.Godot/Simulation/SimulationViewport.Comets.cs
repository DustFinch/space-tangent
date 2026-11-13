// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const double CometFadeDistance = 5 * 149_597_870.7;
    private const int MaximumActiveComets = 16;
    private readonly List<(ObjectId Id, CartesianState State, double Radius, double Distance)> _cometCandidates = [];
    private readonly List<CometVisual> _cometVisuals = [];

    private void DrawComets(WorldSnapshot snapshot, CartesianState sun, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        _cometCandidates.Clear();
        AddComet(NaturalBodyCatalogue.CometId, NaturalBodyCatalogue.Find(NaturalBodyCatalogue.CometId)!.RadiusKilometers);
        foreach (var entry in _importedNodes.Values)
            if (entry.Asset.Kind == CatalogueKind.Comet)
                AddComet(entry.Asset.Id, entry.Asset.RadiusKilometers);
        _cometCandidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        for (int i = 0; i < Math.Min(MaximumActiveComets, _cometCandidates.Count); i++)
        {
            if (_cometVisuals.Count <= i)
                _cometVisuals.Add(new(this));
            var (id, state, radius, distance) = _cometCandidates[i];
            CartesianState relative = state - sun;
            Vector3d away = relative.PositionKilometers / distance;
            Vector3d motion = relative.VelocityKilometersPerSecond;
            Vector3d trail = away * Vector3d.Dot(motion, away) - motion;
            if (trail.Length > 0)
                trail /= trail.Length;
            // A qualitative activity profile. Strong activity is confined to the inner system.
            double length = 1e15 / distance * (radius / 5);
            float brightness = .5f * (float)Math.Clamp((CometFadeDistance - distance) / (CometFadeDistance * .8), 0, 1);
            _cometVisuals[i].Draw(id, state.PositionKilometers, away, trail, length, brightness,
                units, _navigationCamera, project);
        }

        void AddComet(ObjectId id, double radius)
        {
            if (!snapshot.TryState(id, out var state))
                return;
            double distance = (state - sun).PositionKilometers.Length;
            if (distance > 0 && distance < CometFadeDistance)
                _cometCandidates.Add((id, state, radius, distance));
        }
    }

    private void HideComets()
    {
        foreach (var comet in _cometVisuals)
            comet.Hide();
    }

    internal (Vector3d Away, Vector3d Trail, double Length, float ComaFade)? RenderedComet(ObjectId id) =>
        _cometVisuals.FirstOrDefault(comet => comet.Id == id && comet.Visible)?.State;

    private sealed class CometVisual
    {
        private readonly CometRibbon _dust, _ion;
        private readonly MeshInstance3D _coma;
        private readonly ShaderMaterial _comaMaterial;

        internal CometVisual(Node parent)
        {
            var shader = GD.Load<Shader>("res://Shaders/comet_tail.gdshader");
            _dust = new(parent, Material(shader, new(.88f, .83f, .73f), false));
            _ion = new(parent, Material(shader, new(.35f, .55f, 1f), false));
            _comaMaterial = Material(shader, new(.89f, .93f, 1f), true);
            _coma = MakeNode(parent, new QuadMesh { Size = new(2, 2) }, _comaMaterial);
        }

        internal ObjectId Id { get; private set; }
        internal bool Visible => _dust.Visible && _ion.Visible;
        internal (Vector3d Away, Vector3d Trail, double Length, float ComaFade) State { get; private set; }

        internal void Draw(ObjectId id, Vector3d head, Vector3d away, Vector3d trail, double length, float brightness,
            double units, Camera3D camera, Func<Vector3d, (Vector3 Position, double Scale)> project)
        {
            Id = id;
            Vector3 eye = camera.Position;
            _dust.Draw(head, away, trail, length, length * .1, brightness, units, eye, project);
            _ion.Draw(head, away, default, length * 1.5, length * .03, brightness * .7f, units, eye, project);
            var (center, projection) = project(head);
            float radius = (float)(length * .01 * projection / units);
            float fade = Mathf.SmoothStep(.5f, 2f, eye.DistanceTo(center) / radius);
            State = (away, trail, length, fade);
            if (fade <= 0)
                return;
            _coma.Transform = new(camera.Basis.Scaled(Vector3.One * radius), center);
            _comaMaterial.SetShaderParameter("brightness", brightness * fade);
            _coma.Show();
        }

        internal void Hide()
        {
            _dust.Hide();
            _ion.Hide();
            _coma.Hide();
        }

        internal static ShaderMaterial Material(Shader shader, Vector3 color, bool radial)
        {
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("tint", color);
            material.SetShaderParameter("radial", radial);
            return material;
        }

        internal static MeshInstance3D MakeNode(Node parent, Mesh mesh, ShaderMaterial material)
        {
            var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            parent.AddChild(node);
            return node;
        }

        internal sealed class CometRibbon
        {
            private const int Segments = 32;
            private readonly Vector3[] _vertices = new Vector3[(Segments + 1) * 2];
            private readonly byte[] _bytes = new byte[(Segments + 1) * 2 * 3 * sizeof(float)];
            private readonly ArrayMesh _mesh = new();
            private readonly MeshInstance3D _node;
            private readonly ShaderMaterial _material;

            internal CometRibbon(Node parent, ShaderMaterial material)
            {
                var uv = new Vector2[_vertices.Length];
                var indices = new int[Segments * 6];
                for (int i = 0; i <= Segments; i++)
                {
                    int index = i * 2;
                    float along = (float)i / Segments;
                    _vertices[index] = new(-1, along, 0);
                    _vertices[index + 1] = new(1, along, 0);
                    uv[index] = new(0, along);
                    uv[index + 1] = new(1, along);
                    if (i < Segments)
                    {
                        int triangle = i * 6;
                        indices[triangle] = index;
                        indices[triangle + 1] = index + 1;
                        indices[triangle + 2] = index + 2;
                        indices[triangle + 3] = index + 2;
                        indices[triangle + 4] = index + 1;
                        indices[triangle + 5] = index + 3;
                    }
                }
                using var arrays = new global::Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
                arrays[(int)Mesh.ArrayType.TexUV] = uv;
                arrays[(int)Mesh.ArrayType.Index] = indices;
                _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, flags: Mesh.ArrayFormat.FlagUseDynamicUpdate);
                _node = MakeNode(parent, _mesh, material);
                _material = material;
            }

            internal bool Visible => _node.Visible;
            internal void Hide() => _node.Hide();

            internal void Draw(Vector3d head, Vector3d away, Vector3d trail, double length, double width,
                float brightness, double units, Vector3 eye, Func<Vector3d, (Vector3 Position, double Scale)> project,
                Basis? cameraBasis = null)
            {
                var bounds = new Aabb(project(head).Position, Vector3.Zero);
                for (int i = 0; i <= Segments; i++)
                {
                    double along = (double)i / Segments;
                    // Ease the spacing so the root remains resolved when the camera is close to the nucleus.
                    double sample = cameraBasis.HasValue ? along : along * along;
                    var (center, projection) = project(head + (away + trail * (.25 * sample)) * (length * sample));
                    var direction = away + trail * (.5 * sample);
                    Vector3 tangent = cameraBasis is { } basis
                        ? basis * new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z)
                        : ToGodot(direction, 1);
                    Vector3 side = tangent.Cross(eye - center).Normalized();
                    if (side.LengthSquared() < .5f)
                        side = tangent.Cross(Math.Abs(tangent.Y) > .9f ? Vector3.Right : Vector3.Up).Normalized();
                    float halfWidth = (float)(width * (.1 + .9 * sample) * .5 * projection / units);
                    _vertices[2 * i] = center - side * halfWidth;
                    _vertices[2 * i + 1] = center + side * halfWidth;
                    bounds = bounds.Expand(_vertices[2 * i]).Expand(_vertices[2 * i + 1]);
                }
                MemoryMarshal.AsBytes(_vertices.AsSpan()).CopyTo(_bytes);
                _mesh.SurfaceUpdateVertexRegion(0, 0, _bytes);
                _node.CustomAabb = bounds.Grow(.01f);
                _material.SetShaderParameter("brightness", brightness);
                _node.Show();
            }
        }
    }
}
