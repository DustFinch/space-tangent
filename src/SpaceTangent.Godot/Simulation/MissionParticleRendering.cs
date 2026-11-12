// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

internal sealed record PreparedParticleEmitter(MissionParticleEmitter Model, Texture2D? Texture);
internal readonly record struct MissionParticleRenderResult(int Count, int TexturedEmitters, Vector3? FirstVertex);

internal sealed class MissionParticleRendering(Node3D owner, Camera3D camera)
{
    private sealed record Entry(MeshInstance3D Node, ArrayMesh Mesh, ShaderMaterial Material);
    private const int ViewBudget = MissionParticleEmitter.MaximumLiveParticles;
    private readonly Dictionary<(ObjectId, int), Entry> _entries = new();
    private readonly Dictionary<ObjectId, MissionParticleRenderResult> _results = new();
    private readonly List<MissionParticle> _samples = [];
    private readonly List<Vector3> _positions = [];
    private readonly List<Vector2> _uvs = [];
    private readonly List<Color> _colors = [];
    private readonly List<int> _indices = [];
    private readonly Shader _shader = GD.Load<Shader>("res://Shaders/mission_particles.gdshader");
    private int _remaining;

    internal MissionParticleRenderResult? Result(ObjectId id) => _results.TryGetValue(id, out var value) ? value : null;

    internal void Hide()
    {
        _remaining = ViewBudget;
        _results.Clear();
        foreach (Entry entry in _entries.Values)
            entry.Node.Hide();
    }

    internal void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            owner.RemoveChild(entry.Node);
            entry.Node.QueueFree();
        }
        _entries.Clear();
        Hide();
    }

    internal void Draw(ObjectId id, IReadOnlyList<PreparedParticleEmitter> emitters, JulianInstant epoch,
        Vector3 position, Basis3d attitude, double projectedUnitsPerKilometer)
    {
        int drawn = 0, textured = 0;
        Vector3? firstVertex = null;
        Vector3 right = camera.Basis.X, up = camera.Basis.Y;
        float scale = (float)projectedUnitsPerKilometer;
        for (int index = 0; index < emitters.Count && _remaining > 0; index++)
        {
            PreparedParticleEmitter emitter = emitters[index];
            _samples.Clear();
            emitter.Model.Sample(epoch.ToJulianDate(), _remaining, _samples);
            if (_samples.Count == 0)
                continue;
            _positions.Clear();
            _uvs.Clear();
            _colors.Clear();
            _indices.Clear();
            foreach (MissionParticle particle in _samples)
            {
                Vector3 center = Direction(particle.Position) * scale;
                float size = (float)particle.HalfSizeKilometers * scale;
                if (size <= 0 || particle.Opacity <= 0)
                    continue;
                Vector3 velocity = Direction(particle.Velocity);
                var delta = new Vector2(velocity.Dot(right), velocity.Dot(up)) * ((float)emitter.Model.TraceSeconds * scale);
                Vector3 along, side;
                if (delta.LengthSquared() > 1e-24f)
                {
                    Vector2 heading = delta.Normalized();
                    along = (right * heading.X + up * heading.Y) * size;
                    side = (up * heading.X - right * heading.Y) * size;
                }
                else
                {
                    along = right * size;
                    side = up * size;
                }
                Vector3 stretch = right * delta.X + up * delta.Y;
                var tint = new Color((float)particle.Color.X, (float)particle.Color.Y,
                    (float)particle.Color.Z, (float)particle.Opacity);
                firstVertex ??= position + center - along - side;
                AddQuad(center - along - side, center + along - side + stretch,
                    center + along + side + stretch, center - along + side, tint);
            }
            if (_positions.Count == 0)
                continue;
            var key = (id, index);
            if (!_entries.TryGetValue(key, out var entry))
            {
                var mesh = new ArrayMesh();
                var material = new ShaderMaterial { Shader = _shader };
                var node = new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
                owner.AddChild(node);
                _entries.Add(key, entry = new(node, mesh, material));
            }
            entry.Material.SetShaderParameter("textured", emitter.Texture is not null);
            if (emitter.Texture is { } texture)
            {
                entry.Material.SetShaderParameter("particle_texture", texture);
                textured++;
            }
            using var arrays = new global::Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = _positions.ToArray();
            arrays[(int)Mesh.ArrayType.TexUV] = _uvs.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = _colors.ToArray();
            arrays[(int)Mesh.ArrayType.Index] = _indices.ToArray();
            entry.Mesh.ClearSurfaces();
            entry.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            entry.Mesh.SurfaceSetMaterial(0, entry.Material);
            entry.Node.Position = position;
            entry.Node.Show();
            drawn += _samples.Count;
            _remaining -= _samples.Count;
        }
        if (drawn > 0)
            _results.Add(id, new(drawn, textured, firstVertex));

        Vector3 Direction(Vector3d local)
        {
            Vector3d world = attitude.Transform(local);
            return new((float)world.X, (float)world.Z, (float)-world.Y);
        }
    }

    private void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color tint)
    {
        int first = _positions.Count;
        _positions.Add(a);
        _positions.Add(b);
        _positions.Add(c);
        _positions.Add(d);
        _uvs.Add(new(0, 1));
        _uvs.Add(new(1, 1));
        _uvs.Add(new(1, 0));
        _uvs.Add(new(0, 0));
        for (int index = 0; index < 4; index++)
            _colors.Add(tint);
        _indices.Add(first);
        _indices.Add(first + 1);
        _indices.Add(first + 2);
        _indices.Add(first);
        _indices.Add(first + 2);
        _indices.Add(first + 3);
    }
}
