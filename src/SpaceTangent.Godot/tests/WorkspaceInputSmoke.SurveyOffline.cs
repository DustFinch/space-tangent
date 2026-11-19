// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Http;
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private const string SurveyProperties = "obs_title=Offline fixture\nhips_frame=equatorial\nhips_tile_format=png\nhips_tile_width=64\nhips_order=3\n";

    private async Task ExerciseSurveyOffline(string directory)
    {
        string? cache = System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_CACHE");
        string? forced = System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE");
        try
        {
            string root = Path.Combine(directory, "fake-hips");
            Directory.CreateDirectory(Path.Combine(root, "sdss"));
            File.WriteAllText(Path.Combine(root, "sdss", "properties"), SurveyProperties);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_CACHE", root);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE", null);
            var handler = new SurveyHandler();
            using var client = new System.Net.Http.HttpClient(handler);
            using var tiles = new SkySurveyTiles(Path.Combine(root, "no-local"), root, client);
            var local = tiles.Find("sdss")!;
            string path = local.TilePath(3, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8))
            {
                image.Fill(Colors.Blue);
                image.SavePng(path);
            }
            using var cached = tiles.Texture(path, local)!;
            tiles.Find("allwise");
            Require(handler.Requests == 1, "A fake request must be in flight before switching offline.");
            tiles.SetOffline(true);
            Require(handler.Cancelled == 1, "Switching offline must cancel the in-flight request.");
            tiles.Find("dss");
            Require(handler.Requests == 1 && tiles.Find("sdss") is not null &&
                ReferenceEquals(cached, tiles.Texture(path, local)), "Offline must preserve local/cached tiles without issuing requests.");
            handler.Block = false;
            tiles.SetOffline(false);
            tiles.Find("allwise");
            await Until(() => { tiles.PublishRemote(); return tiles.Find("allwise") is not null; }, "Resuming fake HiPS downloads");
            Require(handler.Requests == 2, "Re-enabling downloads must issue exactly one fresh request.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_CACHE", cache);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE", forced);
        }

        if (forced != "1")
        {
            if (!Find<Button>("AddOnsTab").ButtonPressed) await Click(Find<Button>("AddOnsTab"));
            bool before = _scene.SurveysOffline;
            await Click(Find<CheckButton>("HipsOffline"));
            Require(_scene.SurveysOffline != before && new LocalAddonSettingsFile().Load().HipsOffline != before,
                "The offline control must change live policy and persist settings.");
            await Click(Find<Button>("ReloadAddOns"));
            await WaitForAddOns("Reloading with the persisted HiPS policy");
            Require(_scene.SurveysOffline != before, "Add-on reload must retain offline mode.");
            await Click(Find<CheckButton>("HipsOffline"));
        }
        GD.Print("UI: offline surveys cancelled fake HTTP, preserved cached maps, resumed downloads and saved the UI setting.");
    }

    private sealed class SurveyHandler : HttpMessageHandler
    {
        internal int Requests, Cancelled;
        internal bool Block = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (!Block)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SurveyProperties) });
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => { Interlocked.Increment(ref Cancelled); completion.TrySetCanceled(token); });
            return completion.Task;
        }
    }
}
