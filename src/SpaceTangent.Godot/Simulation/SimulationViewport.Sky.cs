// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SkyEnvironment = Godot.Environment;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private SkyPresentation? _skyPresentation;
    private BrightSkyOverlay? _spaceSky => _skyPresentation?.Overlay;
    internal SkySurveyCanvas? SpaceSurveyCanvas => _skyPresentation?.Survey;
    internal SkySurveyCanvas? SurfaceSurveyCanvas => _surface?.SurveyCanvas;

    internal void SetSkyData(BrightSkyData data)
    {
        _compactSky = data;
        SetMinorBodySwarm(data.MinorBodies);
        _satelliteMagnitudes = data.SatelliteMagnitudes;
        _skyPresentation = new(this, data, layer => InstallGalacticSky(layer, data.ZodiacalMap));
        _surface?.SetSkyData(data);
    }

    // The observer's ΔT model for the frame being drawn, for helpers that place Earth surface targets.
    private DeltaTModel _frameDeltaT = DeltaTModel.MorrisonStephenson2004;

    internal bool Draw(ObserverView observer, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        _frameDeltaT = deltaT;
        if (!PrepareBodyObserver(ref observer, snapshot, deltaT))
        {
            HideNavigation();
            _surface?.Draw(false, snapshot.SolarSystem.GetValueOrDefault(), deltaT);
            _spaceSky?.Hide();
            return false;
        }
        if (ArchivedGeometry is not null)
            observer = observer with
            {
                Sky = observer.Sky with { Labels = false, Stars = ArchivedGeometry is { StellarView: null, StarPoints.Count: 0 } && observer.Sky.Stars }
            };
        PrepareGalacticSky(observer, snapshot, deltaT);
        bool available = DrawScene(observer, snapshot, deltaT);
        DrawBodyHorizon(snapshot, !observer.Sky.HideTerrain);
        DrawArchivedStars(ArchivedGeometry?.StellarView, observer.Epoch.ToJulianDate());
        DrawArchivedStarPoints(ArchivedGeometry?.StarPoints);
        DrawArchivedGroundGlow(ArchivedGeometry);
        if (_spaceSky is not null)
        {
            var frame = new ObserverRenderFrame(observer, snapshot, deltaT);
            var display = _skyPresentation!.PrepareSurveys(frame, SpatialStarFieldVisible, CompactLensVisible, _galacticTexture is not null);
            var camera = display.Camera;
            float tangent = display.Tangent;
            bool catalogue = display.Catalogue;
            DrawGalacticSky(display.Orbital, camera, tangent, observer, snapshot, deltaT, SpaceSurveyCanvas!.HasSurvey);
            bool render = _skyPresentation.UpdateVisibility(frame, display, GalacticSkyVisible);
            if (render && camera is not null)
            {
                Basis basis = camera.GlobalBasis;
                Vector3d observerFromSun = observer.Navigation is { } pose && CatalogueWorld is { } world
                    ? pose.AnchorKilometers + pose.ResolvePlacement(snapshot, deltaT).OffsetKilometers - world.StellarOrigin : default;
                Vector3d? localPole = _bodyObserver is { } bodySite && snapshot.TryOrientation(bodySite.Camera.SurfaceBody, out var bodyRotation)
                    ? bodyRotation.ToWorld.Z : null;
                _spaceSky.ShowFrame(new(observer.Epoch, observer.Sky, observer.Selection,
                    Icrs(-basis.Z), Icrs(basis.X), Icrs(basis.Y), tangent, ShowCatalogue: catalogue, ObserverFromSun: observerFromSun,
                    SiteAxes: _bodyObserver?.Frame.EastNorthUp,
                    BodyPath: _bodyObserver is { } site && observer.Sky.Markings.HasFlag(SkyMarkings.SelectedBodyPath) &&
                        observer.Selection is { } selected && CatalogueWorld is { } data
                        ? _bodySkyPath.Sample(data, site.Camera.SurfaceBody, selected, observer.Epoch, deltaT, IncrementalPaths) : null,
                    LocalPole: localPole));
            }
        }
        DrawEyepiece(observer);
        DrawFinders(observer);
        Optics.Remember(new(observer, snapshot, deltaT));
        return available;

        static Vector3 Icrs(Vector3 value) => new(value.X, -value.Z, value.Y);
    }

    internal BrightSkyOverlay? SpaceCultureOverlay => _spaceSky is { Visible: true } ? _spaceSky : null;

    internal bool TransientStarVisible(ObjectId id) => _spaceSky?.TransientVisible(id) == true;

    internal ObjectId? PickObject(Vector2 position) =>
        InsideEyepiece(position) ? PickNavigationBody(position) ?? _surface?.PickStar(position) ?? _spaceSky?.Pick(position) : null;
}
