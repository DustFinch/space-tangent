// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Serialization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Application;

/// <summary>Per-view site, direction and horizon overlays for the Earth surface camera.</summary>
public readonly record struct EarthObserverCamera(EarthSite Site, double AzimuthDegrees, double ElevationDegrees,
    bool HorizonGrid = false, bool CardinalPoints = false)
{
    public static EarthObserverCamera Moscow { get; } = new(new(55.7558, 37.6173, .15), 237, 28);
    public static EarthObserverCamera Tromso { get; } = new(new(69.6492, 18.9553, .01), 180, 10);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroundSkyConditions? Conditions { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LandscapeId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideLandscapeFog { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EquatorialMount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MoonScale { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PlanetScale { get; init; }

    // Coordinates in the site's east, north and up basis.
    [JsonIgnore]
    public Vector3d VisiblePole
    {
        get
        {
            double latitude = Site.LatitudeDegrees * Math.PI / 180;
            double sign = latitude < 0 ? -1 : 1;
            return new(0, sign * Math.Cos(latitude), sign * Math.Sin(latitude));
        }
    }

    public EarthObserverCamera Turn(double yaw, double pitch)
    {
        const double radians = Math.PI / 180;
        double azimuth = AzimuthDegrees - yaw / radians;
        double altitude = ElevationDegrees + pitch / radians;
        if (EquatorialMount)
        {
            var (sinAzimuth, cosAzimuth) = Math.SinCos(AzimuthDegrees * radians);
            var (sinAltitude, cosAltitude) = Math.SinCos(ElevationDegrees * radians);
            Vector3d pole = VisiblePole;
            var forward = new Vector3d(sinAzimuth * cosAltitude, cosAzimuth * cosAltitude, sinAltitude);
            forward = NavigationPlacement.Rotate(forward, pole, yaw);
            double sine = Math.Clamp(Vector3d.Dot(forward, pole), -1, 1);
            var across = forward - pole * sine;
            if (across.Length > 1e-9)
            {
                double declination = Math.Clamp(Math.Asin(sine) + pitch, -89 * radians, 89 * radians);
                forward = across / across.Length * Math.Cos(declination) + pole * Math.Sin(declination);
            }
            azimuth = Math.Atan2(forward.X, forward.Y) / radians;
            altitude = Math.Asin(Math.Clamp(forward.Z, -1, 1)) / radians;
        }
        return this with { AzimuthDegrees = (azimuth % 360 + 360) % 360, ElevationDegrees = Math.Clamp(altitude, -89, 89) };
    }

    public bool IsValid() => (LandscapeId is null || ObservingLandscape.ValidId(LandscapeId)) && Site.IsValid() && Site.HeightKilometers <= 100 && Conditions?.IsValid() != false &&
        MoonScale is null or (>= 1 and <= 10) && PlanetScale is null or (>= 1 and <= 10) && AzimuthDegrees is >= 0 and < 360 && ElevationDegrees is >= -90 and <= 90;
}
