// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private MeshInstance3D _cometTail = null!;
    private readonly Dictionary<ObjectId, Label3D> _moonLabels = new();

    private void InitializeSmallBodies()
    {
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            _planetGlobes.Add(body.Id, CreateNavigationSphere(new Color(body.Color)));
            if (body.Kind != CatalogueKind.Moon)
                continue;
            Label3D label = CreateLabel(body.Name, Vector3.Zero);
            label.PixelSize = .035f;
            label.NoDepthTest = false;
            label.Hide();
            _moonLabels.Add(body.Id, label);
            AddChild(label);
        }
        _cometTail = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new(2, 2) },
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/comet_tail.gdshader") },
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        AddChild(_cometTail);
    }

    private void DrawSmallBodies(WorldSnapshot snapshot, NavigationCamera pose, double units,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            if (!snapshot.TryState(body.Id, out CartesianState state))
                continue;
            var (position, scale) = project(state.PositionKilometers);
            float radius = (float)(body.RadiusKilometers * scale / units);
            // A world-scale observer sees the moons as points and a close observer sees physical sizes.
            bool parentView = pose.Target == body.Parent && _moonLabels.ContainsKey(body.Id);
            if (radius < .035f && pose.Target != body.Id && !parentView)
                continue;
            Vector3 radii = body.NaifId switch
            {
                401 => new(1, 9.1f / 13, 11.4f / 13),
                402 => new(1, 5.1f / 7.8f, 6f / 7.8f),
                2000004 => new(1, 229f / 289, 280f / 289),
                _ => Vector3.One
            };
            PlaceWorldGlobe(_planetGlobes[body.Id], snapshot, body.Id, position, radii * (parentView ? Math.Max(.055f, radius) : radius));
            if (parentView)
            {
                Label3D moonLabel = _moonLabels[body.Id];
                moonLabel.Position = position - _navigationCamera.Basis.Y * Math.Max(.9f, radius * 1.15f);
                moonLabel.Show();
            }
            if (body.Id != NaturalBodyCatalogue.CometId || radius < .1f)
                continue;
            Vector3 antisolar = ToGodot((state - snapshot.SolarSystem!.Value.Sun).PositionKilometers, 1).Normalized();
            Vector3 eye = (_navigationCamera.Position - position).Normalized();
            Vector3 width = antisolar.Cross(eye);
            if (width.LengthSquared() < .0001f)
                continue;
            width = width.Normalized();
            Vector3 normal = width.Cross(antisolar);
            // Illustrative inner tail scaled to the nucleus. Orientation is geometric and anti-solar.
            float length = radius * 14;
            _cometTail.Transform = new(new Basis(width * radius * 2.5f, antisolar * length / 2, normal),
                position + antisolar * (radius * .8f + length / 2));
            _cometTail.Show();
        }
    }

    internal bool CometTailVisible => _cometTail.Visible;
    internal Vector3 CometTailDirection => _cometTail.Transform.Basis.Y.Normalized();
}
