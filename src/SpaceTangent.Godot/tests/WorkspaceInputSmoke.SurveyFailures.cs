// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using System.Net.Http;
using System.Text;
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class WorkspaceInputSmoke
{
    private async Task ExerciseSurveyRemoteFailures(string directory)
    {
        string? oldRoot = System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_CACHE");
        string? oldOffline = System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE");
        try
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE", null);
            foreach (string mode in new[] { "png", "jpg", "status", "properties", "declared-size", "stream-size", "image", "cache-full", "cache-blocked" })
            {
                string root = Path.Combine(directory, "survey-" + mode);
                if (mode == "cache-blocked") File.WriteAllText(root, "This file deliberately blocks the cache directory.");
                else Directory.CreateDirectory(root);
                if (mode == "cache-full")
                {
                    using var sparse = File.Create(Path.Combine(root, "budget-witness.bin"));
                    sparse.SetLength(256L * 1024 * 1024);
                }
                System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_CACHE", root);
                using var image = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
                image.Fill(Colors.Green);
                byte[] bytes = mode == "jpg" ? image.SaveJpgToBuffer() : image.SavePngToBuffer();
                var handler = new SurveyFailureHandler(mode, bytes);
                using var client = new System.Net.Http.HttpClient(handler);
                using var tiles = new SkySurveyTiles(Path.Combine(directory, "absent-surveys"), directory, client);
                Require(tiles.Find("allwise") is null, "Remote properties cannot publish synchronously before the pending queue drains.");
                await Until(() => tiles.PublishRemote(), "Publishing controlled survey properties");
                bool valid = mode is "png" or "jpg" or "image" or "cache-full" or "cache-blocked";
                var survey = tiles.Find("allwise");
                Require((survey is not null) == valid && handler.Requests == 1,
                    $"Survey {mode} must retain either valid properties or one throttled failed request.");
                Require(!tiles.PublishRemote(), "An empty remote queue cannot publish another revision.");
                if (!valid) continue;
                if (mode is "cache-full" or "cache-blocked")
                {
                    Require(!File.Exists(Path.Combine(root, "allwise", "properties")),
                        "A full or unwritable disk cache must not affect the in-memory survey.");
                    continue;
                }
                string path = survey!.TilePath(3, 1);
                Require(tiles.Texture(path, survey) is null, "A remote tile is initially pending.");
                await Until(() => tiles.PublishRemote(), "Publishing controlled survey tile");
                var texture = tiles.Texture(path, survey);
                Require((texture is not null) == (mode != "image") && handler.Requests == 2,
                    "A decoded image must publish once; a malformed image must be throttled.");
                if (texture is null) continue;
                Require(texture.GetWidth() == 64 && texture.GetHeight() == 64 &&
                    ReferenceEquals(texture, tiles.Texture(path, survey)), "A completed tile must retain its dimensions and cache identity.");
                using var pixel = texture.GetImage();
                var color = pixel.GetPixel(32, 32);
                Require(color.G > color.R + .5f && color.G > color.B + .5f, "The remotely decoded authored tile must be green.");
                tiles.SetOffline(true);
                int revision = tiles.Revision;
                tiles.SetOffline(true);
                Require(tiles.Revision == revision && ReferenceEquals(texture, tiles.Texture(path, survey)),
                    "Repeated offline policy must preserve completed images and avoid extra revisions.");
                using var reloaded = new SkySurveyTiles(Path.Combine(directory, "absent-surveys"), directory, client);
                reloaded.SetOffline(true);
                var diskSurvey = reloaded.Find("allwise");
                Require(diskSurvey is not null && reloaded.Texture(diskSurvey.TilePath(3, 1), diskSurvey) is not null && handler.Requests == 2,
                    "Saved remote properties and tiles must reopen offline without another HTTP request.");
            }
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_CACHE", oldRoot);
            System.Environment.SetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE", oldOffline);
        }
        GD.Print("Controlled HiPS: PNG/JPEG pixels, offline disk reuse, failed/oversized/malformed responses and bounded/unwritable cache; no external HTTP.");
    }

    private sealed class SurveyFailureHandler(string mode, byte[] image) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            bool properties = request.RequestUri!.AbsolutePath.EndsWith("/properties", StringComparison.Ordinal);
            string metadata = SurveyProperties.Replace("=png", mode == "jpg" ? "=jpeg" : "=png");
            byte[] bytes = properties ? Encoding.UTF8.GetBytes(mode == "properties" ? "invalid\n" : metadata) :
                mode == "image" ? [1, 2, 3, 4] : image;
            HttpContent content = mode == "stream-size" ? new UnknownLengthSurveyContent(new byte[65537]) : new ByteArrayContent(bytes);
            if (mode == "declared-size") content.Headers.ContentLength = 65537;
            return Task.FromResult(new HttpResponseMessage(mode == "status" ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class UnknownLengthSurveyContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }
}
