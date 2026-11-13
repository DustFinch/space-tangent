// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

/// <summary>Legacy P03 long-period mean pole, expressed in J2000 equatorial axes.</summary>
public static class CelestialPoleOfDate
{
    private const double Arcseconds = Math.PI / (180 * 3600);
    private const double J2000Obliquity = 23.4392911 * Math.PI / 180;

    private static readonly double[][] Polynomial =
    [
        [5750.804069, 0.1948311, -0.00016739, -4.8e-8],
        [-1673.999018, 0.3474459, 0.00011243, -6.4e-8],
        [7907.295950, 5044.374034, -0.00713473, 6e-9],
        [83973.876448, -0.0425899, -0.00000113]
    ];
    private readonly record struct PeriodicRow(double CosFirst, double CosSecond, double SinFirst, double SinSecond, double PeriodCenturies);
    private static readonly PeriodicRow[] EclipticRows =
    [
        new(486.230527,2559.065245,-2578.462809,485.116645,2308.98),
        new(-963.825784,247.582718,-237.405076,-971.375498,1831.25),
        new(-1868.737098,-957.399054,1007.593090,-1930.464338,687.52),
        new(-1589.172175,493.021354,-423.035168,-1634.905683,729.97),
        new(429.442489,-328.301413,337.266785,429.594383,492.21),
        new(-2244.742029,-339.969833,221.240093,-2131.745072,708.13)
    ];
    private static readonly PeriodicRow[] PrecessionRows =
    [
        new(-6180.062400,807.904635,-2434.845716,-2056.455197,409.90),
        new(-2721.869299,-177.959383,538.034071,-912.727303,396.15),
        new(1460.746498,371.942696,-1245.689351,447.710000,536.91),
        new(-1838.488899,-176.029134,529.220775,-611.297411,402.90),
        new(949.518077,-89.154030,277.195375,315.900626,417.15),
        new(32.701460,-336.048179,945.979710,12.390157,288.92),
        new(598.054819,-17.415730,-955.163661,-15.922155,4042.97),
        new(-293.145284,-28.084479,93.894079,-102.870153,304.90),
        new(66.354942,21.456146,0.671968,24.123484,281.46),
        new(18.894136,30.917011,-184.663935,2.512708,204.38)
    ];
    public static Vector3d? Direction(JulianInstant epoch)
    {
        double t = (epoch.ToJulianDate() - JulianInstant.J2000Day) / 36525;
        if (epoch.Scale != TimeScale.Tdb || Math.Abs(t) > 800000d / 36525)
            return null;

        var (p, q) = Pair(EclipticRows, 0, t);
        var (precession, obliquity) = Pair(PrecessionRows, 2, t);
        p *= Arcseconds;
        q *= Arcseconds;
        var (sinPrecession, cosPrecession) = Math.SinCos(precession * Arcseconds);
        var (sinObliquity, cosObliquity) = Math.SinCos(obliquity * Arcseconds);
        var pole = new Vector3d(sinObliquity * sinPrecession, -sinObliquity * cosPrecession, -cosObliquity);
        double w = Math.Sqrt((1 + Math.Sqrt(1 - p * p - q * q)) / 2);
        var quaternionVector = new Vector3d(q, -p, 0) / (2 * w);
        var cross = Vector3d.Cross(quaternionVector, pole);
        pole += (cross * w + Vector3d.Cross(quaternionVector, cross)) * 2;
        var (sine, cosine) = Math.SinCos(J2000Obliquity);
        // Undo the native Y-up conjugation for the pole, then rotate the ecliptic into J2000.
        return new(pole.X, -pole.Y * cosine + pole.Z * sine, -pole.Y * sine - pole.Z * cosine);
    }

    private static (double First, double Second) Pair(PeriodicRow[] terms, int index, double t)
    {
        double first = EvaluatePolynomial(Polynomial[index], t);
        double second = EvaluatePolynomial(Polynomial[index + 1], t);
        foreach (var term in terms)
        {
            var (sine, cosine) = Math.SinCos(Math.Tau * t / term.PeriodCenturies);
            first += term.CosFirst * cosine + term.SinFirst * sine;
            second += term.CosSecond * cosine + term.SinSecond * sine;
        }
        return (first, second);
    }

    private static double EvaluatePolynomial(double[] coefficients, double t)
    {
        double result = coefficients[^1];
        for (int index = coefficients.Length - 2; index >= 0; index--)
            result = result * t + coefficients[index];
        return result;
    }
}
