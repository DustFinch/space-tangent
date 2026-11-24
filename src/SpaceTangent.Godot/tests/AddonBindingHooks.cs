// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Godot.Simulation;

// Excluded from ExportRelease together with the probes. The unimplemented
// partial checkpoint and its calls disappear entirely from production exports.
public partial class SolarSystemScene
{
    internal Action<int>? AddonBindingCheckpoint { get; set; }
    internal AddonGeneration? PublishedAddons => _addOns;
    internal AddonGeneration? RetiringAddons => _retiringAddons;
    internal LocalAddonSettings AppliedAddonSettings => _localAddons;
    internal Task CheckLateObserverCallbacks() => Task.WhenAll(FindNearbyEvents(), FindNextObserverEvent(),
        CalculateObserverCharts(), FindObserverNight(), CalculateTonight());
    partial void OnAddonBinding(int view) => AddonBindingCheckpoint?.Invoke(view);
}
