// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

/// <summary>Reads the sphere displacement model used by local irregular body shapes.</summary>
public static class DisplacedSphereDecoder
{
    private static readonly int[] Permutation = [
        151, 160, 137, 91, 90, 15, 131, 13, 201, 95, 96, 53, 194, 233, 7, 225, 140, 36, 103, 30, 69, 142, 8, 99, 37, 240, 21, 10, 23,
        190, 6, 148, 247, 120, 234, 75, 0, 26, 197, 62, 94, 252, 219, 203, 117, 35, 11, 32, 57, 177, 33, 88, 237, 149, 56, 87, 174, 20, 125, 136, 171, 168,
        68, 175, 74, 165, 71, 134, 139, 48, 27, 166, 77, 146, 158, 231, 83, 111, 229, 122, 60, 211, 133, 230, 220, 105, 92, 41, 55, 46, 245, 40, 244, 102,
        143, 54, 65, 25, 63, 161, 1, 216, 80, 73, 209, 76, 132, 187, 208, 89, 18, 169, 200, 196, 135, 130, 116, 188, 159, 86, 164, 100, 109, 198, 173,
        186, 3, 64, 52, 217, 226, 250, 124, 123, 5, 202, 38, 147, 118, 126, 255, 82, 85, 212, 207, 206, 59, 227, 47, 16, 58, 17, 182, 189, 28, 42, 223,
        183, 170, 213, 119, 248, 152, 2, 44, 154, 163, 70, 221, 153, 101, 155, 167, 43, 172, 9, 129, 22, 39, 253, 19, 98, 108, 110, 79, 113, 224, 232,
        178, 185, 112, 104, 218, 246, 97, 228, 251, 34, 242, 193, 238, 210, 144, 12, 191, 179, 162, 241, 81, 51, 145, 235, 249, 14, 239, 107, 49, 192,
        214, 31, 181, 199, 106, 157, 184, 84, 204, 176, 115, 121, 50, 45, 127, 4, 150, 254, 138, 236, 205, 93, 222, 114, 67, 29, 24, 72, 243, 141, 128,
        195, 78, 66, 215, 61, 156, 180
    ];

    public static DecodedScienceModel Decode(string path, CancellationToken cancellation = default) =>
        AddonDiagnostics.Read(AddonStage.Mesh, path, () => Read(path, cancellation));

    private static DecodedScienceModel Read(string path, CancellationToken cancellation)
    {
        using var stream = AddonDependencies.OpenRead(path, 16_384);
        if (stream.Length is 0 or > 16_384)
            throw new InvalidDataException("A displaced sphere definition exceeds 16 KiB or is empty.");
        using var input = new StreamReader(stream);
        string text = input.ReadToEnd();
        string[] words = Regex.Matches(text, @"#[^\r\n]*|[{}\[\]]|[^\s#{}\[\]]+")
            .Select(match => match.Value).Where(word => !word.StartsWith('#')).ToArray();
        int cursor = 0;
        Expect("SphereDisplacementMesh");
        Expect("{");
        Vector3d size = new(1, 1, 1), offset = new(10, 10, 10);
        double height = 0, octaves = 1;
        int slices = 20, rings = 20;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        for (string key = Next(); key != "}"; key = Next())
        {
            if (!fields.Add(key))
                throw new InvalidDataException("A displaced sphere field is repeated.");
            switch (key)
            {
                case "Size": size = Vector(); break;
                case "NoiseOffset": offset = Vector(); break;
                case "FeatureHeight": height = Number(); break;
                case "Octaves": octaves = Number(); break;
                case "Slices": slices = Integer(); break;
                case "Rings": rings = Integer(); break;
                default: throw new InvalidDataException("Unknown displaced sphere field " + key + ".");
            }
        }
        if (cursor != words.Length || slices is < 3 or > 2000 || rings is < 3 or > 1000 ||
            (rings - 1) * slices > 500_000 || octaves is < 0 or > 65_536 ||
            size.X < 1e-12 || size.Y < 1e-12 || size.Z < 1e-12)
            throw new InvalidDataException("Displaced sphere dimensions exceed the model limits.");
        return Build(size, offset, height, octaves, slices, rings, cancellation);

        string Next() => cursor < words.Length ? words[cursor++] : throw new InvalidDataException("Truncated displaced sphere.");
        void Expect(string value)
        {
            if (Next() != value)
                throw new InvalidDataException("Expected " + value + " in the displaced sphere.");
        }
        double Number() => double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            value is >= -1e6 and <= 1e6 ? value : throw new InvalidDataException("Invalid displaced sphere value.");
        int Integer() => int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : throw new InvalidDataException("Invalid displaced sphere subdivision.");
        Vector3d Vector()
        {
            Expect("[");
            var vector = new Vector3d(Number(), Number(), Number());
            Expect("]");
            return vector;
        }
    }

    private static DecodedScienceModel Build(Vector3d size, Vector3d offset, double height, double octaves,
        int slices, int rings, CancellationToken cancellation)
    {
        int columns = slices + 1;
        var points = new Vector3d[columns * rings];
        var smooth = new Vector3d[slices * rings];
        var fallback = new Vector3d[points.Length];
        Vector3d min = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity), max = min * -1;
        for (int row = 0; row < rings; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            var (sinLat, cosLat) = Math.SinCos((row / (double)(rings - 1) - .5) * Math.PI);
            for (int col = 0; col < columns; col++)
            {
                var (sinLon, cosLon) = Math.SinCos(col / (double)slices * Math.Tau);
                var unit = new Vector3d(cosLat * cosLon, sinLat, cosLat * sinLon);
                var normal = new Vector3d(unit.X / size.X, unit.Y / size.Y, unit.Z / size.Z);
                normal /= normal.Length;
                double variation = 0;
                for (double frequency = octaves; frequency >= 1; frequency *= .5)
                    variation += Noise((unit + offset) * frequency) / frequency;
                Vector3d p = new Vector3d(unit.X * size.X, unit.Y * size.Y, unit.Z * size.Z) + normal * (height * variation);
                p = new((float)p.X, (float)p.Y, (float)p.Z);
                int index = row * columns + col;
                points[index] = p;
                fallback[index] = normal;
                min = new(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }
        }

        int count = (rings - 1) * slices * 6;
        var vertices = new Vector3d[count];
        var normals = new Vector3d[count];
        var uv = new float[count * 2];
        var indices = new int[count];
        int at = 0;
        for (int row = 0; row < rings - 1; row++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int col = 0; col < slices; col++)
            {
                int a = row * columns + col, b = a + 1, c = a + columns, d = c + 1;
                Triangle(a, c, b);
                Triangle(b, c, d);
            }
        }
        for (int i = 0; i < count; i++)
        {
            int vertex = indices[i];
            int row = vertex / columns, col = vertex % columns;
            vertices[i] = points[vertex];
            Vector3d normal = smooth[Seam(vertex)];
            normals[i] = normal.Length > 0 ? normal / normal.Length : fallback[vertex];
            uv[i * 2] = 1 - (float)col / slices;
            uv[i * 2 + 1] = 1 - (float)row / (rings - 1);
        }
        return new([new(vertices, normals, uv, new(new(1, 1, 1), 1, null))],
            new Dictionary<string, ReadOnlyMemory<byte>>(), (max - min).Length);

        int Seam(int index)
        {
            int row = index / columns, col = index % columns;
            return row * slices + (row == 0 || row == rings - 1 || col == slices ? 0 : col);
        }

        void Triangle(int a, int b, int c)
        {
            Vector3d face = Vector3d.Cross(points[b] - points[a], points[c] - points[a]);
            smooth[Seam(a)] += face;
            smooth[Seam(b)] += face;
            smooth[Seam(c)] += face;
            // Godot's front face uses clockwise winding.
            indices[at++] = a;
            indices[at++] = c;
            indices[at++] = b;
        }
    }

    private static double Noise(Vector3d p)
    {
        double fx = Math.Floor(p.X), fy = Math.Floor(p.Y), fz = Math.Floor(p.Z);
        int ix = (int)(fx % 256) & 255, iy = (int)(fy % 256) & 255, iz = (int)(fz % 256) & 255;
        double x = p.X - fx, y = p.Y - fy, z = p.Z - fz;
        double u = Fade(x), v = Fade(y), w = Fade(z);
        int a = Perm(ix) + iy, b = Perm(ix + 1) + iy;
        int aa = Perm(a) + iz, ab = Perm(a + 1) + iz, ba = Perm(b) + iz, bb = Perm(b + 1) + iz;
        return Lerp(w,
            Lerp(v, Lerp(u, Gradient(aa, x, y, z), Gradient(ba, x - 1, y, z)),
                Lerp(u, Gradient(ab, x, y - 1, z), Gradient(bb, x - 1, y - 1, z))),
            Lerp(v, Lerp(u, Gradient(aa + 1, x, y, z - 1), Gradient(ba + 1, x - 1, y, z - 1)),
                Lerp(u, Gradient(ab + 1, x, y - 1, z - 1), Gradient(bb + 1, x - 1, y - 1, z - 1))));

        static int Perm(int index) => Permutation[index & 255];
        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
        static double Lerp(double t, double left, double right) => left + t * (right - left);
        static double Gradient(int index, double x, double y, double z)
        {
            int hash = Perm(index) & 15;
            double first = hash < 8 ? x : y;
            double second = hash < 4 ? y : hash is 12 or 14 ? x : z;
            return ((hash & 1) == 0 ? first : -first) + ((hash & 2) == 0 ? second : -second);
        }
    }
}
