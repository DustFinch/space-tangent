// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Core.Observing;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private async Task SaveEarthSite(string name, EarthSite site)
    {
        try
        {
            var path = AddonLoader.SavedEarthPlacePath();
            LegacySavedEarthPlaces.Save(path.Explicit ?? path.User, name, site);
            if (await ReloadAddOns())
                _status.Text = $"Saved site · {name.Trim()}";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not save site. {error.Message}";
        }
    }
}
