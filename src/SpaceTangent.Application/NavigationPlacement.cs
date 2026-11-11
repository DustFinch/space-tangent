// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>Eye offset and orthonormal attitude, in ICRS axes or the tracked body's local axes.</summary>
public readonly record struct NavigationPlacement(Vector3d OffsetKilometers, Vector3d Forward, Vector3d Up,
    bool BodyFixed = false)
{
    public bool IsValid(double range) => OffsetKilometers.IsFinite && OffsetKilometers.Length <= range * 1.001 &&
        Forward.IsFinite && Up.IsFinite && Math.Abs(Forward.Length - 1) < .000001 &&
        Math.Abs(Up.Length - 1) < .000001 && Math.Abs(Vector3d.Dot(Forward, Up)) < .000001;

    internal NavigationPlacement Transform(Basis3d basis, bool bodyFixed) =>
        new(basis.Transform(OffsetKilometers), basis.Transform(Forward), basis.Transform(Up), bodyFixed);

    internal NavigationPlacement Turn(double yaw, double pitch)
    {
        Vector3d forward = Rotate(Forward, Up, yaw);
        Vector3d right = Vector3d.Cross(forward, Up);
        return this with { Forward = Rotate(forward, right, pitch), Up = Rotate(Up, right, pitch) };
    }

    private static Vector3d Rotate(Vector3d direction, Vector3d axis, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return direction * cos + Vector3d.Cross(axis, direction) * sin + axis * (Vector3d.Dot(axis, direction) * (1 - cos));
    }
}
