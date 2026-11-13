// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

public sealed record TourConversion(string Lua, int Commands, IReadOnlyList<string> Unapplied)
{
    public IReadOnlyList<string> MediaFiles { get; init; } = [];
}

public static partial class TourConverter
{
    private const int MaximumBytes = 32 * 1024;
    private const string RecursiveTourSha256 = "bd3f8522a7854b51cbdb1ea8a00f22b0b13a955a38bdc4095bf10070e55f182c";
    private static readonly Regex Tokens = new(
        """\s+|#[^\r\n]*|"(?:\\.|[^"\\])*"|[{}\[\]]|[^\s{}\[\]"#]+""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private sealed record Command(string Name, Dictionary<string, string> Fields)
    {
        internal string Get(string key) => Fields.TryGetValue(key, out var value) ? value :
            throw new InvalidDataException($"The {Name} command needs {key}.");
        internal double Number(string key, double fallback = 0) => Fields.TryGetValue(key, out string? value)
            ? ParseNumber(value) : fallback;
    }

    public static TourConversion ConvertFile(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > MaximumBytes)
            throw new InvalidDataException("The tour exceeds 32 KiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        return ConvertSource(reader.ReadToEnd(), Path.GetExtension(path));
    }

    private static TourConversion ConvertSource(string input, string extension)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaximumBytes)
            throw new InvalidDataException("The tour exceeds 32 KiB.");
        if (extension.Equals(".celx", StringComparison.OrdinalIgnoreCase))
            return ConvertLuaTour(input);
        if (!extension.Equals(".cel", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Choose a CEL or CELX tour.");
        var words = Tokenize(input);
        if (words.Count >= 2 && words[0] == "{" &&
            words[1].ToLowerInvariant() is "name" or "target" or "description" or "distance" or "distanceunits")
            return ConvertGuide(words);
        Command[] commands = Parse(words);
        var unapplied = new SortedSet<string>(StringComparer.Ordinal);
        double wait = commands.Where(command => command.Name is "wait" or "orbit" or "rotate" or "changedistance")
            .Sum(command => Duration(command.Number("duration", command.Name == "wait" ? 0 : 1)));
        double scale = wait > 110 ? 110 / wait : 1;
        if (scale < 1)
            unapplied.Add("original timing compressed to fit the Lua wait quota");
        var lua = new StringBuilder("""
            local selected = true
            local function turn(x, y, z, angle, duration, orbit)
                while st.travelling() do st.wait(0) end
                local steps = math.max(1, math.min(16, math.ceil(duration * 8)))
                for step = 1, steps do
                    st.turnaxis(x, y, z, angle / steps, orbit)
                    if duration > 0 then st.wait(duration / steps) end
                end
            end
            local function dolly(amount, duration)
                while st.travelling() do st.wait(0) end
                st.legacydolly(amount, duration)
            end

            """);
        var mediaFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int converted = 0;
        foreach (Command command in commands)
        {
            switch (command.Name)
            {
                case "print":
                    var (horizontal, vertical) = (command.Fields.GetValueOrDefault("origin") ?? "").ToLowerInvariant() switch
                    {
                        "center" => (0, 0), "left" => (-1, 0), "right" => (1, 0),
                        "topleft" => (-1, 1), "top" => (0, 1), "topright" => (1, 1),
                        "bottom" => (0, -1), "bottomright" => (1, -1), _ => (-1, -1)
                    };
                    double seconds = Math.Clamp(command.Number("duration", 600), 0, 600) * scale;
                    lua.AppendLine($"st.notice({Quote(command.Fields.GetValueOrDefault("text") ?? "")}, {Number(seconds)}, " +
                        $"{horizontal}, {vertical}, {Number(Math.Truncate(command.Number("column")))}, {Number(Math.Truncate(command.Number("row")))})");
                    break;
                case "overlay":
                    mediaFiles.Add(command.Get("filename"));
                    lua.AppendLine($"st.overlay({Quote(command.Get("filename"))}, {Number(command.Number("duration", 3) * scale)}, " +
                        $"{Number(.5 + command.Number("xoffset"))}, {Number(.5 - command.Number("yoffset"))}, " +
                        $"{Number(command.Number("width"))}, {Number(command.Number("height"))}, {Number(command.Number("alpha", 1))})");
                    break;
                case "play":
                    if (command.Fields.GetValueOrDefault("filename") is { Length: > 0 } sound)
                        mediaFiles.Add(sound);
                    string loop = command.Fields.GetValueOrDefault("loop") is "true" or "1" ? "true" : "false";
                    lua.AppendLine($"st.play({Quote(command.Fields.GetValueOrDefault("filename") ?? "")}, " +
                        $"{Number(command.Number("volume", 1))}, {Number(command.Number("channel"))}, {loop})");
                    foreach (string field in new[] { "pan", "nopause" }.Where(command.Fields.ContainsKey))
                        unapplied.Add("play " + field);
                    break;
                case "cls":
                    lua.AppendLine("st.notice(\"\", 0)");
                    break;
                case "select":
                    lua.AppendLine($"selected = st.tryselect({Quote(command.Get("object"))})");
                    break;
                case "goto":
                    lua.AppendLine($"if selected then st.legacygoto({FlightTime(command)}, {Number(command.Number("distance", 5))}, " +
                        $"{Vector(command, "up", "0 1 0")}, {Quote(command.Fields.GetValueOrDefault("upframe") ?? "observer")}) end");
                    break;
                case "gotolonglat":
                    lua.AppendLine($"if selected then st.legacylonglat({FlightTime(command)}, {Number(command.Number("distance", 5))}, " +
                        $"{Vector(command, "up", "0 1 0")}, {Number(command.Number("longitude"))}, {Number(command.Number("latitude"))}) end");
                    if (command.Fields.ContainsKey("upframe"))
                        unapplied.Add("gotolonglat upframe");
                    break;
                case "gotoloc" when command.Fields.ContainsKey("position"):
                    lua.AppendLine($"if selected then st.legacyrelativepose({FlightTime(command)}, {Vector(command, "position", "")}, " +
                        $"{Number(command.Number("xrot"))}, {Number(command.Number("yrot"))}, {Number(command.Number("zrot"))}) end");
                    break;
                case "gotoloc" when command.Fields.ContainsKey("x") && command.Fields.ContainsKey("y") && command.Fields.ContainsKey("z"):
                    lua.AppendLine($"st.legacyabsolutepose({FlightTime(command)}, {Quote(command.Get("x"))}, {Quote(command.Get("y"))}, " +
                        $"{Quote(command.Get("z"))}, {Number(command.Number("ow", 1))}, {Number(command.Number("ox"))}, " +
                        $"{Number(command.Number("oy"))}, {Number(command.Number("oz"))})");
                    break;
                case "time" when command.Fields.ContainsKey("jd"):
                    lua.AppendLine($"st.settime({Number(command.Number("jd"))})");
                    break;
                case "center":
                    lua.AppendLine($"if selected then st.center({Number(Duration(command.Number("time", 1.65)) * scale)}) end");
                    break;
                case "follow":
                    lua.AppendLine("if selected then st.follow() end");
                    break;
                case "synchronous":
                    lua.AppendLine("if selected then st.legacybodylock() end");
                    break;
                case "cancel":
                    lua.AppendLine("st.cancelflight()");
                    break;
                case "wait":
                    lua.AppendLine($"st.wait({Number(Duration(command.Number("duration")) * scale)})");
                    break;
                case "timerate":
                    lua.AppendLine($"st.rate({Number(command.Number("rate"))})");
                    break;
                case "labels":
                case "renderflags":
                    AppendFlags(command, true);
                    AppendFlags(command, false);
                    break;
                case "changedistance":
                    double dollyDuration = Duration(command.Number("duration", 1));
                    lua.AppendLine($"if selected then dolly({Number(command.Number("rate") * dollyDuration)}, {Number(dollyDuration * scale)}) end");
                    break;
                case "orbit":
                case "rotate":
                    double duration = Duration(command.Number("duration", 1));
                    double angle = -command.Number("rate") * duration * Math.PI / 180;
                    bool orbit = command.Name == "orbit";
                    string turn = $"turn({Vector(command, "axis", "0 1 0")}, {Number(angle)}, {Number(duration * scale)}, " +
                        (orbit ? "true)" : "false)");
                    lua.AppendLine(orbit ? $"if selected then {turn} end" : turn);
                    break;
                default:
                    unapplied.Add(command.Name);
                    continue;
            }
            converted++;
        }
        foreach (string item in unapplied.Reverse())
            lua.Insert(0, "-- Unapplied: " + item + "\n");
        return new(lua.ToString(), converted, Array.AsReadOnly(unapplied.ToArray())) { MediaFiles = Array.AsReadOnly(mediaFiles.ToArray()) };

        void AppendFlags(Command command, bool enabled)
        {
            if (!command.Fields.TryGetValue(enabled ? "set" : "clear", out string? flags))
                return;
            foreach (string flag in flags.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                string? layer = (command.Name, flag) switch
                {
                    ("renderflags", "stars") => "stars",
                    ("renderflags", "constellations") => "constellations",
                    ("renderflags", "galaxies") => "deep",
                    ("labels", "stars") => "labels",
                    _ => null
                };
                if (layer is not null)
                    lua.AppendLine($"st.sky({Quote(layer)}, {(enabled ? "true" : "false")})");
                else
                    unapplied.Add(command.Name + " " + flag);
            }
        }

        string FlightTime(Command command) => Number(Duration(command.Number("time", 1)) * scale);
    }

    private static string Vector(Command command, string field, string fallback)
    {
        string raw = command.Fields.GetValueOrDefault(field) ?? fallback;
        string[] fields = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3)
            throw new InvalidDataException($"The {command.Name} command requires a three-component {field} vector.");
        return string.Join(", ", fields.Select(ParseNumber).Select(Number));
    }

    private static TourConversion ConvertLuaTour(string source)
    {
        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        if (!hash.Equals(RecursiveTourSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { new Script(CoreModules.None).LoadString(source); }
            catch (InterpreterException error)
            {
                throw new InvalidDataException(error.DecoratedMessage ?? error.Message, error);
            }
            // Other scripts already use Lua. The ordinary runner applies its sandbox and quotas on execution.
            return new(source, 1, Array.Empty<string>());
        }
        const string lua = """
            local targets = st.cataloguetour()
            local flight = math.min(3, 96 / #targets)
            for _, target in ipairs(targets) do
                if st.tryselect(target) then
                    local object = st.selection()
                    st.celxprint(object:type() .. " - " .. object:name(), 1.5)
                    st.gotoobject(flight, 4.2)
                    st.wait(flight + 0.05)
                    st.follow()
                    st.wait(0.35)
                end
            end
            """;
        return new(lua, 1, Array.Empty<string>());
    }

    private static List<string> Tokenize(string input)
    {
        var words = new List<string>();
        int offset = 0;
        foreach (Match match in Tokens.Matches(input))
        {
            if (match.Index != offset)
                throw new InvalidDataException("The tour contains an invalid token.");
            offset += match.Length;
            if (!char.IsWhiteSpace(match.Value[0]) && match.Value[0] != '#')
                words.Add(match.Value);
        }
        if (offset != input.Length)
            throw new InvalidDataException("The tour contains a truncated token.");
        return words;
    }

    private static Command[] Parse(IReadOnlyList<string> words)
    {
        int at = 0;
        Expect("{");
        var commands = new List<Command>();
        while (Peek() != "}")
        {
            if (commands.Count == 256)
                throw new InvalidDataException("The tour contains too many commands.");
            string command = Take();
            Expect("{");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            while (Peek() != "}")
            {
                string key = Take();
                string value = Take();
                if (value == "[")
                {
                    var vector = new List<string>();
                    while (Peek() != "]" && vector.Count < 16)
                        vector.Add(Take());
                    Expect("]");
                    value = string.Join(' ', vector);
                }
                else if (value.StartsWith('"'))
                    value = DecodeValue(value);
                if (!fields.TryAdd(key, value))
                    throw new InvalidDataException("A tour field is repeated.");
            }
            Expect("}");
            commands.Add(new(command, fields));
        }
        Expect("}");
        if (at != words.Count)
            throw new InvalidDataException("Unexpected text after the tour.");
        return commands.ToArray();

        string Peek() => at < words.Count ? words[at] : throw new InvalidDataException("The tour ended early.");
        string Take() { string value = Peek(); at++; return value; }
        void Expect(string value)
        {
            if (Take() != value)
                throw new InvalidDataException($"Expected {value} in the tour.");
        }
    }

    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (char value in text)
        {
            result.Append(value switch
            {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ when value < ' ' => "\\" + ((int)value).ToString("D3", CultureInfo.InvariantCulture),
                _ => value.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    private static string Number(double value) => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) :
        throw new InvalidDataException("A converted tour number is outside the supported range.");

    private static double ParseNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number)
            ? number : throw new InvalidDataException("The tour has an invalid number.");

    private static double Duration(double seconds) => seconds is >= 0 and <= 300 ? seconds :
        throw new InvalidDataException("A tour duration is outside the supported range.");
}
