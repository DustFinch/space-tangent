// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExercisePlugins(string directory)
    {
        string restore = Path.Combine(directory, "before-plugins.stscene");
        ScenarioFile.Save(restore, Session.CaptureScenario(0));
        try
        {
            Require(_scene.LoadScenario(restore), "The plugin test needs the first observer active.");
            Session.Synchronize(false, 0);
            Session.SetPlaying(0, false);
            Session.SetPlaying(1, false);
            var other = Session[1];
            var scripts = Find<Button>("ScriptsTab");
            if (!scripts.ButtonPressed)
                await Click(scripts);
            var menu = Find<MenuButton>("ScriptMenu");
            if (System.Environment.GetEnvironmentVariable("SPACETANGENT_ORIGINAL_TOUR_PLUGIN") is { Length: > 0 } manifest)
            {
                var plugin = _scene.OpenPlugin(manifest) ?? throw new InvalidOperationException("The original tour plugin did not load.");
                Require(plugin.Manifest.Scripts.Count == 2, "The original plugin must publish both archived scripts.");
                await Invoke(plugin.Manifest.Scripts.Single(item => item.File == "tour.lua").Title);
                var visited = Complete(plugin);
                Require(plugin.Error is null && visited.Contains(new("sol/titan")) && visited.Contains(new("sol/neptune")) &&
                    visited.Count >= 18 && Session[1] == other,
                    $"The unchanged plugin tour must reach the outer planets with the other observer unchanged. {plugin.Error}");
                await Invoke("Disable " + plugin.Manifest.Name);
                Require(!plugin.Enabled, "The original plugin must stop through the existing Script menu.");
            }

            string root = Path.Combine(directory, "Plugin с пробелами");
            Directory.CreateDirectory(root);
            string? texture = System.Environment.GetEnvironmentVariable("SPACETANGENT_PLUGIN_MEDIA_JPEG");
            if (texture is not null)
                File.Copy(texture, Path.Combine(root, "image.jpg"));
            else
            {
                using var icon = GD.Load<Texture2D>("res://UI/space-tangent.png").GetImage();
                Require(icon.SaveJpg(Path.Combine(root, "image.jpg")) == Error.Ok, "Prepare a local plugin image.");
            }
            File.WriteAllText(Path.Combine(root, "plugin.json"), """
                {"format":1,"api":1,"id":"local-tools","name":"Local tools","version":"1","main":"main.lua"}
                """);
            File.WriteAllText(Path.Combine(root, "helper.lua"), "return {hours = 1}");
            File.WriteAllText(Path.Combine(root, "main.lua"), """
                local settings = require("helper")
                local observers = celestia:getobservers()
                assert(#observers == 1 and observers[1] == celestia:getobserver())
                observers[1]:setfov(math.rad(42))
                assert(math.abs(math.deg(observers[1]:getfov()) - 42) < 1e-10)
                plugin.command("step", "Step one hour", function()
                    st.wait(0.2)
                    st.settime(st.gettime() + settings.hours / 24)
                end)
                plugin.command("media", "Show local image", function()
                    st.overlay("image.jpg", 20, 0.5, 0.5, 64, 64)
                end)
                plugin.command("denied-media", "Open outside image", function()
                    st.overlay("../icon.png")
                end)
                plugin.command("denied", "Read outside plugin", function()
                    plugin.read("../unrelated.lua")
                end)
                plugin.on("select", function(object)
                    if object and object:id() == "sol/moon" then
                        plugin.run(function() st.settime(st.gettime() + 1 / 24) end)
                    end
                end)
                plugin.importer(".hours", function(text, name)
                    assert(text == "one hour" and name == "step.hours")
                    return "st.log('local importer')"
                end, "Local hour input")
                """);
            var local = _scene.OpenPlugin(Path.Combine(root, "plugin.json")) ??
                throw new InvalidOperationException("Could not load the local compatibility fixture.");
            await Until(() => local.Commands.Any() && !local.HasJobs, "Initializing the local plugin");
            Require(local.Error is null && Math.Abs(Session[0].EffectiveFieldOfView - 42) < 1e-9 && Session[1] == other,
                $"The plugin must have zoom access only to its own observer. {local.Error}");
            Require(local.Convert(".hours", "one hour", "step.hours") == "st.log('local importer')",
                "The registered plugin importer must run and validate returned Lua.");
            var epoch = Session[0].Epoch;
            await Invoke("Read outside plugin");
            _ = Complete(local);
            Require(local.Enabled && local.Error?.Contains("local Lua paths", StringComparison.Ordinal) == true,
                "A plugin file access error must be contained to that command.");
            await Invoke("Step one hour");
            _ = Complete(local);
            Require(Math.Abs((double)Session[0].Epoch.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond - 3600) < .01,
                "A separate plugin command must still execute and wait after a handler error.");
            Session.SelectObject(0, SolarSystemSample.EarthId);
            local.Step(.1);
            Session.SelectObject(0, SolarSystemSample.MoonId);
            _ = Complete(local);
            Require(Math.Abs((double)Session[0].Epoch.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond - 7200) < .01 &&
                Session[1] == other, "Selection events and spawned work must belong to the plugin's observer.");
            await Invoke("Open outside image");
            _ = Complete(local);
            Require(local.Error?.Contains("relative to the script folder", StringComparison.Ordinal) == true,
                "Plugin media must stay inside its own folder.");
            await Invoke("Show local image");
            _ = Complete(local);
            Require(_scene.Renderer(0).FindChild("ScriptImage", true, false) is TextureRect &&
                _scene.Renderer(1).FindChild("ScriptImage", true, false) is null,
                "Plugin media must appear only in the plugin observer.");
            await Invoke("Disable Local tools");
            await Until(() => _scene.Renderer(0).FindChild("ScriptImage", true, false) is null, "Clearing disabled plugin media");
            GD.Print("UI: displayed local plugin media, isolated the view, and cleared it on disable.");
            Require(!local.Enabled && local.Commands.Count() == 0 && local.Importers.Count() == 0,
                "Disabling a plugin must remove its commands and importers.");
            GD.Print("UI: ran the original tour plugin and checked local commands, waits, events, importer, folder isolation and disable.");

            async Task Invoke(string label)
            {
                var popup = menu.GetPopup();
                if (!popup.Visible)
                    await Click(menu);
                int index = Enumerable.Range(0, popup.ItemCount).FirstOrDefault(item => popup.GetItemText(item) == label, -1);
                await ChooseMenuItem(popup, index);
            }

            HashSet<ObjectId> Complete(ObserverLuaPlugin plugin)
            {
                var visited = new HashSet<ObjectId>();
                for (int step = 0; step < 1600; step++)
                {
                    plugin.Step(.25);
                    var view = Session[0];
                    if (view.Selection is { } id)
                        visited.Add(id);
                    if (Session.IsFlying(0))
                        Session.UpdateCamera(0, Session.World.Graph.Evaluate(view.Epoch), .25);
                    if (!plugin.HasJobs)
                        return visited;
                }
                throw new InvalidOperationException("Plugin execution did not settle.");
            }
        }
        finally
        {
            Require(_scene.LoadScenario(restore), "The previous workspace must reopen after checking local plugins.");
        }
    }
}
