// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

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

    private sealed record Command(string Name, int Line, Dictionary<string, string> Fields)
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
        var words = Tokenize(input, out var lines);
        if (words.Count >= 2 && words[0] == "{" &&
            words[1].ToLowerInvariant() is "name" or "target" or "description" or "distance" or "distanceunits")
            return ConvertGuide(words);
        Command[] commands = Parse(words, lines);
        var unapplied = new SortedSet<string>(StringComparer.Ordinal);
        double wait = commands.TakeWhile(command => command.Name != "exit")
            .Where(command => command.Name is "wait" or "orbit" or "rotate" or "changedistance" or "move")
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
            void Report(string message) => unapplied.Add($"line {command.Line}: {message}");
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
                case "mark":
                case "unmark":
                    string target = command.Fields.GetValueOrDefault("object") ?? "";
                    string lookup = target.Length == 0 ? "(selected and st.selection())" : $"st.find({Quote(target)})";
                    if (command.Name == "unmark")
                        lua.AppendLine($"do local item = {lookup}; if item then item:unmark() end end");
                    else
                    {
                        string color = MarkerColor(command.Fields.GetValueOrDefault("color") ?? "#00ff00");
                        string symbol = command.Fields.GetValueOrDefault("symbol") ?? "diamond";
                        lua.AppendLine($"do local item = {lookup}; if item then item:mark({Quote(color)}, {Quote(symbol)}, " +
                            $"{Number(command.Number("size", 10))}, {Number(command.Number("alpha", 1))}) end end");
                        if (command.Fields.ContainsKey("occludable"))
                            Report("mark occludable");
                    }
                    break;
                case "unmarkall":
                    lua.AppendLine("st.legacyclearmarks()");
                    break;
                case "overlay":
                    mediaFiles.Add(command.Get("filename"));
                    foreach (string field in new[] { "fitscreen", "color", "colortop", "colorbottom", "colortopleft",
                        "colortopright", "colorbottomleft", "colorbottomright", "fadeafter" }.Where(command.Fields.ContainsKey))
                        Report("overlay " + field);
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
                        Report("play " + field);
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
                        Report("gotolonglat upframe");
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
                case "time" when command.Fields.TryGetValue("utc", out string? utc) &&
                    DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date):
                    var label = new UtcDateTime(DateOnly.FromDateTime(date.UtcDateTime), date.Hour, date.Minute,
                        date.Second, (int)(date.Ticks % TimeSpan.TicksPerSecond) * 100);
                    var instant = TimeConversion.FromUtc(label, TimeScale.Tdb);
                    lua.AppendLine($"st.settime({Number(instant.ToJulianDate())})");
                    break;
                case "set" when string.Equals(command.Fields.GetValueOrDefault("name"), "FOV", StringComparison.OrdinalIgnoreCase):
                    double degrees = command.Number("value", 45);
                    if (!ObserverView.ValidFieldOfView(degrees))
                    {
                        Report("set FOV outside the supported range");
                        continue;
                    }
                    lua.AppendLine($"st.legacyfov({Number(degrees)})");
                    break;
                case "setposition" when command.Fields.ContainsKey("x") && command.Fields.ContainsKey("y") && command.Fields.ContainsKey("z"):
                    lua.AppendLine($"st.legacyposition({Quote(command.Get("x"))}, {Quote(command.Get("y"))}, {Quote(command.Get("z"))})");
                    break;
                case "setposition" when command.Fields.ContainsKey("base"):
                    lua.AppendLine($"st.legacypositionkm({Vector(command, "base", "")}, {Vector(command, "offset", "0 0 0")})");
                    break;
                case "setorientation" when command.Fields.ContainsKey("axis") && command.Fields.ContainsKey("angle"):
                    lua.AppendLine($"st.legacyorientationaxis({Vector(command, "axis", "")}, {Number(command.Number("angle"))})");
                    break;
                case "setorientation":
                    lua.AppendLine($"st.legacyorientation({Number(command.Number("ow", 1))}, {Number(command.Number("ox"))}, " +
                        $"{Number(command.Number("oy"))}, {Number(command.Number("oz"))})");
                    break;
                case "lookback":
                    lua.AppendLine("st.legacylookback()");
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
                case "splitview":
                    string direction = command.Fields.GetValueOrDefault("type") ?? command.Fields.GetValueOrDefault("split") ?? "v";
                    lua.AppendLine($"st.legacyview(\"splitview\", {Number(command.Number("view", 1))}, " +
                        $"{Quote(direction)}, {Number(command.Number("position", .5))})");
                    break;
                case "deleteview":
                case "makeactiveview":
                    lua.AppendLine($"st.legacyview({Quote(command.Name)}, {Number(command.Number("view", 1))})");
                    break;
                case "singleview":
                    lua.AppendLine("st.legacyview(\"singleview\")");
                    if (command.Fields.ContainsKey("view"))
                        Report("singleview view");
                    break;
                case "chase":
                case "lock":
                    lua.AppendLine($"if selected then st.legacyframe({Quote(command.Name)}, st.selection()) end");
                    break;
                case "setframe":
                    string? frame = (command.Fields.GetValueOrDefault("coordsys") ?? "universal").ToLowerInvariant() switch
                    {
                        "universal" => "universal",
                        "ecliptic" or "ecliptical" or "observer" => "inertial",
                        "equatorial" => "equatorial",
                        "geographic" or "bodyfixed" or "planetographic" => "bodyfixed",
                        "chase" => "chase",
                        "lock" or "phaselock" => "lock",
                        _ => null
                    };
                    if (frame is null)
                    {
                        Report("setframe " + command.Get("coordsys"));
                        continue;
                    }
                    if (frame == "universal")
                        lua.AppendLine("st.legacyframe(\"universal\")");
                    else
                    {
                        string subject = command.Fields.TryGetValue("ref", out string? reference)
                            ? $"st.find({Quote(reference)})" : "(selected and st.selection() or nil)";
                        if (frame == "lock" && command.Fields.TryGetValue("target", out string? secondary))
                            lua.AppendLine($"do local subject, other = {subject}, st.find({Quote(secondary)}); " +
                                "if subject and other then st.legacyframe(\"lock\", subject, other) end end");
                        else
                            lua.AppendLine($"do local subject = {subject}; if subject then st.legacyframe({Quote(frame)}, subject) end end");
                    }
                    break;
                case "cancel":
                    lua.AppendLine("st.cancelflight()");
                    break;
                case "exit":
                    lua.AppendLine("do return end");
                    break;
                case "move":
                    double moveDuration = Duration(command.Number("duration", 1));
                    lua.AppendLine($"st.legacymove({Vector(command, "velocity", "0 0 0")}, {Number(moveDuration)}, {Number(moveDuration * scale)})");
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
                    Report(command.Name);
                    continue;
            }
            converted++;
            if (command.Name == "exit")
                break;
        }
        foreach (string item in unapplied.Reverse())
            lua.Insert(0, "-- Unapplied: " + item + "\n");
        return new(lua.ToString(), converted, Array.AsReadOnly(unapplied.ToArray())) { MediaFiles = Array.AsReadOnly(mediaFiles.ToArray()) };

        void AppendFlags(Command command, bool enabled)
        {
            if (!command.Fields.TryGetValue(enabled ? "set" : "clear", out string? flags))
                return;
            foreach (string flag in flags.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? layer = (command.Name, flag.ToLowerInvariant()) switch
                {
                    ("renderflags", "stars") => "stars",
                    ("renderflags", "constellations") => "constellations",
                    ("renderflags", "galaxies" or "globulars") => "deep",
                    ("renderflags", "orbits") => "orbits",
                    ("renderflags", "cloudmaps") => "clouds",
                    ("renderflags", "atmospheres") => "atmospheres",
                    ("renderflags", "comettails") => "comettails",
                    ("renderflags", "boundaries") => "boundaries",
                    ("renderflags", "grid") => "grid",
                    ("renderflags", "eclipticgrid") => "eclipticgrid",
                    ("renderflags", "galacticgrid") => "galacticgrid",
                    ("renderflags", "horizontalgrid") => "horizongrid",
                    ("renderflags", "lightdelay") => "lighttime",
                    ("renderflags", "ecliptic") => "ecliptic",
                    ("renderflags", "markers") => "markers",
                    ("labels", "stars" or "planets" or "moons" or "dwarfplanets") => "labels",
                    ("labels", "minorplanets" or "asteroids" or "comets" or "spacecraft" or "minormoons") => "minorlabels",
                    ("labels", "locations") => "places",
                    ("labels", "openclusters") => "clusterlabels",
                    ("labels", "nebulae") => "nebulalabels",
                    _ => null
                };
                if (layer is not null)
                    lua.AppendLine($"st.sky({Quote(layer)}, {(enabled ? "true" : "false")})");
                else
                    unapplied.Add($"line {command.Line}: {command.Name} {flag}");
            }
        }

        string FlightTime(Command command) => Number(Duration(command.Number("time", 1)) * scale);
    }

    private static string MarkerColor(string value)
    {
        string[] channels = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return channels.Length == 3
            ? "#" + string.Concat(channels.Select(channel =>
                ((int)Math.Round(Math.Clamp(ParseNumber(channel), 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture)))
            : value;
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

    private static List<string> Tokenize(string input, out List<int> lines)
    {
        var words = new List<string>();
        lines = [];
        int offset = 0, line = 1;
        foreach (Match match in Tokens.Matches(input))
        {
            if (match.Index != offset)
                throw new InvalidDataException("The tour contains an invalid token.");
            offset += match.Length;
            if (!char.IsWhiteSpace(match.Value[0]) && match.Value[0] != '#')
            {
                words.Add(match.Value);
                lines.Add(line);
            }
            foreach (char character in match.ValueSpan)
                if (character == '\n')
                    line++;
        }
        if (offset != input.Length)
            throw new InvalidDataException("The tour contains a truncated token.");
        return words;
    }

    private static Command[] Parse(IReadOnlyList<string> words, IReadOnlyList<int> lines)
    {
        int at = 0;
        Expect("{");
        var commands = new List<Command>();
        while (Peek() != "}")
        {
            if (commands.Count == 256)
                throw new InvalidDataException("The tour contains too many commands.");
            int line = lines[at];
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
            commands.Add(new(command, line, fields));
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
