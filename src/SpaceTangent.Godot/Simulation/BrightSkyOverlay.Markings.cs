// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal partial class BrightSkyOverlay
{
    private static readonly Color GalacticMark = new(.95f, .6f, .9f, .5f);
    private static readonly Color HorizonMark = new(.9f, .9f, .55f, .55f);
    private static readonly Color PrecessionMark = new(.85f, .7f, .5f, .45f);
    private static readonly Color PolarMark = new(.45f, .75f, 1, .55f);
    private static readonly Color PointMark = new(.95f, .95f, 1, .9f);
    private static readonly (float Sine, float Cosine)[] CircleSteps = Enumerable.Range(0, 240)
        .Select(index => MathF.SinCos(MathF.Tau * index / 240)).ToArray();
    private readonly (Vector3 From, Vector3 To)[] _markSegments = new (Vector3, Vector3)[CircleSteps.Length];

    private void DrawSkyMarkings()
    {
        SkyMarkings marks = _frame.Layers.Markings;
        if (marks == SkyMarkings.None)
            return;

        const float rightAngle = MathF.PI / 2;
        if (marks.HasFlag(SkyMarkings.GalacticEquator))
            MarkCircle(GalacticDirection(0, 90), rightAngle, GalacticMark);
        if (marks.HasFlag(SkyMarkings.PrecessionCircles))
        {
            const float obliquity = 23.4392911f * MathF.PI / 180;
            Vector3 eclipticPole = EclipticDirection(0, 90);
            MarkCircle(eclipticPole, obliquity, PrecessionMark);
            MarkCircle(-eclipticPole, obliquity, PrecessionMark);
        }
        if (_frame.SiteAxes is not { } axes)
            return;
        Vector3 zenith = Convert(axes.Z);
        if (marks.HasFlag(SkyMarkings.Meridian))
            MarkCircle(Convert(axes.X), rightAngle, HorizonMark);
        if (marks.HasFlag(SkyMarkings.PrimeVertical))
            MarkCircle(Convert(axes.Y), rightAngle, HorizonMark);
        if (marks.HasFlag(SkyMarkings.CircumpolarCircles))
        {
            float latitude = MathF.Asin(Math.Clamp(zenith.Z, -1, 1));
            if (Math.Abs(latitude) > .001f)
            {
                Vector3 pole = Vector3.Back * Math.Sign(latitude);
                MarkCircle(pole, Math.Abs(latitude), PolarMark);
                MarkCircle(-pole, Math.Abs(latitude), PolarMark with { A = .3f });
            }
        }
        if (marks.HasFlag(SkyMarkings.ZenithAndNadir))
        {
            MarkPoint(zenith, "Zenith");
            MarkPoint(-zenith, "Nadir");
        }
    }

    private void MarkCircle(Vector3 axis, float radius, Color color)
    {
        axis = axis.Normalized();
        Vector3 u = axis.Cross(Math.Abs(axis.X) < .9f ? Vector3.Right : Vector3.Up).Normalized();
        Vector3 v = axis.Cross(u);
        var (sine, cosine) = MathF.SinCos(radius);
        Vector3 previous = At(CircleSteps[^1]);
        for (int index = 0; index < CircleSteps.Length; index++)
        {
            Vector3 current = At(CircleSteps[index]);
            _markSegments[index] = (previous, current);
            previous = current;
        }
        DrawSegments(_markSegments, color, clipHorizon: false);

        Vector3 At((float Sine, float Cosine) step) =>
            axis * cosine + (u * step.Cosine + v * step.Sine) * sine;
    }

    private void MarkPoint(Vector3 direction, string name)
    {
        if (!Project(direction, out Vector2 at, clipHorizon: false) || !new Rect2(Vector2.Zero, Size).HasPoint(at))
            return;
        DrawCircle(at, 2.5f, PointMark, antialiased: true);
        DrawString(ThemeDB.FallbackFont, at + new Vector2(6, -4), name, fontSize: 13, modulate: PointMark);
    }
}
