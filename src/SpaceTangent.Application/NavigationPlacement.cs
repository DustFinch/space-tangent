// SPDX-License-Identifier: GPL-2.0-or-later
using System.Numerics;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public enum NavigationFrame { Inertial, Chase, PhaseLock }

/// <summary>Eye offset and orthonormal attitude, in ICRS axes or the tracked body's local axes.</summary>
public readonly record struct NavigationPlacement(Vector3d OffsetKilometers, Vector3d Forward, Vector3d Up,
    bool BodyFixed = false)
{
    public NavigationFrame Frame { get; init; }
    public ObjectId? Reference { get; init; }

    internal bool UsesFrame => BodyFixed || Frame != NavigationFrame.Inertial;

    public bool IsValid(double range) => Enum.IsDefined(Frame) &&
        (Frame == NavigationFrame.Inertial ? Reference is null : Reference.HasValue && !BodyFixed) &&
        OffsetKilometers.IsFinite && OffsetKilometers.Length <= range * 1.001 &&
        Forward.IsFinite && Up.IsFinite && Math.Abs(Forward.Length - 1) < .000001 &&
        Math.Abs(Up.Length - 1) < .000001 && Math.Abs(Vector3d.Dot(Forward, Up)) < .000001;

    internal NavigationPlacement Transform(Basis3d basis, bool bodyFixed) =>
        new(basis.Transform(OffsetKilometers), basis.Transform(Forward), basis.Transform(Up), bodyFixed);

    internal NavigationPlacement Turn(double yaw, double pitch, bool orbit)
    {
        Vector3d forward = Rotate(Forward, Up, yaw);
        Vector3d right = Vector3d.Cross(forward, Up);
        return this with
        {
            OffsetKilometers = orbit ? Rotate(Rotate(OffsetKilometers, Up, yaw), right, pitch) : OffsetKilometers,
            Forward = Rotate(forward, right, pitch),
            Up = Rotate(Up, right, pitch)
        };
    }

    internal NavigationPlacement Interpolate(NavigationPlacement end, double fraction)
    {
        if (fraction == 0)
            return this;
        var rotation = Quaternion.Slerp(ToQuaternion(), end.ToQuaternion(), (float)fraction);
        Vector3d forward = Direction(Vector3.Transform(-Vector3.UnitZ, rotation));
        Vector3d up = Direction(Vector3.Transform(Vector3.UnitY, rotation));
        forward /= forward.Length;
        up -= forward * Vector3d.Dot(up, forward);
        return new(OffsetKilometers + (end.OffsetKilometers - OffsetKilometers) * fraction, forward, up / up.Length);

        static Vector3d Direction(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
    }

    internal Quaternion ToQuaternion()
    {
        Vector3d right = Vector3d.Cross(Forward, Up);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            (float)right.X, (float)right.Y, (float)right.Z, 0,
            (float)Up.X, (float)Up.Y, (float)Up.Z, 0,
            (float)-Forward.X, (float)-Forward.Y, (float)-Forward.Z, 0,
            0, 0, 0, 1)));
    }

    private static Vector3d Rotate(Vector3d direction, Vector3d axis, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        return direction * cos + Vector3d.Cross(axis, direction) * sin + axis * (Vector3d.Dot(axis, direction) * (1 - cos));
    }
}
