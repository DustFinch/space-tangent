// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Tests;

// Double-precision midpoint quadrature on both the view ray and the ray to the Sun.
// The production shader uses a Chapman approximation for the latter.
internal static class EarthSkyReference
{
    private static readonly Vector3d Rayleigh = new(.004847, .01149, .02870);
    private static readonly Vector3d Absorption = new(.00407, .00254, .00001);
    private const double Ground = EarthSurfaceLighting.EarthRadiusKilometers, Top = Ground + 100;
    private const double Mie = .01, Anisotropy = .76, Exposure = 7;

    internal static Vector3d Pixel(SurfaceLighting lighting, int width, int height, int x, int y, double pitchDegrees)
    {
        const double Degree = Math.PI / 180;
        const double Heading = 237 * Degree;
        double pitch = pitchDegrees * Degree;
        Basis3d enu = lighting.EastNorthUp;
        Vector3d horizontal = enu.Y * Math.Cos(Heading) + enu.X * Math.Sin(Heading);
        Vector3d forward = horizontal * Math.Cos(pitch) + enu.Z * Math.Sin(pitch);
        Vector3d right = Unit(Vector3d.Cross(forward, enu.Z));
        Vector3d up = Vector3d.Cross(right, forward);
        double screenX = (2 * (x + .5) / width - 1) * width / height;
        double screenY = 1 - 2 * (y + .5) / height;
        Vector3d ray = Unit(forward + (right * screenX + up * screenY) * Math.Tan(5 * Degree));
        Vector3d observer = lighting.PositionKilometers;
        Vector3d sun = Unit(lighting.EarthToSun - observer);
        double cosine = Vector3d.Dot(ray, sun);
        double rayleighPhase = (3d / 16 / Math.PI) * (1 + cosine * cosine);
        double miePhase = (1 - Anisotropy * Anisotropy) /
            (4 * Math.PI * Math.Pow(1 + Anisotropy * Anisotropy - 2 * Anisotropy * cosine, 1.5));

        double distance = Exit(observer, ray);
        double previous = 0, columnR = 0, columnM = 0;
        Vector3d radiance = default;
        const int Steps = 384;
        for (int i = 1; i <= Steps; i++)
        {
            double next = distance * i * i / (Steps * Steps);
            double segment = next - previous;
            Vector3d sample = observer + ray * ((previous + next) / 2);
            double radius = sample.Length;
            var (densityR, densityM) = Density(radius);
            double mu = Vector3d.Dot(sample, sun) / radius;
            double horizon = -Math.Sqrt(Math.Max(0, 1 - Ground * Ground / (radius * radius)));
            double t = Math.Clamp((mu - horizon + .00465) / .0093, 0, 1);
            double visibility = t * t * (3 - 2 * t);
            if (visibility > 0)
            {
                var (solarR, solarM) = SolarColumn(sample, sun);
                Vector3d transmission = Transmission(solarR + columnR + densityR * segment / 2,
                    solarM + columnM + densityM * segment / 2);
                double haze = Mie * densityM * miePhase;
                Vector3d local = Rayleigh * (densityR * rayleighPhase) + new Vector3d(haze, haze, haze);
                radiance += Multiply(transmission, local) * (segment * visibility);
            }
            columnR += densityR * segment;
            columnM += densityM * segment;
            previous = next;
        }
        var background = new Vector3d(.012 / 12.92, .018 / 12.92, .035 / 12.92);
        Vector3d sky = new(1 - Math.Exp(-Exposure * radiance.X), 1 - Math.Exp(-Exposure * radiance.Y),
            1 - Math.Exp(-Exposure * radiance.Z));
        Vector3d color = sky + Multiply(Transmission(columnR, columnM), background);
        return new(Encode(color.X), Encode(color.Y), Encode(color.Z));
    }

    private static (double Rayleigh, double Mie) SolarColumn(Vector3d from, Vector3d direction)
    {
        const int Steps = 384;
        double step = Exit(from, direction) / Steps;
        double rayleigh = 0, mie = 0;
        for (int i = 0; i < Steps; i++)
        {
            var (r, m) = Density((from + direction * ((i + .5) * step)).Length);
            rayleigh += r;
            mie += m;
        }
        return (rayleigh * step, mie * step);
    }

    private static double Exit(Vector3d point, Vector3d direction)
    {
        double along = Vector3d.Dot(point, direction);
        return Math.Max(0, Math.Sqrt(Math.Max(0, Top * Top - Vector3d.Dot(point, point) + along * along)) - along);
    }

    private static (double Rayleigh, double Mie) Density(double radius)
    {
        double height = Math.Max(0, radius - Ground);
        return (Math.Exp(-height / 8.5), Math.Exp(-height / 1.2));
    }

    private static Vector3d Transmission(double rayleigh, double mie)
    {
        Vector3d optical = (Rayleigh + Absorption) * rayleigh + new Vector3d(Mie, Mie, Mie) * mie;
        return new(Math.Exp(-optical.X), Math.Exp(-optical.Y), Math.Exp(-optical.Z));
    }

    private static Vector3d Unit(Vector3d vector) => vector / vector.Length;
    private static Vector3d Multiply(Vector3d a, Vector3d b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    private static double Encode(double linear) => linear <= .0031308
        ? 12.92 * linear : 1.055 * Math.Pow(linear, 1d / 2.4) - .055;
}
