// SPDX-License-Identifier: GPL-2.0-or-later
using System.Net.Http;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Application;

public sealed record SatelliteGroupUpdate(int Updated, IReadOnlyList<string> Failures);

/// <summary>Updates the local orbital element cache from the four supported GP groups.</summary>
public static class SatelliteGroupUpdater
{
    private static readonly string[] Groups = ["stations", "visual", "science", "gps-ops"];
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(2);

    public static async Task<SatelliteGroupUpdate> UpdateAsync(HttpClient client, string directory,
        TimeProvider clock, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(directory);
        var failures = new List<string>();
        int updated = 0;
        foreach (string group in Groups)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Path.Combine(directory, group + ".tle");
            string attempt = target + ".attempt";
            try
            {
                using var gate = new FileStream(target + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                DateTime now = clock.GetUtcNow().UtcDateTime;
                if (File.Exists(attempt) && now - File.GetLastWriteTimeUtc(attempt) < MinimumInterval ||
                    File.Exists(target) && now - File.GetLastWriteTimeUtc(target) < MinimumInterval)
                    continue;
                using (File.Create(attempt)) { }
                File.SetLastWriteTimeUtc(attempt, now);
                string uri = $"https://celestrak.org/NORAD/elements/gp.php?GROUP={group}&FORMAT=TLE";
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                string temporary = Path.Combine(directory, $".{group}-{Guid.NewGuid():N}.tmp");
                bool created = false;
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        var buffer = new byte[32 * 1024];
                        int length;
                        while ((length = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                        {
                            if (file.Length + length > 4 * 1024 * 1024)
                                throw new InvalidDataException("The response exceeds 4 MiB.");
                            await file.WriteAsync(buffer.AsMemory(0, length), cancellation).ConfigureAwait(false);
                        }
                        file.Flush(flushToDisk: true);
                    }
                    LocalTleCatalogue.ValidateGroup(temporary, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    File.SetLastWriteTimeUtc(temporary, now);
                    File.Move(temporary, target, overwrite: true);
                    created = false;
                    updated++;
                }
                finally
                {
                    if (created)
                        File.Delete(temporary);
                }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested &&
                error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
                    System.Text.DecoderFallbackException or OperationCanceledException)
            {
                failures.Add($"{group}: {error.Message}");
            }
        }
        return new(updated, failures.AsReadOnly());
    }
}
