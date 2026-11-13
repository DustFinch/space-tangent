// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;


namespace SpaceTangent.Application;

public sealed record MinorBodyEntry(string Designation, string Name, int Group, double Epoch, double MeanAtEpoch,
    double MeanPerDay, double Eccentricity, Vector3d MajorAxis, Vector3d MinorAxis, double AbsoluteMagnitude, double CometSlope)
{
    private const double Au = AstronomicalUnits.KilometersPerAstronomicalUnit;
    public Vector3d Position(JulianInstant epoch)
    {
        double mean = Math.IEEERemainder(MeanAtEpoch + MeanPerDay * (epoch.ToJulianDate() - Epoch), Math.Tau);
        double anomaly = Eccentricity < .8 ? mean : Math.CopySign(Math.PI, mean);
        for (int step = 0; step < 40; step++)
        {
            var (sin, cos) = Math.SinCos(anomaly);
            double delta = (anomaly - Eccentricity * sin - mean) / (1 - Eccentricity * cos);
            anomaly -= delta;
            if (Math.Abs(delta) < 1e-12)
                break;
        }
        var (sine, cosine) = Math.SinCos(anomaly);
        return MajorAxis * (cosine - Eccentricity) + MinorAxis * sine;
    }

    public double Magnitude(Vector3d place, Vector3d observer)
    {
        Vector3d towardEye = observer - place;
        double sun = place.Length / Au, distance = towardEye.Length / Au;
        if (!(sun > 0 && distance > 0))
            return double.PositiveInfinity;
        if (Group == 5)
            return AbsoluteMagnitude + 5 * Math.Log10(distance) + CometSlope * Math.Log10(sun);
        double phase = Math.Acos(Math.Clamp(-Vector3d.Dot(place, towardEye) / (sun * distance * Au * Au), -1, 1));
        double half = Math.Tan(Math.Min(phase, 3.1) / 2);
        double intensity = .85 * Math.Exp(-3.33 * Math.Pow(half, .63)) + .15 * Math.Exp(-1.87 * Math.Pow(half, 1.22));
        return AbsoluteMagnitude + 5 * Math.Log10(sun * distance) - 2.5 * Math.Log10(intensity);
    }
}

public static class MinorBodySwarm
{
    private const double Au = AstronomicalUnits.KilometersPerAstronomicalUnit;
    private const double Gaussian = .01720209895;
    private const double Radians = Math.PI / 180;
    private const double Obliquity = 23.4392911 * Radians;

    public static MinorBodyEntry[] Read(TextReader reader)
    {
        var entries = new List<MinorBodyEntry>();
        string[] classes = ["MB", "NE", "TJ", "CE", "TN", "CO"];
        bool header = false;
        int lineNumber = 0;
        var values = new double[9];
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (!header)
            {
                if (line != "class,designation,name,epoch,a,e,i,node,peri,M,H,K")
                    throw new InvalidDataException("Invalid small-body CSV header.");
                header = true;
                continue;
            }
            if (entries.Count == 250_000 || line.Length > 512)
                throw new InvalidDataException("The small-body catalogue exceeds its size limit.");
            string[] fields = line.Split(',');
            int group = Array.IndexOf(classes, fields[0]);
            if (fields.Length != 12 || group < 0 || fields[1].Length == 0)
                throw new InvalidDataException($"Invalid small-body CSV row {lineNumber}.");
            for (int i = 0; i < values.Length; i++)
                if (!double.TryParse(fields[i + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    !double.IsFinite(values[i]))
                    throw new InvalidDataException($"Invalid small-body number on row {lineNumber}.");
            double epoch = values[0], a = values[1], e = values[2];
            if (epoch is < 1_000_000 or > 4_000_000 || a is < .001 or > 1e6 || e is < 0 or >= 1)
                throw new InvalidDataException($"Unsupported small-body orbit on row {lineNumber}.");
            var (si, ci) = Math.SinCos(values[3] * Radians);
            var (sn, cn) = Math.SinCos(values[4] * Radians);
            var (sw, cw) = Math.SinCos(values[5] * Radians);
            var p = Equatorial(new(cn * cw - sn * sw * ci, sn * cw + cn * sw * ci, sw * si));
            var q = Equatorial(new(-cn * sw - sn * cw * ci, -sn * sw + cn * cw * ci, cw * si));
            entries.Add(new(fields[1], fields[2], group, epoch, values[6] * Radians, Gaussian / (a * Math.Sqrt(a)), e,
                p * (a * Au), q * (a * Au * Math.Sqrt(1 - e * e)), values[7], values[8]));
        }
        if (!header || entries.Count == 0)
            throw new InvalidDataException("The small-body catalogue is empty.");
        return entries.ToArray();
    }

    private static Vector3d Equatorial(Vector3d point)
    {
        var (sin, cos) = Math.SinCos(Obliquity);
        return new(point.X, point.Y * cos - point.Z * sin, point.Y * sin + point.Z * cos);
    }
}
