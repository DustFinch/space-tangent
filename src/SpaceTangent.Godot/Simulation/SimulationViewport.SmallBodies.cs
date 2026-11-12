// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<ObjectId, Label3D> _moonLabels = new();
    private IReadOnlyDictionary<ObjectId, ArrayMesh>? _moonModels;

    internal MeshInstance3D? NaturalMoonModel(ObjectId id) =>
        _moonModels?.ContainsKey(id) == true ? _planetGlobes[id] : null;

    internal void SetPlanetMoonModels(IReadOnlyDictionary<ObjectId, ArrayMesh> models)
    {
        _moonModels = models;
        foreach (var (id, mesh) in models)
        {
            _planetGlobes[id].Mesh = mesh;
            _planetGlobes[id].MaterialOverride = null;
        }
    }

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
                802 => new(1, 150.42f / 196.65f, 1),
                2000004 => new(1, 229f / 289, 280f / 289),
                _ => Vector3.One
            };
            if (NaturalMoonModel(body.Id) is { } model)
            {
                // The raw model vertices are kilometres in the source Z-up body frame.
                Basis3d rotation = snapshot.TryOrientation(body.Id, out FrameKinematics frame) ? frame.ToWorld : Basis3d.Identity;
                double size = scale / units;
                model.Transform = new(new Basis(ToGodot(rotation.X * size, 1),
                    ToGodot(rotation.Y * size, 1), ToGodot(rotation.Z * size, 1)), position);
                model.Show();
            }
            else
                PlaceWorldGlobe(_planetGlobes[body.Id], snapshot, body.Id, position, radii * (parentView ? Math.Max(.055f, radius) : radius));
            if (parentView)
            {
                Label3D moonLabel = _moonLabels[body.Id];
                moonLabel.Position = position - _navigationCamera.Basis.Y * Math.Max(.9f, radius * 1.15f);
                moonLabel.Show();
            }
        }
    }
}
