// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed record InstalledSscBody(ObjectId Id, ObjectId Parent, SscBodyDefinition Definition)
{
    public ObjectId? StellarHost { get; init; }
    public (ObjectId Center, IReadOnlyList<Vector3d> Points)? Orbit { get; init; }
    public CatalogueKind Kind => (StellarHost.HasValue && Definition.Class == "asteroid" ?
        Parent == StellarHost ? "planet" : "moon" : Definition.Class.ToLowerInvariant()) switch
    {
        "spacecraft" => CatalogueKind.Spacecraft,
        "planet" => CatalogueKind.Planet,
        "moon" or "minormoon" => CatalogueKind.Moon,
        "comet" => CatalogueKind.Comet,
        "dwarfplanet" or "dwarf planet" => CatalogueKind.DwarfPlanet,
        _ => CatalogueKind.Asteroid
    };

    internal static NaturalBodyDefinition? FindBuiltInMinorPlanet(SscBodyDefinition definition)
    {
        if (!definition.Parent.Equals("Sol", StringComparison.OrdinalIgnoreCase))
            return null;
        return NaturalBodyCatalogue.Bodies.FirstOrDefault(body =>
            body.NaifId is > 2_000_000 and < 3_000_000 &&
            (definition.Name.Equals(body.Name, StringComparison.OrdinalIgnoreCase) ||
                definition.Name.Equals(FormattableString.Invariant($"{body.NaifId - 2_000_000} {body.Name}"),
                    StringComparison.OrdinalIgnoreCase)));
    }

    internal static IReadOnlyList<InstalledSscBody> Install(WorldGraphBuilder graph, IEnumerable<SscBodyDefinition> definitions, StellarCatalogue stars, ExoplanetCatalogue exoplanets, BrightStarCatalogue brightStars, Vector3d stellarOrigin, PlanetEphemerides? planets)
    {
        if (definitions.TryGetNonEnumeratedCount(out int count) && count == 0)
            return [];

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
        var stellarPaths = new Dictionary<string, StellarEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var star in stars.Stars.Concat(exoplanets.Hosts.Where(host => exoplanets.CanonicalHost(host.Id) == host.Id)))
        {
            stellarPaths.TryAdd(Normalize(star.Name), star);
            foreach (string alias in star.Aliases)
                stellarPaths.TryAdd(Normalize(alias), star);
        }

        foreach (var bright in brightStars.Stars)
            if (stars.Find(bright.Id) is { IsBarycenter: false } star)
                stellarPaths.TryAdd(Normalize(bright.Name), star);

        var hosts = new Dictionary<ObjectId, StellarEntry>();
        var installedHostRules = new HashSet<ObjectId>();
        foreach (var planet in exoplanets.Bodies)
        {
            if (exoplanets.FindHost(planet.Host) is not { } fallback)
                continue;
            var star = stars.Find(exoplanets.CanonicalHost(planet.Host)) ?? fallback;
            var names = star.Aliases.Append(star.Name).Append(fallback.Name).Concat(fallback.Aliases);
            foreach (string host in names.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string name in planet.Names)
                    paths.TryAdd(host + "/" + Normalize(name), planet.Id);
            hosts[planet.Id] = star;
        }

        var ids = new HashSet<ObjectId>();
        var bodies = new List<(ObjectId Id, SscBodyDefinition Definition)>();
        foreach (SscBodyDefinition body in definitions)
        {
            if (bodies.Count == 512)
                throw new InvalidDataException("The enabled SSC sources contain more than 512 bodies.");
            string parent = Normalize(body.Parent);
            var id = FindBuiltInMinorPlanet(body)?.Id ?? new ObjectId("ssc/" + Slug(parent) + "/" + Slug(body.Name));
            if (!ids.Add(id))
                throw new InvalidDataException($"Duplicate SSC object identifier for {parent}/{body.Name}.");
            foreach (string name in body.Names.Split(':').Distinct(StringComparer.OrdinalIgnoreCase))
                if (!paths.TryAdd(parent + "/" + name, id) && paths[parent + "/" + name] != id)
                    throw new InvalidDataException($"Duplicate SSC path {parent}/{name}.");
            string hostName = parent.Split('/')[0];
            if (stellarPaths.TryGetValue(hostName, out var star))
            {
                hosts.Add(id, star);
                paths.TryAdd(hostName, star.Id);
            }
            bodies.Add((id, body));
        }
        var importedRadii = bodies.ToDictionary(body => body.Id, body => body.Definition.RadiusKilometers);
        var installed = new List<InstalledSscBody>(bodies.Count);
        foreach (var (id, definition) in bodies)
        {
            ObjectId parent = Resolve(definition.Parent);
            (ObjectId Center, IReadOnlyList<Vector3d> Points)? orbit = null;
            if (definition.BindTimeline is { } timeline)
                graph.AddSampledMission(timeline(id, Resolve, Radius));
            else
            {
                ObjectId center = Resolve(definition.Center);
                if (hosts.TryGetValue(id, out var star))
                {
                    InstallHost(star);
                    if (exoplanets.Find(center) is { } planet && installedHostRules.Add(planet.Id))
                        graph.AddTrajectory(planet.Id, epoch => planet.ParentRelativeState(epoch), star.Id);
                }
                var sample = definition.Sample;
                if (hosts.TryGetValue(id, out var host) && definition.BindExtrasolarOrbit is { } bind)
                {
                    var conic = bind(center == host.Id);
                    sample = conic.Sample;
                    orbit = (center, conic.Points);
                }
                graph.AddTrajectory(id, sample, center);
                if (definition.SampleRotation is { } rotation)
                    graph.AddOrientation(id, rotation);
                else if (definition.Orientation is { } orientation)
                    graph.AddTwoVectorOrientation(id, orientation.Primary.Axis, Direction(orientation.Primary),
                        orientation.Secondary.Axis, Direction(orientation.Secondary));
                else
                    graph.AddOrientation(id, _ => new(Basis3d.Identity, default(Vector3d)));
            }
            var hostId = hosts.TryGetValue(id, out var stellarHost) ? (ObjectId?)stellarHost.Id : null;
            installed.Add(new(id, parent, definition) { StellarHost = hostId, Orbit = orbit });
        }
        return installed.AsReadOnly();

        void InstallHost(StellarEntry star)
        {
            if (!installedHostRules.Add(star.Id))
                return;
            graph.AddTrajectory(star.Id, epoch =>
            {
                var state = star.StateAt(epoch);
                return new(state.PositionKilometers + stellarOrigin, state.VelocityKilometersPerSecond);
            });
        }

        double Radius(ObjectId body) => importedRadii.TryGetValue(body, out double radius) ? radius :
            planets?.EquatorialRadiusKilometers(body) ?? (NaturalBodyCatalogue.Find(body)?.RadiusKilometers ??
                PlanetCatalogue.Find(body)?.RadiusKilometers ?? (body == SolarSystemSample.MoonId ||
                    body == SolarSystemSample.SunId ? NavigationCamera.BodyRadiusKilometers(body) :
                    throw new InvalidDataException($"No SSC surface radius is available for {body.Value}.")));

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
