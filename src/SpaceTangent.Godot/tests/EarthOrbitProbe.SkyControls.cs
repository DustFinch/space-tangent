// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Godot.Simulation;
using SpaceTangent.Godot.UI;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private async Task ExerciseSkyControls()
    {
        var data = BrightSkyData.Load();
        var view = new SubViewport { Size = new(352, 256), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
        AddChild(view);
        var overlay = new BrightSkyOverlay { Size = new(256, 256) }; view.AddChild(overlay); overlay.Configure(data, false);
        try
        {
            var epoch = new JulianInstant(2451545, 0, TimeScale.Tdb);
            var sirius = data.Catalogue.Find(new ObjectId("hip/32349")) ?? throw new InvalidOperationException("The built-in Sirius entry is required.");
            Vector3 forward = BrightSkyOverlay.Convert(sirius.DirectionAt(epoch));
            var frame = Frame(epoch, forward) with
            {
                Horizon = forward, Conditions = new GroundSkyConditions(1) { Scintillation = true, Refraction = true },
                OpticalLimit = 3
            };
            overlay.ShowFrame(frame);
            await ToSignal(GetTree().CreateTimer(.12), SceneTreeTimer.SignalName.Timeout);
            if (!overlay.TryPoint(sirius.Id, out var point) || point.DistanceTo(new(128, 128)) > .1f || overlay.Pick(point) != sirius.Id)
                throw new InvalidOperationException("The refracted scintillating zenith star must project and remain pickable at the known optical centre.");
            await Drawn();
            using (var pixels = view.GetTexture().GetImage())
                if (pixels.GetPixel(128, 128).Luminance < .1f)
                    throw new InvalidOperationException("The selected bright star must produce visible finite GPU radiance.");
            if (data.Transients.Stars.Count == 0 || data.Boundaries.Count < 1000)
                throw new InvalidOperationException("Original transient and constellation-boundary inputs must be installed.");
            var transient = data.Transients.Stars.MinBy(star => star.PeakMagnitude)!;
            var peak = JulianInstant.FromJulianDate(transient.PeakJulianDay, TimeScale.Tdb);
            overlay.ShowFrame(Frame(peak, BrightSkyOverlay.Convert(transient.Direction)) with
                { Layers = new(true, true, false, false) { ConstellationBoundaries = true } });
            await Drawn();
            if (!overlay.TransientVisible(transient.Id)) throw new InvalidOperationException("The original brightest transient must be visible at its recorded peak.");
            overlay.ShowFrame(Frame(peak.AddSeconds(200 * 365.25 * 86400), BrightSkyOverlay.Convert(transient.Direction)));
            if (overlay.TransientVisible(transient.Id)) throw new InvalidOperationException("The same historical transient must fade below the display limit.");
            overlay.Hide();
            string[] pages = ["Search", "Time", "Layers", "Location", "Ephemerides", "Bookmarks", "AddOns", "Scripts", "Settings", "Help", "Unknown"];
            view.Size = new(pages.Length * 32, 32);
            for (int i = 0; i < pages.Length; i++)
                view.AddChild(new WorkspaceGlyph { Page = pages[i], Size = new(32, 32), Position = new(i * 32, 0) });
            await Drawn();
            using var icons = view.GetTexture().GetImage();
            for (int i = 0; i < pages.Length; i++)
            {
                int ink = 0;
                for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    if (icons.GetPixel(i * 32 + x, y).A > .1f) ink++;
                if (pages[i] == "Unknown" ? ink != 0 : ink < 10)
                    throw new InvalidOperationException($"Workspace icon {pages[i]} has unexpected visible bounds: {ink} pixels.");
            }
            GD.Print($"Sky controls GPU: Sirius zenith refraction/scintillation and picking, original {transient.Designation} peak/fade, real boundaries and ten bounded workspace icons passed.");
        }
        finally
        {
            data.Surveys.Dispose(); RemoveChild(view); view.QueueFree();
        }
        BrightSkyOverlay.SkyFrame Frame(JulianInstant time, Vector3 forward)
        {
            Vector3 right = forward.Cross(Vector3.Up).Normalized(), up = right.Cross(forward).Normalized();
            return new(time, new(true, false, false, false), null, forward, right, up, .2f);
        }
        async Task Drawn()
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        }
    }
}
