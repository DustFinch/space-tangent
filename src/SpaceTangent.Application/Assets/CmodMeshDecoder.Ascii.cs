// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text;

namespace SpaceTangent.Application.Assets;

public static partial class CmodMeshDecoder
{
    private static MemoryStream ConvertAscii(Stream source, CancellationToken cancellation)
    {
        using var text = new StreamReader(source, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var tokens = new AsciiTokens(text);
        var converted = new MemoryStream();
        using var output = new BinaryWriter(converted, Encoding.UTF8, leaveOpen: true);
        try
        {
            int materials = 0;
            while (tokens.Next() is { } token)
            {
                cancellation.ThrowIfCancellationRequested();
                switch (token)
                {
                    case "material":
                        if (++materials > MaximumMaterials)
                            throw new InvalidDataException("CMOD contains too many materials.");
                        WriteMaterial();
                        break;
                    case "mesh":
                        WriteMesh();
                        break;
                    default:
                        throw new InvalidDataException($"Unknown ASCII CMOD block '{token}'.");
                }
            }
            converted.Position = 0;
            return converted;
        }
        catch
        {
            converted.Dispose();
            throw;
        }

        void WriteMaterial()
        {
            output.Write((short)1001);
            for (string key = tokens.Required(); key != "end_material"; key = tokens.Required())
            {
                CheckBudget();
                switch (key)
                {
                    case "diffuse" or "specular" or "emissive":
                        output.Write((short)(key == "diffuse" ? 1003 : key == "specular" ? 1004 : 1014));
                        output.Write((short)7);
                        for (int component = 0; component < 3; component++)
                            output.Write(tokens.Float());
                        break;
                    case "opacity" or "specpower":
                        output.Write((short)(key == "opacity" ? 1006 : 1005));
                        output.Write((short)1);
                        output.Write(tokens.Float());
                        break;
                    case "blend":
                        tokens.Required();
                        break;
                    case "texture0" or "normalmap" or "specularmap" or "emissivemap":
                        byte[] name = Encoding.UTF8.GetBytes(tokens.Required());
                        if (name.Length is 0 or > 1024)
                            throw new InvalidDataException("Invalid CMOD texture name.");
                        output.Write((short)1007);
                        output.Write((short)(key switch { "texture0" => 0, "normalmap" => 1, "specularmap" => 2, _ => 3 }));
                        output.Write((short)5);
                        output.Write((ushort)name.Length);
                        output.Write(name);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown ASCII CMOD material field '{key}'.");
                }
            }
            output.Write((short)1002);
        }

        void WriteMesh()
        {
            output.Write((short)1009);
            tokens.Expect("vertexdesc");
            output.Write((short)1011);
            var formats = new List<short>();
            for (string key = tokens.Required(); key != "end_vertexdesc"; key = tokens.Required())
            {
                short semantic = key switch
                {
                    "position" => 0, "color0" => 1, "color1" => 2, "normal" => 3, "tangent" => 4,
                    "texcoord0" => 5, "texcoord1" => 6, "texcoord2" => 7, "texcoord3" => 8,
                    "pointsize" => 9, "nextposition" => 10, "scalefactor" => 11,
                    _ => throw new InvalidDataException($"Unknown ASCII CMOD vertex attribute '{key}'.")
                };
                short format = tokens.Required() switch
                {
                    "f1" => 0, "f2" => 1, "f3" => 2, "f4" => 3, "ub4" => 4,
                    _ => throw new InvalidDataException("Unknown ASCII CMOD vertex format.")
                };
                if (formats.Count >= 12)
                    throw new InvalidDataException("CMOD contains too many vertex attributes.");
                formats.Add(format);
                output.Write(semantic);
                output.Write(format);
            }
            output.Write((short)1012);
            tokens.Expect("vertices");
            output.Write((short)1013);
            int vertices = tokens.Integer(1, MaximumVertices);
            output.Write((uint)vertices);
            for (int vertex = 0; vertex < vertices; vertex++)
            {
                if ((vertex & 4095) == 0)
                    CheckBudget();
                foreach (short format in formats)
                {
                    int components = format == 4 ? 4 : format + 1;
                    for (int component = 0; component < components; component++)
                        if (format == 4)
                            output.Write((byte)tokens.Integer(0, 255));
                        else
                            output.Write(tokens.Float());
                }
            }
            for (string primitive = tokens.Required(); primitive != "end_mesh"; primitive = tokens.Required())
            {
                CheckBudget();
                output.Write((short)(primitive switch
                {
                    "trilist" => 0, "tristrip" => 1, "trifan" => 2, "linelist" => 3,
                    "linestrip" => 4, "points" => 5, "sprites" => 6,
                    _ => throw new InvalidDataException($"Unknown ASCII CMOD primitive '{primitive}'.")
                }));
                output.Write(unchecked((uint)tokens.Integer(-1, MaximumMaterials)));
                int count = tokens.Integer(0, 3 * MaximumTriangles);
                output.Write((uint)count);
                for (int index = 0; index < count; index++)
                {
                    if ((index & 8191) == 0)
                        CheckBudget();
                    output.Write((uint)tokens.Integer(0, vertices - 1));
                }
            }
            output.Write((short)1010);
            CheckBudget();
        }

        void CheckBudget()
        {
            cancellation.ThrowIfCancellationRequested();
            if (converted.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("Decoded ASCII CMOD exceeds 64 MiB.");
        }
    }

    private sealed class AsciiTokens(TextReader input)
    {
        private readonly StringBuilder _token = new(32);
        internal string? Next()
        {
            int next;
            while ((next = input.Read()) >= 0)
            {
                if (char.IsWhiteSpace((char)next))
                    continue;
                if (next == '#')
                {
                    while ((next = input.Read()) is >= 0 and not ('\n' or '\r')) { }
                    continue;
                }
                _token.Clear();
                if (next == '"')
                {
                    while ((next = input.Read()) is >= 0 and not ('"' or '\n' or '\r'))
                        Append(next);
                    if (next != '"')
                        throw new InvalidDataException("Unterminated ASCII CMOD string.");
                }
                else
                {
                    Append(next);
                    while (input.Peek() is int peek && peek >= 0 && !char.IsWhiteSpace((char)peek) && peek != '#')
                        Append(input.Read());
                }
                return _token.ToString();
            }
            return null;
        }

        internal string Required() => Next() ?? throw new InvalidDataException("ASCII CMOD ends early.");
        internal void Expect(string token)
        {
            if (Required() != token)
                throw new InvalidDataException($"Expected ASCII CMOD field '{token}'.");
        }

        internal int Integer(int minimum, int maximum) =>
            int.TryParse(Required(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) &&
                value >= minimum && value <= maximum ? value : throw new InvalidDataException("Invalid ASCII CMOD integer.");

        internal float Float() =>
            float.TryParse(Required(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ?
                value : throw new InvalidDataException("Invalid ASCII CMOD coordinate or material value.");

        private void Append(int value)
        {
            if (_token.Length >= 1024)
                throw new InvalidDataException("ASCII CMOD token exceeds 1024 characters.");
            _token.Append((char)value);
        }
    }
}
