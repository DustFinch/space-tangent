// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed record InstalledSscBody(ObjectId Id, ObjectId Parent, SscBodyDefinition Definition)
{
    public CatalogueKind Kind => Definition.Class.ToLowerInvariant() switch
    {
        "spacecraft" => CatalogueKind.Spacecraft,
        "planet" => CatalogueKind.Planet,
        "moon" or "minormoon" => CatalogueKind.Moon,
        "comet" => CatalogueKind.Comet,
        "dwarfplanet" or "dwarf planet" => CatalogueKind.DwarfPlanet,
        _ => CatalogueKind.Asteroid
    };

    internal static IReadOnlyList<InstalledSscBody> Install(WorldGraphBuilder graph, IEnumerable<SscBodyDefinition> definitions)
    {
        var paths = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sol"] = SolarSystemSample.SunId,
            ["SSB"] = SolarSystemSample.BarycenterId,
            ["Sol/Earth/Moon"] = SolarSystemSample.MoonId
        };
        var primaryPaths = new Dictionary<ObjectId, string> { [SolarSystemSample.SunId] = "Sol" };
        foreach (PlanetDefinition planet in PlanetCatalogue.Planets)
        {
            string path = "Sol/" + planet.Name;
            paths.Add(path, planet.Id);
            primaryPaths.Add(planet.Id, path);
        }
        foreach (NaturalBodyDefinition natural in NaturalBodyCatalogue.Bodies)
            paths.Add(primaryPaths[natural.Parent] + "/" + natural.Name, natural.Id);
        var ids = new HashSet<ObjectId>();
        var bodies = new List<(ObjectId Id, SscBodyDefinition Definition)>();
        foreach (SscBodyDefinition body in definitions)
        {
            if (bodies.Count == 512)
                throw new InvalidDataException("The enabled SSC sources contain more than 512 bodies.");
            string parent = Normalize(body.Parent);
            var id = new ObjectId("ssc/" + Slug(parent) + "/" + Slug(body.Name));
            if (!ids.Add(id))
                throw new InvalidDataException($"Duplicate SSC object identifier for {parent}/{body.Name}.");
            foreach (string name in body.Names.Split(':').Distinct(StringComparer.OrdinalIgnoreCase))
                if (!paths.TryAdd(parent + "/" + name, id))
                    throw new InvalidDataException($"Duplicate SSC path {parent}/{name}.");
            bodies.Add((id, body));
        }
        var installed = new List<InstalledSscBody>(bodies.Count);
        foreach (var (id, definition) in bodies)
        {
            ObjectId parent = Resolve(definition.Parent);
            if (definition.BindTimeline is { } timeline)
                graph.AddSampledMission(timeline(id, Resolve));
            else
            {
                graph.AddTrajectory(id, definition.Sample, Resolve(definition.Center));
                if (definition.SampleRotation is { } rotation)
                    graph.AddOrientation(id, rotation);
                else if (definition.Orientation is { } orientation)
                    graph.AddTwoVectorOrientation(id, orientation.Primary.Axis, Direction(orientation.Primary),
                        orientation.Secondary.Axis, Direction(orientation.Secondary));
                else
                    graph.AddOrientation(id, _ => new(Basis3d.Identity, default(Vector3d)));
            }
            installed.Add(new(id, parent, definition));
        }
        return installed.AsReadOnly();

        ObjectId Resolve(string path) => paths.TryGetValue(Normalize(path), out var id) ? id :
            throw new InvalidDataException($"An SSC body refers to the unavailable object {path}.");
        RelativeDirection Direction(SscBodyAxis axis) => new(Resolve(axis.Observer), Resolve(axis.Target), axis.Quantity);
        static string Normalize(string path) => path.Replace(':', '/').Trim('/');
        static string Slug(string path) => string.Join('/', path.Split('/').Select(part =>
        {
            string slug = Regex.Replace(part.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
            if (slug.Length == 0)
                throw new InvalidDataException("An SSC object needs an ASCII catalogue identifier.");
            return slug;
        }));
    }
}
