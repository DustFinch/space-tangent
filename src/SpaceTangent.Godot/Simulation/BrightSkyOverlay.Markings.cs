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
        Vector3? pole = CelestialPoleOfDate.Direction(_frame.Epoch) is { } datePole ? Convert(datePole) : null;
        DrawDateMarkings(marks, pole);
        DrawCatalogueMarkings(marks);
        if (_frame.BodyPath is { } path)
            DrawBodyPath(path.FirstDay, path.Directions);
        if (_frame.SiteAxes is not { } axes)
            return;
        Vector3 zenith = Convert(axes.Z);
        if (marks.HasFlag(SkyMarkings.Meridian))
            MarkCircle(Convert(axes.X), rightAngle, HorizonMark);
        if (marks.HasFlag(SkyMarkings.PrimeVertical))
            MarkCircle(Convert(axes.Y), rightAngle, HorizonMark);
        if (marks.HasFlag(SkyMarkings.CircumpolarCircles) && pole is { } polarAxis)
        {
            float latitude = MathF.Asin(Math.Clamp(zenith.Dot(polarAxis), -1, 1));
            if (Math.Abs(latitude) > .001f)
            {
                Vector3 visiblePole = polarAxis * Math.Sign(latitude);
                MarkCircle(visiblePole, Math.Abs(latitude), PolarMark);
                MarkCircle(-visiblePole, Math.Abs(latitude), PolarMark with { A = .3f });
            }
        }
        if (marks.HasFlag(SkyMarkings.ZenithAndNadir))
        {
            MarkPoint(zenith, "Zenith");
            MarkPoint(-zenith, "Nadir");
        }
    }

    private void DrawBodyPath(double firstDay, Vector3?[] directions)
    {
        Color color = new(1, .8f, .45f, .8f);
        var visible = new Rect2(Vector2.Zero, Size).Grow(24);
        Vector2? previous = null;
        for (int day = 0; day < directions.Length; day++)
        {
            if (directions[day] is not { } direction || !Project(direction, out var point, clipHorizon: false))
            {
                previous = null;
                continue;
            }
            if (previous is { } start && (point - start).LengthSquared() < 4e6f &&
                new Rect2(start, Vector2.Zero).Expand(point).Grow(1).Intersects(visible))
                DrawLine(start, point, color, 1.5f, true);
            previous = point;
            if (day % 10 != 0 || !visible.HasPoint(point))
                continue;
            DrawCircle(point, day == 60 ? 3.5f : 2, color, antialiased: true);
            if (day % 30 == 0 && firstDay + day - 2440587.5 is >= -719162 and <= 2932896)
            {
                var date = DateTime.UnixEpoch.AddDays(firstDay + day - 2440587.5);
                DrawString(ThemeDB.FallbackFont, point + new Vector2(6, 14),
                    date.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture), fontSize: 11, modulate: color);
            }
        }
    }

    private void DrawCatalogueMarkings(SkyMarkings marks)
    {
        if (marks.HasFlag(SkyMarkings.NavigationalStars))
            DrawNavigationalStars();
        if (marks.HasFlag(SkyMarkings.Pulsars))
            DrawMarkers(_data.Pulsars, pulsars: true);
        if (marks.HasFlag(SkyMarkings.Quasars))
            DrawMarkers(_data.Quasars, pulsars: false);
    }

    private void DrawNavigationalStars()
    {
        Color color = new(.75f, .9f, 1, .9f);
        foreach (uint hip in SkyMarkerCatalogue.NavigationalStars)
        {
            if (CultureDirection(hip) is not { } direction || !Project(direction, out Vector2 at) ||
                !new Rect2(Vector2.Zero, Size).Grow(8).HasPoint(at))
                continue;
            DrawPolyline([at + new Vector2(0, -6), at + new Vector2(6, 0), at + new Vector2(0, 6),
                at + new Vector2(-6, 0), at + new Vector2(0, -6)], color, 1.2f, true);
            var id = new SpaceTangent.Contracts.ObjectId(FormattableString.Invariant($"hip/{hip}"));
            string? name = _cultureWorld?.Catalogue.Find(id)?.Name ?? _data.Catalogue.Find(id)?.Name;
            if (string.IsNullOrWhiteSpace(name))
                name = FormattableString.Invariant($"HIP {hip}");
            DrawString(ThemeDB.FallbackFont, at + new Vector2(8, 12), name, fontSize: 12, modulate: color);
        }
    }

    private void DrawMarkers(SkyCatalogueMarker[] entries, bool pulsars)
    {
        double field = 360 / Math.PI * Math.Atan(_frame.TangentHalfField);
        double faintest = 12 + 5 * Math.Log10(Math.Max(50 / Math.Max(field, .001), 1));
        Color color = pulsars ? new(.45f, 1, .6f, .85f) : new(1, .65f, .35f, .85f);
        var rect = new Rect2(Vector2.Zero, Size).Grow(20);
        foreach (var marker in entries)
        {
            if ((!pulsars && !(marker.Magnitude <= faintest)) || !Project(marker.Direction, out Vector2 at) ||
                !rect.HasPoint(at))
                continue;
            if (pulsars)
                DrawArc(at, 3.5f, 0, Mathf.Tau, 12, color, 1.2f, true);
            else
                DrawCircle(at, 2, color, antialiased: true);
            if (field < 10)
                DrawString(ThemeDB.FallbackFont, at + new Vector2(6, -4), marker.Name, fontSize: 11, modulate: color);
        }
    }

    private void DrawDateMarkings(SkyMarkings marks, Vector3? pole)
    {
        const float rightAngle = MathF.PI / 2;
        Vector3 ecliptic = EclipticDirection(0, 90), galactic = GalacticDirection(0, 90);
        if (marks.HasFlag(SkyMarkings.Poles))
        {
            MarkPoint(ecliptic, "North ecliptic pole");
            MarkPoint(-ecliptic, "South ecliptic pole");
            MarkPoint(galactic, "North galactic pole");
            MarkPoint(-galactic, "South galactic pole");
        }
        if (pole is not { } axis)
            return;
        if (marks.HasFlag(SkyMarkings.Poles))
        {
            MarkPoint(axis, "North celestial pole");
            MarkPoint(-axis, "South celestial pole");
        }
        if (marks.HasFlag(SkyMarkings.EquatorOfDate))
            MarkCircle(axis, rightAngle, PolarMark);
        Vector3 equinox = axis.Cross(ecliptic).Normalized();
        Vector3 solstice = ecliptic.Cross(equinox).Normalized();
        if (marks.HasFlag(SkyMarkings.Colures))
        {
            Color color = new(.6f, .9f, .7f, .45f);
            MarkCircle(axis.Cross(equinox), rightAngle, color);
            MarkCircle(axis.Cross(solstice), rightAngle, color);
        }
        if (marks.HasFlag(SkyMarkings.EquinoxesAndSolstices))
        {
            MarkPoint(equinox, "March equinox");
            MarkPoint(-equinox, "September equinox");
            MarkPoint(solstice, "June solstice");
            MarkPoint(-solstice, "December solstice");
        }
        if (marks.HasFlag(SkyMarkings.ArchaeoLines))
        {
            const float obliquity = 23.4392911f, lunarInclination = 5.145f;
            DrawPair(obliquity, new(1, .85f, .4f, .5f));
            DrawPair(16.4f, new(.8f, .8f, .8f, .35f));
            DrawPair(obliquity + lunarInclination, new(.6f, .7f, 1, .45f));
            DrawPair(obliquity - lunarInclination, new(.6f, .7f, 1, .45f));
        }

        void DrawPair(float declination, Color color)
        {
            float radius = Mathf.DegToRad(90 - declination);
            MarkCircle(axis, radius, color);
            MarkCircle(-axis, radius, color);
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
