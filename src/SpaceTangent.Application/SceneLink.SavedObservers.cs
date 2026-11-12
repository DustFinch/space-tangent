// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public static partial class SceneLink
{
    private const string ArchivedBookmarkScheme = "celestia-godot:";
    private const int MaximumObserverBytes = 8192;

    public static SimulationScenario ReadArchivedObserver(Stream source, SimulationWorld world, DeltaTModel deltaT)
    {
        using var reader = new StreamReader(source, leaveOpen: true);
        char[] buffer = new char[MaximumObserverBytes + 1];
        int length = reader.ReadBlock(buffer);
        if (length > MaximumObserverBytes)
            throw new InvalidDataException("The saved observer exceeds the supported size.");
        return RestoreArchivedObserver(new string(buffer, 0, length), world, deltaT);
    }

    private static SimulationScenario ReadArchivedBookmark(string payload, SimulationWorld world, DeltaTModel deltaT)
    {
        if (payload.Length > 16 * 1024)
            throw new InvalidDataException("The saved bookmark link exceeds the supported size.");
        using var document = JsonDocument.Parse(DecodeBase64(payload.Replace('-', '+').Replace('_', '/')),
            new JsonDocumentOptions { MaxDepth = 4 });
        return RestoreArchivedBookmark(document.RootElement, world, deltaT);
    }

    internal static SimulationScenario RestoreArchivedBookmark(JsonElement value, SimulationWorld world, DeltaTModel deltaT)
    {
        var bookmark = Fields(value);
        string state = bookmark["Observer"].GetString() ?? throw new InvalidDataException("The bookmark needs an observer state.");
        return RestoreArchivedObserver(state, world, deltaT, bookmark);
    }

    private static SimulationScenario RestoreArchivedObserver(string text, SimulationWorld world, DeltaTModel deltaT,
        IReadOnlyDictionary<string, JsonElement>? bookmark = null)
    {
        if (text.Length > MaximumObserverBytes)
            throw new InvalidDataException("The saved observer exceeds the supported size.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        var state = Fields(document.RootElement);
        if (state["schemaVersion"].GetInt32() != 1 ||
            state["frame"].GetString() != "J2000EclipticYUp" ||
            state["positionConvention"].GetString() != "universal-native-J2000-ecliptic-micro-light-year" ||
            state["orientationConvention"].GetString() != "active-camera-to-frame-wxyz")
            throw new InvalidDataException("The saved observer uses an unsupported coordinate convention.");
        var time = Fields(state["time"]);
        if (!Enum.TryParse(time["scale"].GetString(), out TimeScale scale) || !Enum.IsDefined(scale))
            throw new InvalidDataException("The saved observer uses an unsupported time scale.");
        var instant = new JulianInstant(long.Parse(time["wholeDays"].GetString()!, CultureInfo.InvariantCulture),
            long.Parse(time["nanosecondsOfDay"].GetString()!, CultureInfo.InvariantCulture), scale);
        var epoch = TimeConversion.Convert(instant, TimeScale.Tdb);
        if (epoch.CompareTo(world.Ephemeris.Start) < 0 || epoch.CompareTo(world.Ephemeris.End) > 0)
            throw new InvalidDataException("The saved observer epoch lies outside the ephemeris.");

        var bits = Fields(state["positionBits"]);
        var eye = FromEcliptic(new(ReadFixed(bits["x"]), ReadFixed(bits["y"]), ReadFixed(bits["z"])));
        var rotation = Fields(state["orientationBinary64"]);
        Vector3d axis = new(ReadDouble(rotation["x"]), ReadDouble(rotation["y"]), ReadDouble(rotation["z"]));
        double w = ReadDouble(rotation["w"]);
        double norm = Math.Sqrt(w * w + Vector3d.Dot(axis, axis));
        if (!double.IsFinite(norm) || Math.Abs(norm - 1) > .001)
            throw new InvalidDataException("The saved observer orientation must be a unit quaternion.");
        w /= norm;
        axis /= norm;
        var attitude = new NavigationPlacement(default, FromEcliptic(Rotate(new(0, 0, -1))), FromEcliptic(Rotate(new(0, 1, 0))));
        ObjectId selected = ResolveSavedObject(state["selectedId"].GetString()!, world.Catalogue);
        int mode = bookmark is null ? 0 : bookmark["Frame"].GetInt32();
        if (bookmark?.TryGetValue("SchemaVersion", out var schema) == true && schema.GetInt32() is not (1 or 2 or 3))
            throw new InvalidDataException("The saved bookmark schema is unsupported.");
        if (mode is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7))
            throw new InvalidDataException("The saved bookmark uses an unsupported observer frame.");
        ObjectId? target = mode == 0 ? null : OptionalObject("FrameReference") ?? selected;
        CatalogueCameraFrame? catalogueFrame = null;
        if (mode == 6)
        {
            target = OptionalObject("CatalogFrameOwner") ?? throw new InvalidDataException("The saved frame needs a catalogue object.");
            if (!bookmark!.TryGetValue("CatalogFrameSelectionJd", out var date) || date.ValueKind != JsonValueKind.Number ||
                !date.TryGetDouble(out double jd) || !double.IsFinite(jd) || jd < 0 || jd >= JulianInstant.MaximumDay + 1d)
                throw new InvalidDataException("The saved frame needs a valid selection epoch.");
            bool orbit = bookmark.TryGetValue("CatalogFrameOrbit", out var kind) && kind.GetBoolean();
            catalogueFrame = new(JulianInstant.FromJulianDate(jd, TimeScale.Tdb), orbit);
        }
        var frame = mode switch
        {
            2 => NavigationFrame.Chase,
            3 => NavigationFrame.PhaseLock,
            5 => NavigationFrame.Equatorial,
            6 => NavigationFrame.Catalogue,
            _ => NavigationFrame.Inertial
        };
        ObjectId? reference = frame is NavigationFrame.Inertial or NavigationFrame.Equatorial or NavigationFrame.Catalogue ? null :
            OptionalObject("FrameOther") ?? (target is { } body ? world.Catalogue.Find(body)?.Parent : null);
        double rate = bookmark is null ? 1 : bookmark["Rate"].GetDouble();
        double? field = bookmark?.TryGetValue("FieldOfViewDegrees", out var lens) == true && lens.ValueKind != JsonValueKind.Null
            ? lens.GetDouble() : null;
        if (!ObserverView.ValidPlaybackRate(rate) || field is { } fov && !ObserverView.ValidFieldOfView(fov))
            throw new InvalidDataException("The saved observer has an unsupported playback rate or field of view.");
        if (!TryRestoreAbsoluteCamera(world, world.Graph.Evaluate(epoch), deltaT, eye, attitude, selected,
                target, mode == 1, frame, reference, out NavigationCamera camera, catalogueFrame))
            throw new InvalidDataException("The saved observer camera or reference frame is unavailable.");
        var observer = new ObserverView(epoch, CameraMode.Solar, selected, rate != 0)
            { Navigation = camera, PlaybackRate = rate, FieldOfViewDegrees = field };
        return new([observer], true, 0, 0, deltaT);

        ObjectId? OptionalObject(string key) => bookmark?.TryGetValue(key, out var value) == true &&
            value.GetString() is { Length: > 0 } name ? ResolveSavedObject(name, world.Catalogue) : null;

        Vector3d Rotate(Vector3d vector)
        {
            Vector3d twiceCross = Vector3d.Cross(axis, vector) * 2;
            return vector + twiceCross * w + Vector3d.Cross(axis, twiceCross);
        }
    }

    private static ObjectId ResolveSavedObject(string? name, ObjectCatalogue catalogue)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("The saved observer must name a reference object.");
        if (name.Length > 0 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_'))
        {
            ObjectId canonical = new(name.ToLowerInvariant());
            if (catalogue.Find(canonical) is not null)
                return canonical;
        }
        return ResolveObject(name, catalogue);
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement value)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty item in value.EnumerateObject())
            if (!fields.TryAdd(item.Name, item.Value))
                throw new InvalidDataException($"The saved observer repeats {item.Name}.");
        return fields;
    }

    private static double ReadFixed(JsonElement value) =>
        (double)Int128.Parse(ReadHex(value, 32), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        / 18_446_744_073_709_551_616d * (StellarEntry.KilometersPerLightYear * 1e-6);

    private static double ReadDouble(JsonElement value) =>
        BitConverter.UInt64BitsToDouble(ulong.Parse(ReadHex(value, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));

    private static string ReadHex(JsonElement value, int length) => value.GetString() is { } text && text.Length == length
        ? text : throw new InvalidDataException("The saved observer has invalid coordinate bits.");
}
