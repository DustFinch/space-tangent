// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>First-order light time and annual aberration for a stationed observer.</summary>
public static class ApparentSky
{
    private const double LightSpeed = 299_792.458;

    public static Vector3d ObserverBeta(CartesianState body) => body.VelocityKilometersPerSecond / LightSpeed;

    public static Vector3d Aberrate(Vector3d direction, Vector3d beta)
    {
        if (beta == default || !(direction.Length > 0))
            return direction;
        var shifted = direction / direction.Length + beta;
        return shifted / shifted.Length;
    }

    public static Vector3d BodyOffset(CartesianState target, CartesianState observerBody, Vector3d siteOffset)
    {
        Vector3d offset = (target - observerBody).PositionKilometers - siteOffset;
        double lightTime = offset.Length / LightSpeed;
        if (!(lightTime > 0))
            return offset;
        var emissionOffset = offset - target.VelocityKilometersPerSecond * lightTime;
        return Aberrate(emissionOffset, ObserverBeta(observerBody)) * emissionOffset.Length;
    }
}
