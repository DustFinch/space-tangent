// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static void ExerciseAddonSettingsContracts(string directory)
    {
        const string variable = "SPACETANGENT_ADDONS_SETTINGS_PATH";
        string? previous = System.Environment.GetEnvironmentVariable(variable);
        string path = Path.Combine(directory, "isolated-addon-settings.json");
        System.Environment.SetEnvironmentVariable(variable, path);
        try
        {
            var file = new LocalAddonSettingsFile();
            var first = file.FindOrCreate(LocalAddonSettings.Empty, LocalAddonKind.Mission, directory);
            var second = first with { Id = Guid.NewGuid(), Path = Path.Combine(directory, "second") };
            var settings = LocalAddonSettings.Empty.SetSource(first, true).SetSource(second, true);
            Require(!settings.Sources[0].Enabled && settings.Sources[1].Enabled && !settings.UsesDefault(LocalAddonKind.Mission),
                "Enabling a mission source must disable the previous mission and the default.");
            Require(first.Id == file.FindOrCreate(settings, first.Kind, directory).Id,
                "An existing source must preserve its identity when added again.");
            settings = settings.SetSource(second, false).SetDefault(LocalAddonKind.Mission, true);
            Require(settings.Sources.All(source => !source.Enabled) && settings.UsesDefault(LocalAddonKind.Mission),
                "Restoring the default must leave custom missions disabled.");
            file.Save(settings);
            Require(file.Load().Sources.SequenceEqual(settings.Sources), "The valid source choices must survive an atomic round trip.");
            byte[] valid = File.ReadAllBytes(path);
            foreach (var invalid in new[]
            {
                settings with { DisabledPacks = ["duplicate", "duplicate"] },
                settings with { DisabledPlugins = ["duplicate", "duplicate"] },
                settings with { Sources = [first with { Id = Guid.Empty }] },
                settings with { Sources = [first, first] },
                settings with { Sources = [first with { Path = "relative", RelativeToData = false }] },
                settings with { Sources = [first with { Path = "../outside", RelativeToData = true }] },
                settings with { Sources = [first with { Path = "nested/../../outside", RelativeToData = true }] },
                LocalAddonSettings.Empty with { Sources = [first] },
                settings with { Sources = [first, second] },
                settings with { Sources = Enumerable.Range(0, 64).Select(index => first with
                    { Id = Guid.NewGuid(), Path = Path.Combine(directory, new string('x', 1100) + index), Enabled = false }).ToArray() }
            })
            {
                Reject(() => file.Save(invalid));
                Require(File.ReadAllBytes(path).SequenceEqual(valid), "Rejected settings must preserve the last valid file.");
            }
            foreach (string json in new[] { "", "{\"version\":2,\"disabledDefaults\":[],\"sources\":[]}",
                         "{\"version\":1,\"disabledDefaults\":null,\"sources\":[]}" })
            {
                File.WriteAllText(path, json);
                Reject(() => file.Load());
            }
            using (var oversized = File.Create(path)) oversized.SetLength(65537);
            Reject(() => file.Load());
            file.Save(settings);
            Require(file.Load().Sources.SequenceEqual(settings.Sources), "Valid settings must be writable again after malformed input.");
            global::Godot.GD.Print("Add-on settings contracts: exclusive missions, stable identity, atomic rejection, portable path containment and recovery passed.");
        }
        finally { System.Environment.SetEnvironmentVariable(variable, previous); }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid add-on settings were accepted.");
        }
    }
}
