// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public static partial class SceneLink
{
    public const string Scheme = "spacetangent://scene/";
    public const int MaximumLength = 44_000;
    private const string LegacyScheme = "cel://";

    public static string Write(SimulationScenario scenario) => Scheme +
        Convert.ToBase64String(ScenarioFile.Encode(scenario)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (SimulationScenario Scenario, string? Warning) Read(string text, SimulationWorld world, DeltaTModel deltaT)
    {
        string link = text.Trim();
        if (link.Length > MaximumLength)
            throw new FormatException("The scene link is too long.");
        if (link.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            string payload = link[Scheme.Length..].Replace('-', '+').Replace('_', '/');
            return (ScenarioFile.Decode(DecodeBase64(payload)), null);
        }
        if (!link.StartsWith(LegacyScheme, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Unrecognized scene link scheme.");
        return ReadExternal(link[LegacyScheme.Length..], world, deltaT);
    }

    private static (SimulationScenario, string?) ReadExternal(string text, SimulationWorld world, DeltaTModel deltaT)
    {
        int separator = text.IndexOf('?');
        if (separator <= 0)
            throw new FormatException("The link has no observer parameters.");
        string[] path = text[..separator].TrimEnd('/').Split('/');
        string mode = path[0].ToUpperInvariant();
        int bodies = mode switch
        {
            "FREEFLIGHT" => 0,
            "FOLLOW" or "SYNCORBIT" or "CHASE" => 1,
            "PHASELOCK" => 2,
            _ => throw new FormatException($"The link uses the unsupported {path[0]} frame.")
        };
        bool follow = bodies != 0;
        if (path.Length != bodies + 2)
            throw new FormatException("The link has an invalid observer path.");

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in text[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0 || !parameters.TryAdd(pair[..equals], Uri.UnescapeDataString(pair[(equals + 1)..])))
                throw new FormatException("The link has an invalid or repeated parameter.");
        }
        if (parameters.GetValueOrDefault("ver") is not ("3" or "4"))
            throw new FormatException("This link importer reads versions 3 and 4.");

        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (!DateTime.TryParseExact(Uri.UnescapeDataString(path[^1]), formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime date))
            throw new FormatException("The link has an invalid TDB calendar date.");
        DateTime j2000 = new(2000, 1, 1, 12, 0, 0);
        var epoch = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb).AddNanoseconds((Int128)(date.Ticks - j2000.Ticks) * 100);
        if (epoch.CompareTo(world.Ephemeris.Start) < 0 || epoch.CompareTo(world.Ephemeris.End) > 0)
            throw new InvalidDataException("The linked epoch lies outside the ephemeris.");
        var snapshot = world.Graph.Evaluate(epoch);
        ObjectId? parent = follow ? ResolveObject(Uri.UnescapeDataString(path[1]), world.Catalogue) : null;
        ObjectId? partner = mode switch
        {
            "CHASE" => world.Catalogue.Find(parent!.Value)?.Parent ?? throw new InvalidDataException("The chase target has no parent."),
            "PHASELOCK" => ResolveObject(Uri.UnescapeDataString(path[2]), world.Catalogue),
            _ => null
        };
        NavigationFrame frame = mode switch
        {
            "CHASE" => NavigationFrame.Chase,
            "PHASELOCK" => NavigationFrame.PhaseLock,
            _ => NavigationFrame.Inertial
        };
        ObjectId? selected = parameters.TryGetValue("select", out string? selection) && selection.Length != 0
            ? ResolveObject(selection, world.Catalogue) : parent;
        if (!world.TryNavigationState(parent ?? SolarSystemSample.SunId, snapshot, out CartesianState center))
            throw new InvalidDataException("The linked reference has no state at this epoch.");

        Vector3d offset = FrameCoordinates(new(ReadCoordinate(Value("x")), ReadCoordinate(Value("y")), ReadCoordinate(Value("z"))));
        double w = Number("ow");
        Vector3d quaternion = new(-Number("ox"), -Number("oy"), -Number("oz"));
        double norm = Math.Sqrt(w * w + Vector3d.Dot(quaternion, quaternion));
        if (!double.IsFinite(norm) || norm < .00001)
            throw new FormatException("The link has an invalid camera orientation.");
        quaternion /= norm;
        w /= norm;
        Vector3d forward = FrameCoordinates(Rotate(new(0, 0, -1)));
        Vector3d up = FrameCoordinates(Rotate(new(0, 1, 0)));
        Vector3d anchor = center.PositionKilometers + (follow ? default : offset);
        double scale = offset.Length;
        if (!follow)
            scale = selected is { } target && world.TryNavigationState(target, snapshot, out CartesianState selectedState)
                ? (anchor - selectedState.PositionKilometers).Length : 100_000;
        var camera = new NavigationCamera(anchor, Math.Max(NavigationCamera.MinRange, scale), 0, 0, parent, follow)
        {
            Placement = new(follow ? offset : default, forward, up, BodyFixed: mode == "SYNCORBIT") { Frame = frame, Reference = partner }
        };
        double field = parameters.ContainsKey("fov") ? Number("fov") : 49;
        if (!camera.IsValid() || !ObserverView.ValidFieldOfView(field))
            throw new FormatException("The linked camera exceeds the supported scale or field of view.");
        if (camera.Placement is { UsesFrame: true } && !camera.TryFrameBasis(snapshot, deltaT, out _))
            throw new InvalidDataException("The linked observer frame is unavailable at this epoch.");
        string? paused = parameters.GetValueOrDefault("p");
        if (paused is not (null or "0" or "1"))
            throw new FormatException("The link has an invalid pause flag.");

        double? rate = parameters.ContainsKey("ts") ? Number("ts") : null;
        if (rate is { } value && !ObserverView.ValidPlaybackRate(value))
            throw new FormatException("The link has an unsupported playback rate.");
        var warnings = parameters.Keys.Where(key => key is not ("ver" or "x" or "y" or "z" or "ow" or "ox" or "oy" or "oz"
            or "select" or "fov" or "p" or "ts") && !(key is "ltd" or "tsrc" && parameters[key] == "0")).ToArray();
        var observer = new ObserverView(epoch, CameraMode.Solar, selected, paused == "0" && rate != 0)
            { Navigation = camera, FieldOfViewDegrees = field, PlaybackRate = rate };
        return (new([observer], true, 0, 0, deltaT), warnings.Length == 0 ? null :
            $"Unapplied link parameters: {string.Join(", ", warnings)}.");

        string Value(string key) => parameters.TryGetValue(key, out var value) ? value :
            throw new FormatException($"The link needs the {key} parameter.");

        double Number(string key) => double.TryParse(Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) ? value : throw new FormatException($"The link has an invalid {key} parameter.");

        Vector3d FrameCoordinates(Vector3d vector) => mode switch
        {
            "SYNCORBIT" => new(vector.X, -vector.Z, vector.Y),
            "CHASE" or "PHASELOCK" => vector,
            _ => FromEcliptic(vector)
        };

        Vector3d Rotate(Vector3d vector)
        {
            Vector3d cross = Vector3d.Cross(quaternion, vector) * 2;
            return vector + cross * w + Vector3d.Cross(quaternion, cross);
        }
    }

    internal static ObjectId ResolveObject(string path, ObjectCatalogue catalogue)
    {
        string[] parts = path.Split([':', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidDataException("The link does not name a reference object.");
        if (parts.Length == 1 && parts[0].Equals("Sol", StringComparison.OrdinalIgnoreCase))
            return SolarSystemSample.SunId;
        foreach (CatalogueObject candidate in catalogue.Search(parts[^1]))
        {
            if (!Matches(candidate, parts[^1]))
                continue;
            CatalogueObject? parent = candidate;
            int segment = parts.Length - 1;
            while (segment > 0 && parent?.Parent is { } id)
            {
                parent = catalogue.Find(id);
                if (parent is null || !Matches(parent, parts[--segment]))
                    break;
            }
            if (segment == 0 && parent is not null && Matches(parent, parts[0]))
                return candidate.Id;
        }
        throw new InvalidDataException($"The link needs an unavailable object ({path}).");

        static bool Matches(CatalogueObject candidate, string name) =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            candidate.Aliases.Any(alias => alias.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            candidate.Id.Value.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) ||
            (candidate.Id == SolarSystemSample.SunId && name.Equals("Sol", StringComparison.OrdinalIgnoreCase));
    }

    private static Vector3d FromEcliptic(Vector3d value)
    {
        var (sin, cos) = Math.SinCos(23.4392911 * Math.PI / 180);
        return new(value.X, -value.Z * cos - value.Y * sin, value.Y * cos - value.Z * sin);
    }

    private static double ReadCoordinate(string text)
    {
        if (text.Length > 24)
            throw new FormatException("The linked position is too large.");
        byte[] data = DecodeBase64(text);
        if (data.Length > 16)
            throw new FormatException("The link has an invalid position.");
        Span<byte> bits = stackalloc byte[16];
        bits.Clear();
        data.AsSpan().CopyTo(bits);
        double microLightYears = (double)BinaryPrimitives.ReadInt128LittleEndian(bits) / 18_446_744_073_709_551_616d;
        return microLightYears * StellarEntry.KilometersPerLightYear / 1e6;
    }

    private static byte[] DecodeBase64(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
}
