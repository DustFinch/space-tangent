// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using System.Text.Json;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application.Assets;

public static partial class PackedModelDecoder
{
    private readonly record struct ModelAccessor(ReadOnlyMemory<byte> Data, int Offset, int Count, int Stride);

    private static IEnumerable<(int Mesh, Matrix4x4 Transform)> SceneMeshes(JsonElement root, CancellationToken cancellation)
    {
        JsonElement nodes = root.GetProperty("nodes");
        int count = nodes.GetArrayLength();
        if (count > 65_536)
            throw new InvalidDataException("glTF node budget exceeded.");
        int[] roots;
        if (root.TryGetProperty("scenes", out var scenes))
        {
            var scene = scenes[Int(root, "scene", 0)];
            roots = Children(scene, "nodes");
        }
        else
        {
            var children = new HashSet<int>();
            foreach (var node in nodes.EnumerateArray())
                children.UnionWith(Children(node, "children"));
            roots = Enumerable.Range(0, count).Where(index => !children.Contains(index)).ToArray();
        }
        var seen = new HashSet<int>();
        foreach (int index in roots)
        foreach (var mesh in Visit(index, Matrix4x4.Identity, 0))
            yield return mesh;

        IEnumerable<(int Mesh, Matrix4x4 Transform)> Visit(int index, Matrix4x4 parent, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 64 || index < 0 || index >= count || !seen.Add(index))
                throw new InvalidDataException("glTF scene has an invalid, repeated or cyclic node.");
            JsonElement node = nodes[index];
            Matrix4x4 transform = LocalTransform(node) * parent;
            if (node.TryGetProperty("mesh", out var mesh))
                yield return (mesh.GetInt32(), transform);
            foreach (int child in Children(node, "children"))
            foreach (var item in Visit(child, transform, depth + 1))
                yield return item;
        }

        static int[] Children(JsonElement item, string property) => item.TryGetProperty(property, out var children)
            ? children.EnumerateArray().Select(child => child.GetInt32()).ToArray() : [];
    }

    private static Matrix4x4 LocalTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var m = Values(matrix, 16);
            if (m[3] != 0 || m[7] != 0 || m[11] != 0 || m[15] != 1)
                throw new InvalidDataException("A glTF node matrix must be affine.");
            return new(m[0], m[1], m[2], 0, m[4], m[5], m[6], 0, m[8], m[9], m[10], 0, m[12], m[13], m[14], 1);
        }
        var t = node.TryGetProperty("translation", out var translation) ? Values(translation, 3) : [0, 0, 0];
        var s = node.TryGetProperty("scale", out var scale) ? Values(scale, 3) : [1, 1, 1];
        var r = node.TryGetProperty("rotation", out var rotation) ? Values(rotation, 4) : [0, 0, 0, 1];
        var q = new Quaternion(r[0], r[1], r[2], r[3]);
        if (Math.Abs(q.LengthSquared() - 1) > .001f)
            throw new InvalidDataException("A glTF rotation must be a unit quaternion.");
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) *
            Matrix4x4.CreateTranslation(t[0], t[1], t[2]);

        static float[] Values(JsonElement item, int length)
        {
            if (item.GetArrayLength() != length)
                throw new InvalidDataException("glTF node transform has the wrong component count.");
            float[] values = item.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            if (values.Any(value => !float.IsFinite(value)))
                throw new InvalidDataException("A glTF transform is not finite.");
            return values;
        }
    }

    private static Vector3d Checked(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z)
            ? new(vector.X, vector.Y, vector.Z)
            : throw new InvalidDataException("A transformed glTF vertex or normal is not finite.");

    private static string ImageExtension(JsonElement image, string? uri)
    {
        string? mime = image.TryGetProperty("mimeType", out var value) ? value.GetString() : null;
        if (mime is null && uri is not null && uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int end = uri.IndexOf(';');
            if (end > 5)
                mime = uri[5..end];
        }
        return mime switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/vnd-ms.dds" => ".dds",
            null when uri is not null && Path.GetExtension(uri).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".dds"
                => Path.GetExtension(uri).ToLowerInvariant(),
            _ => throw new NotSupportedException("glTF textures require PNG, JPEG or supported DDS data.")
        };
    }

    private static byte[] ReadUri(string uri, string root, string path, int limit)
    {
        if (string.IsNullOrEmpty(uri))
            throw new InvalidDataException("Missing glTF resource URI.");
        if (uri.StartsWith("data:", StringComparison.Ordinal))
        {
            int comma = uri.IndexOf(',');
            if (comma <= 5 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.Ordinal) ||
                uri.Length - comma - 1 > (limit + 2L) / 3 * 4)
                throw new InvalidDataException("Unsupported glTF data URI or resource budget exceeded.");
            byte[] data;
            try { data = Convert.FromBase64String(uri[(comma + 1)..]); }
            catch (FormatException error) { throw new InvalidDataException("Malformed glTF data URI.", error); }
            return data.Length <= limit ? data : throw new InvalidDataException("glTF resource budget exceeded.");
        }

        string relative = Uri.UnescapeDataString(uri);
        if (relative.Length == 0 || relative.Any(char.IsControl) || relative.IndexOfAny([':', '?', '#', '\\']) >= 0 ||
            Path.IsPathRooted(relative))
            throw new InvalidDataException("glTF resources must use local relative URIs.");
        string fullRoot = Path.GetFullPath(root);
        string resource = Path.GetFullPath(relative, Path.GetDirectoryName(Path.GetFullPath(path))!);
        string withinRoot = Path.GetRelativePath(fullRoot, resource);
        if (Path.IsPathRooted(withinRoot) || withinRoot is "." or ".." ||
            withinRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("glTF resource is outside its add-on root.");
        for (string cursor = resource; ; cursor = Path.GetDirectoryName(cursor)!)
        {
            if ((AddonDependencies.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("glTF resources must not use symbolic links.");
            if (Path.GetRelativePath(fullRoot, cursor) == ".")
                break;
        }
        using FileStream stream = AddonDependencies.OpenRead(resource, limit);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
