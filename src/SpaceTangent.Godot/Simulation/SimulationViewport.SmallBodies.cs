// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private readonly Dictionary<ObjectId, Label3D> _bodyLabels = new();
    private IReadOnlyDictionary<ObjectId, ArrayMesh>? _moonModels;

    internal Label3D? BodyLabel(ObjectId id) => _bodyLabels.GetValueOrDefault(id);

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
        }
    }

    private void DrawSmallBodies(WorldSnapshot snapshot, NavigationCamera pose, Vector3d eyeOffset, double units, SkyLayerSettings sky, ObjectId? selection,
        Func<Vector3d, (Vector3 Position, double Scale)> project)
    {
        foreach (NaturalBodyDefinition body in NaturalBodyCatalogue.Bodies)
        {
            if (_importedNodes.ContainsKey(body.Id) || !snapshot.TryState(body.Id, out CartesianState state))
                continue;
            var (position, scale) = project(state.PositionKilometers);
            float radius = (float)(body.RadiusKilometers * scale / units);
            Vector3d towardEye = (pose.AnchorKilometers - state.PositionKilometers) + eyeOffset;
            bool isMoon = body.Kind == CatalogueKind.Moon;
            bool resolved = DrawReflectedBody(body.Id, body.Parent, body.RadiusKilometers, NaturalBodyAlbedo(body.NaifId),
                new Color(body.Color), snapshot, towardEye, state.PositionKilometers, position, radius);
            Vector3 radii = body.NaifId switch
            {
                401 => new(1, 9.1f / 13, 11.4f / 13),
                402 => new(1, 5.1f / 7.8f, 6f / 7.8f),
                802 => new(1, 150.42f / 196.65f, 1),
                2000004 => new(1, 229f / 289, 280f / 289),
                _ => Vector3.One
            };
            if (resolved && NaturalMoonModel(body.Id) is { } model)
            {
                // The raw model vertices are kilometres in the source Z-up body frame.
                Basis3d rotation = snapshot.TryOrientation(body.Id, out FrameKinematics frame) ? frame.ToWorld : Basis3d.Identity;
                double size = scale / units;
                model.Transform = new(new Basis(ToGodot(rotation.X * size, 1),
                    ToGodot(rotation.Y * size, 1), ToGodot(rotation.Z * size, 1)), position);
                model.Show();
            }
            else if (resolved)
                PlaceWorldGlobe(_planetGlobes[body.Id], snapshot, body.Id, position, radii * radius);
            bool minor = body.Kind is CatalogueKind.Asteroid or CatalogueKind.Comet;
            if (sky.Labels && (!minor || sky.MinorBodyLabels || selection == body.Id))
                DrawBodyLabel(body.Id, body.Name, body.Parent, isMoon ? MoonOrbitRadius(body.NaifId) :
                    body.Kind == CatalogueKind.DwarfPlanet ? PlanetLabelOrbitRadius(body.NaifId) : 0,
                    body.RadiusKilometers, snapshot, state.PositionKilometers, towardEye, position, radius);
        }
    }

    private void DrawBodyLabel(ObjectId id, string name, ObjectId? parentId, double orbitRadius, double bodyRadius,
        WorldSnapshot snapshot, Vector3d center, Vector3d towardEye, Vector3 position, float radius, Vector3d? tint = null)
    {
        double distance = towardEye.Length;
        if (distance <= bodyRadius || _navigationCamera.IsPositionBehind(position))
            return;
        if (parentId is { } primary &&
            (CatalogueWorld is { } world ? world.TryNavigationState(primary, snapshot, out var parent) :
                snapshot.TryState(primary, out parent)))
        {
            Vector3d relativeParent = parent.PositionKilometers - center;
            Vector3d direction = towardEye / distance;
            double along = Vector3d.Dot(relativeParent, direction);
            double parentRadius = CatalogueWorld?.TargetRadiusKilometers(primary) ?? NavigationCamera.BodyRadiusKilometers(primary);
            double miss = (relativeParent - direction * along).Length;
            if (miss < parentRadius)
            {
                double halfChord = Math.Sqrt(parentRadius * parentRadius - miss * miss);
                if (along + halfChord > 0 && along - halfChord < distance)
                    return;
            }
        }

        const double minimumPixels = 20;
        double focalPixels = GetViewport().GetVisibleRect().Size.Y / (2 * Math.Tan(Math.PI * _navigationCamera.Fov / 360));
        if (focalPixels <= 0)
            return;
        double orbitPixels = orbitRadius > 0 ? focalPixels * orbitRadius / distance : double.PositiveInfinity;
        if (orbitPixels <= minimumPixels)
            return;

        if (!_bodyLabels.TryGetValue(id, out Label3D? label))
        {
            label = CreateLabel(name, Vector3.Zero);
            label.FixedSize = true;
            label.PixelSize = .0015f;
            label.NoDepthTest = false;
            _bodyLabels.Add(id, label);
            AddChild(label);
        }
        label.Text = name;
        float opacity = (float)Math.Min(1, (orbitPixels - minimumPixels) / minimumPixels);
        label.Modulate = tint is { } rgb ? new Color((float)rgb.X, (float)rgb.Y, (float)rgb.Z, opacity) : new Color(Colors.White, opacity);
        label.OutlineModulate = new Color(Colors.Black, opacity);
        Vector3 cameraOffset = _navigationCamera.Position - position;
        float depth = Math.Abs(cameraOffset.Dot(_navigationCamera.Basis.Z));
        float gap = (float)(depth * 12 / focalPixels);
        label.Position = position + cameraOffset.Normalized() * radius * 1.01f -
            _navigationCamera.Basis.Y * (radius + gap);
        label.Show();
    }

    private static double PlanetLabelOrbitRadius(int naif)
    {
        const double au = 149_597_870.7;
        return au * (naif switch
        {
            199 => .467, 299 => .728, 399 => 1.017, 499 => 1.666,
            599 => 5.457, 699 => 10.124, 799 => 20.10, 899 => 30.33,
            999 => 49.31, 2000001 => 2.767677892029369 * 1.075823549018329,
            _ => 0
        });
    }

    // Apocentres from the archived orbital elements, in kilometres.
    private static double MoonOrbitRadius(int naif) => naif switch
    {
        401 => 9378.938 * 1.01468, 402 => 23459.739 * 1.000283,
        501 => 421769 * 1.0041, 502 => 671079 * 1.0094, 503 => 1070428 * 1.0013, 504 => 1882700 * 1.0074,
        601 => 185539 * 1.0196, 602 => 238037 * 1.0047, 603 => 294672 * 1.0001, 604 => 377415 * 1.0022,
        605 => 527068 * 1.0012583, 606 => 1221865 * 1.0288, 607 => 1500934 * 1.0274, 608 => 3560851 * 1.0276812,
        609 => 12917060.944 * 1.152921, 701 => 191020 * 1.0012, 702 => 266000 * 1.0039,
        703 => 435910 * 1.0011, 704 => 583520 * 1.0014, 705 => 129390 * 1.0013,
        801 => 354759 * 1.000016, 802 => 5506217.008 * 1.753882, 808 => 117647 * 1.00053,
        _ => 0
    };
}
