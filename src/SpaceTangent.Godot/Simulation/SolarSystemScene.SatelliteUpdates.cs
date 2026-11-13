// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Http;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private CancellationTokenSource? _satelliteUpdate;

    private async Task UpdateSatelliteGroups()
    {
        if (_satelliteUpdate is not null || Session is null)
            return;

        using var cancellation = new CancellationTokenSource();
        _satelliteUpdate = cancellation;
        _status.Text = "Downloading satellite groups...";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SpaceTangent/0.1");
            string directory = AddonLoader.SatelliteUpdatesPath();
            var result = await Task.Run(() => SatelliteGroupUpdater.UpdateAsync(client, directory,
                TimeProvider.System, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsInsideTree())
                return;
            if (result.Updated > 0 && !await ReloadAddOns())
                return;

            _status.Text = result.Updated > 0 ? $"Updated satellite groups: {result.Updated}." :
                result.Failures.Count > 0 ? "Satellite group update failed." : "Satellite groups are current.";
            _status.TooltipText = string.Join("\n", result.Failures);
            foreach (string failure in result.Failures)
                global::Godot.GD.Print($"Satellite group update: {failure}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (IsLoadError(error))
        {
            if (IsInsideTree())
                _status.Text = $"Could not update satellite groups. {error.Message}";
        }
        finally
        {
            _satelliteUpdate = null;
        }
    }
}
