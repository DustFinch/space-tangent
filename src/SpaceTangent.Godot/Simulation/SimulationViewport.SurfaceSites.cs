// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

internal partial class SimulationViewport
{
    private const int MaximumSiteLabels = 24;
    private readonly List<Label3D> _siteLabels = new();
    private readonly HashSet<ObjectId> _visibleSites = new();
    internal bool ShowSurfaceSiteLabels { get; set; }
    internal bool SiteLabelVisible(ObjectId id) => _visibleSites.Contains(id);

    private void HideSurfaceSites()
    {
        _visibleSites.Clear();
        foreach (Label3D label in _siteLabels)
            label.Hide();
    }

    private void DrawSurfaceSites(NavigationCamera camera, ObjectId? selected, WorldSnapshot snapshot, DeltaTModel deltaT)
    {
        if (CatalogueWorld is not { } world)
            return;
        SurfaceSite? focus = selected is { } id ? world.FindSurfaceSite(id) : null;
        ObjectId? owner = focus?.Body ?? camera.Target ?? selected;
        if (owner is not { } body)
            return;
        var sites = world.SurfaceSitesOn(body);
        if (sites.Count == 0 || !world.TrySurfaceFrame(sites[0], snapshot, deltaT, out CartesianState state, out Basis3d orientation))
            return;
        NavigationPlacement placement = camera.ResolvePlacement(snapshot, deltaT);
        Vector3d eye = camera.AnchorKilometers + placement.OffsetKilometers;
        double units = camera.RangeKilometers / NavigationCameraUnits;
        double projectedDistance = (state.PositionKilometers - camera.AnchorKilometers).Length / units;
        double scale = projectedDistance > NavigationFar * .8 ? NavigationFar * .8 / projectedDistance : 1;
        if (focus is not null)
            Place(focus, force: true);
        if (ShowSurfaceSiteLabels)
        {
            foreach (SurfaceSite site in sites)
            {
                if (_visibleSites.Count == MaximumSiteLabels)
                    break;
                if (site != focus)
                    Place(site, force: false);
            }
        }

        void Place(SurfaceSite site, bool force)
        {
            Vector3d surface = state.PositionKilometers + orientation.Transform(site.OffsetKilometers);
            Vector3d normal = orientation.Transform(site.Radial);
            Vector3d toEye = eye - surface;
            if (Vector3d.Dot(normal, toEye) <= 0 ||
                !force && site.ScaleKilometers < toEye.Length * .008)
                return;
            Vector3d lifted = surface + normal * Math.Max(.25, site.OffsetKilometers.Length * .0015);
            Vector3 point = ToGodot((lifted - camera.AnchorKilometers) * scale, units);
            if (_navigationCamera.IsPositionBehind(point) ||
                !GetViewport().GetVisibleRect().HasPoint(_navigationCamera.UnprojectPosition(point)))
                return;
            int index = _visibleSites.Count;
            if (!_visibleSites.Add(site.Id))
                return;
            if (index == _siteLabels.Count)
            {
                Label3D created = CreateLabel("", Vector3.Zero);
                created.FixedSize = true;
                created.PixelSize = .0025f;
                created.FontSize = 18;
                created.NoDepthTest = false;
                _siteLabels.Add(created);
                AddChild(created);
            }
            Label3D label = _siteLabels[index];
            label.Text = site.Definition.Name;
            label.Position = point;
            label.Show();
        }
    }
}
