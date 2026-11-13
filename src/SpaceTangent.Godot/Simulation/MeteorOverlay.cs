// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

internal partial class MeteorOverlay : Control
{
    private sealed record Streak(Vector3 Start, Vector3 Away, float Length, double Age, double Duration, Color Tint);

    private readonly List<Streak> _streaks = [];
    private readonly Random _random = new(20240813);
    private MeteorCatalogue _catalogue = null!;
    private IReadOnlyList<MeteorRadiant> _radiants = [];
    private JulianInstant? _previous, _lastRadiant;
    private EarthSite _site;
    private double _limit, _debt;
    private ulong _lastUpdate;
    private Vector3 _forward, _right, _cameraUp, _zenith;
    private float _tangent, _night;
    private bool _showRadiants;
    private static readonly Color RadiantColor = new(1f, .72f, .5f, .85f);

    internal int ActiveTrailCount => _streaks.Count;

    internal bool ShowsRadiant(string code) => Visible && _showRadiants && _night > 0 &&
        _radiants.Any(item => item.Shower.Code == code && TryProject(BrightSkyOverlay.Convert(item.Direction), out _));

    internal void Configure(MeteorCatalogue catalogue)
    {
        _catalogue = catalogue;
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;
        Resized += QueueRedraw;
        Hide();
    }

    internal void HideFrame()
    {
        Hide();
        _previous = null;
        _lastRadiant = null;
        _streaks.Clear();
        _debt = 0;
    }

    internal void ShowFrame(SolarSystemSample solar, SurfaceLighting light, EarthSite site, double limit,
        Vector3 forward, Vector3 right, Vector3 cameraUp, float tangent,
        bool showTrails, bool showRadiants, int rateMultiplier)
    {
        _showRadiants = showRadiants;
        _forward = forward;
        _right = right;
        _cameraUp = cameraUp;
        _zenith = BrightSkyOverlay.Convert(light.EastNorthUp.Z);
        _tangent = tangent;
        _night = Mathf.Clamp((float)(-light.SolarAltitudeRadians / (Math.PI / 18)), 0, 1);
        if (_night <= 0)
        {
            HideFrame();
            return;
        }
        Show();
        var epoch = solar.Epoch;
        if (_lastRadiant is not { } old || site != _site || limit != _limit || Math.Abs(Seconds(epoch, old)) >= 30)
        {
            _radiants = _catalogue.Radiants(solar, light, limit);
            _lastRadiant = epoch;
            _site = site;
            _limit = limit;
        }

        ulong now = Time.GetTicksMsec();
        double elapsed = Math.Min(.25, (now - _lastUpdate) / 1000.0);
        _lastUpdate = now;
        double simulated = _previous is { } previous ? Seconds(epoch, previous) : 0;
        _previous = epoch;
        if (!showTrails || simulated < 0 || simulated > 1800)
        {
            _debt = 0;
            _streaks.Clear();
        }
        else if (simulated > 0)
        {
            for (int i = _streaks.Count - 1; i >= 0; i--)
            {
                var streak = _streaks[i];
                double age = streak.Age + elapsed;
                if (age >= streak.Duration)
                    _streaks.RemoveAt(i);
                else
                    _streaks[i] = streak with { Age = age };
            }
            Spawn(simulated * rateMultiplier);
        }
        QueueRedraw();
    }

    private void Spawn(double seconds)
    {
        double sporadic = 10 / Math.Pow(3, 6.5 - _limit);
        double total = _radiants.Sum(item => item.ObservedHourlyRate) + sporadic;
        _debt = Math.Min(4, _debt + total * seconds / 3600);
        for (; _debt >= 1 && _streaks.Count < 80; _debt--)
        {
            double choice = _random.NextDouble() * total;
            float z = 2 * _random.NextSingle() - 1;
            float angle = Mathf.Tau * _random.NextSingle();
            float horizontal = MathF.Sqrt(1 - z * z);
            Vector3 radiant = new(horizontal * MathF.Cos(angle), horizontal * MathF.Sin(angle), z);
            double speed = 25 + 45 * _random.NextDouble();
            foreach (var item in _radiants)
            {
                choice -= item.ObservedHourlyRate;
                if (choice >= 0)
                    continue;
                radiant = BrightSkyOverlay.Convert(item.Direction);
                speed = item.Shower.SpeedKilometersPerSecond is > 0 and var known ? known : 40;
                break;
            }
            Vector3 first = radiant.Cross(Math.Abs(radiant.Y) < .9 ? Vector3.Up : Vector3.Right).Normalized();
            Vector3 second = radiant.Cross(first);
            float separation = Mathf.DegToRad(10 + 60 * _random.NextSingle());
            float turn = Mathf.Tau * _random.NextSingle();
            var (sine, cosine) = MathF.SinCos(separation);
            var start = radiant * cosine + (first * MathF.Cos(turn) + second * MathF.Sin(turn)) * sine;
            if (start.Dot(_zenith) < .08f)
                continue;
            var away = (start * cosine - radiant).Normalized();
            float length = Mathf.DegToRad(4 + 15 * _random.NextSingle());
            double duration = Math.Clamp(length / Mathf.DegToRad((float)(.55 * speed) * sine), .18, 1.4);
            Color tint = speed < 30 ? new(1, .72f, .48f) : speed > 55 ? new(.71f, .91f, 1) : new(1, .93f, .78f);
            _streaks.Add(new(start, away, length, 0, duration, tint));
        }
    }

    public override void _Draw()
    {
        if (_showRadiants)
            DrawRadiants();
        foreach (var streak in _streaks)
        {
            float fraction = (float)(streak.Age / streak.Duration);
            float head = streak.Length * fraction;
            float tail = streak.Length * Math.Max(0, fraction - .42f);
            if (TryProject(streak.Start * MathF.Cos(head) + streak.Away * MathF.Sin(head), out Vector2 a) &&
                TryProject(streak.Start * MathF.Cos(tail) + streak.Away * MathF.Sin(tail), out Vector2 b))
                DrawLine(a, b, new Color(streak.Tint, _night * (1 - fraction)), 2, true);
        }
    }

    private void DrawRadiants()
    {
        foreach (var item in _radiants)
        {
            if (!TryProject(BrightSkyOverlay.Convert(item.Direction), out Vector2 point))
                continue;
            Color tint = new(RadiantColor, RadiantColor.A * _night);
            DrawArc(point, 7, 0, Mathf.Tau, 18, tint, 1.2f, true);
            DrawString(ThemeDB.FallbackFont, point + new Vector2(9, -6), item.Shower.Code,
                fontSize: 13, modulate: tint);
        }
    }

    private bool TryProject(Vector3 direction, out Vector2 point)
    {
        point = default;
        float depth = direction.Dot(_forward);
        if (depth <= .02 || direction.Dot(_zenith) < 0 || Size.Y <= 0)
            return false;
        float scale = Size.Y / (2 * depth * _tangent);
        point = Size / 2 + new Vector2(direction.Dot(_right), -direction.Dot(_cameraUp)) * scale;
        return new Rect2(Vector2.Zero, Size).Grow(12).HasPoint(point);
    }

    private static double Seconds(JulianInstant first, JulianInstant second) =>
        (double)first.DifferenceNanoseconds(second) / JulianInstant.NanosecondsPerSecond;
}
