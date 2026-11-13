// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net;
using SpaceTangent.Application;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SatelliteGroupUpdaterTests
{
    [ArchivedGroupsFact]
    [Trait("Category", "ExternalData")]
    public async Task KeepsCachedGroupsWhenAReplyFailsAndMergesValidUpdates()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")!;
        var directory = Directory.CreateTempSubdirectory("st-satellite-updates-");
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            string cached = Path.Combine(directory.FullName, "visual.tle");
            string existing = File.ReadAllText(Path.Combine(source, "visual.txt"));
            File.WriteAllText(cached, existing);
            File.SetLastWriteTimeUtc(cached, clock.GetUtcNow().UtcDateTime.AddHours(-3));

            string? live = Environment.GetEnvironmentVariable("SPACETANGENT_TLE_LIVE_STATIONS");
            if (live is not null)
                LocalTleCatalogue.ValidateGroup(live);
            using var handler = new ArchiveHandler(source);
            using var client = new HttpClient(handler);
            var result = await SatelliteGroupUpdater.UpdateAsync(client, directory.FullName, clock);
            Assert.Equal(3, result.Updated);
            Assert.StartsWith("visual:", Assert.Single(result.Failures));
            Assert.Equal(existing, File.ReadAllText(cached));
            Assert.Equal(4, handler.Requests);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
            var original = LocalTleCatalogue.Load(source);
            var merged = LocalTleCatalogue.LoadSources([source, directory.FullName]);
            Assert.Equal(original.Select(satellite => satellite.Id).OrderBy(id => id.Value),
                merged.Select(satellite => satellite.Id).OrderBy(id => id.Value));
            Assert.Equal(243, merged.Count);

            result = await SatelliteGroupUpdater.UpdateAsync(client, directory.FullName, clock);
            Assert.Equal(0, result.Updated);
            Assert.Empty(result.Failures);
            Assert.Equal(4, handler.Requests);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ArchiveHandler(string source) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requests++;
            Assert.Equal("celestrak.org", request.RequestUri!.Host);
            string group = request.RequestUri.Query.Split('=', '&')[1];
            string text = group == "visual" ? "<html>Invalid upstream response</html>"
                : File.ReadAllText(Path.Combine(source, group + ".txt"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    private sealed class ArchivedGroupsFactAttribute : FactAttribute
    {
        public ArchivedGroupsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")))
                Skip = "Set the original local satellite groups.";
        }
    }
}
