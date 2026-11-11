// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpaceTangent.Application;

public sealed record TourConversion(string Lua, int Commands, IReadOnlyList<string> Unapplied);

public static class TourConverter
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
            return ConvertRecursiveTour(input);
        if (!extension.Equals(".cel", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Choose a CEL or CELX tour.");
        Command[] commands = Parse(input);
        var unapplied = new SortedSet<string>(StringComparer.Ordinal);
        double wait = commands.Where(command => command.Name is "wait" or "orbit")
            .Sum(command => Duration(command.Number("duration")));
        double scale = wait > 110 ? 110 / wait : 1;
        if (scale < 1)
            unapplied.Add("original timing compressed to fit the Lua wait quota");
        var lua = new StringBuilder("""
            local selected = true
            local function orbit(yaw, pitch, duration)
                for step = 1, 8 do
                    st.turn(yaw / 8, pitch / 8)
                    st.wait(duration / 8)
                end
            end

            """);
        int converted = 0;
        foreach (Command command in commands)
        {
            switch (command.Name)
            {
                case "print":
                    lua.AppendLine($"st.log({Quote(command.Get("text"))})");
                    break;
                case "select":
                    lua.AppendLine($"selected = st.tryselect({Quote(command.Get("object"))})");
                    break;
                case "goto":
                    if (command.Fields.ContainsKey("upframe") || command.Fields.ContainsKey("up"))
                        unapplied.Add("goto up vector");
                    lua.AppendLine($"if selected then st.gotoobject({Number(Duration(command.Number("time", 1.65)) * scale)}, {Number(command.Number("distance", 4.2))}) end");
                    break;
                case "center":
                    lua.AppendLine($"if selected then st.center({Number(Duration(command.Number("time", 1.65)) * scale)}) end");
                    break;
                case "follow":
                    lua.AppendLine("if selected then st.follow() end");
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
                case "orbit":
                    if (!TryOrbit(command))
                    {
                        unapplied.Add("orbit axis");
                        continue;
                    }
                    break;
                default:
                    unapplied.Add(command.Name);
                    continue;
            }
            converted++;
        }
        foreach (string item in unapplied.Reverse())
            lua.Insert(0, "-- Unapplied: " + item + "\n");
        return new(lua.ToString(), converted, Array.AsReadOnly(unapplied.ToArray()));

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

        bool TryOrbit(Command command)
        {
            double[] axis = command.Get("axis").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ParseNumber).ToArray();
            if (axis.Length != 3 || axis[2] != 0 || Math.Abs(axis[0]) + Math.Abs(axis[1]) != 1)
                return false;
            double duration = Duration(command.Number("duration"));
            double angle = command.Number("rate") * duration * Math.PI / 180;
            lua.AppendLine($"if selected then orbit({Number(angle * axis[1])}, {Number(angle * axis[0])}, {Number(duration * scale)}) end");
            return true;
        }
    }

    private static TourConversion ConvertRecursiveTour(string source)
    {
        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        if (!hash.Equals(RecursiveTourSha256, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This CELX converter accepts the verified recursive Solar System tour.");
        const string lua = """
            local targets = st.cataloguetour()
            local flight = math.min(3, 96 / #targets)
            for _, target in ipairs(targets) do
                if st.tryselect(target) then
                    st.log(target)
                    st.gotoobject(flight, 4.2)
                    st.wait(flight + 0.05)
                    st.follow()
                    st.wait(0.35)
                end
            end
            """;
        return new(lua, 1, Array.AsReadOnly(new[] { "display banner replaced by a tour log entry" }));
    }

    private static Command[] Parse(string input)
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
                    value = JsonSerializer.Deserialize<string>(value) ?? "";
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
