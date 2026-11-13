// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Net.Http;
using System.Text;
using Godot;
using SpaceTangent.Application.Assets;

namespace SpaceTangent.Godot.Simulation;

internal sealed partial class SkySurveyTiles
{
    private const string Mirror = "https://irsa.ipac.caltech.edu/data/hips/CDS/";
    private const int MaximumRequests = 6, MaximumImageBytes = 16 * 1024 * 1024;
    private const long MaximumCacheBytes = 256L * 1024 * 1024;
    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["dss"] = "DSS2/color/",
        ["2mass"] = "2MASS/Color/",
        ["allwise"] = "AllWISE/RGB-W4-W2-W1/",
        ["galex"] = "GALEX/GR6-03-2014/AIS-Color/",
        ["sdss"] = "SDSS/DR9/color/",
        ["iris"] = "IRIS/color/",
        ["planck"] = "PLANCK/R2/HFI/color/",
    };
    private static readonly System.Net.Http.HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly string _remoteRoot = Path.GetFullPath(
        System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_CACHE") ??
        ProjectSettings.GlobalizePath("user://hips"));
    private readonly bool _offline = System.Environment.GetEnvironmentVariable("SPACETANGENT_HIPS_OFFLINE") == "1";
    private readonly CancellationTokenSource _remoteLifetime = new();
    private readonly Dictionary<string, RemoteRequest> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _unavailable = new(StringComparer.Ordinal);
    private long? _cacheBytes;

    private sealed record RemoteRequest(string Id, bool Properties, LocalSkySurvey? Survey, bool AllSky, Task<byte[]?> Bytes);
    internal int Revision { get; private set; }

    private void RequestRemote(string id, string relative, bool properties = false, LocalSkySurvey? survey = null,
        bool allSky = false)
    {
        if (_offline || DisplayServer.GetName() == "headless" || _pending.Count >= MaximumRequests)
            return;
        string path = Path.Combine(_remoteRoot, id, relative.Replace('/', Path.DirectorySeparatorChar));
        if (_pending.ContainsKey(path) ||
            (_unavailable.TryGetValue(path, out long retryAfter) && retryAfter > System.Environment.TickCount64))
            return;
        int limit = properties ? 64 * 1024 : MaximumImageBytes;
        var url = new Uri(Mirror + Providers[id] + relative);
        _pending.Add(path, new(id, properties, survey, allSky, Download(url, limit, _remoteLifetime.Token)));
    }

    internal bool PublishRemote()
    {
        if (_pending.Count == 0)
            return false;
        var completed = _pending.Where(entry => entry.Value.Bytes.IsCompleted).ToArray();
        if (completed.Length == 0)
            return false;
        foreach (var (path, request) in completed)
        {
            _pending.Remove(path);
            try
            {
                if (request.Bytes.GetAwaiter().GetResult() is not { } bytes)
                {
                    MarkUnavailable(path);
                    continue;
                }
                if (request.Properties)
                {
                    using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                    _surveys[request.Id] = LocalSkySurvey.ReadProperties(Path.Combine(_remoteRoot, request.Id), reader);
                }
                else
                {
                    if (!ValidRemoteImage(bytes, request.Survey!, request.AllSky))
                        throw new InvalidDataException("The remote HiPS image has an unsupported header or dimensions.");
                    DecodeTexture(path, bytes, request.Survey, request.AllSky);
                }
                _unavailable.Remove(path);
                try { SaveRemote(path, bytes); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    GD.Print($"HiPS cache {request.Id}: {error.Message}");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                MarkUnavailable(path);
                GD.Print($"HiPS {request.Id}: {error.Message}");
            }
        }
        // A failed request also frees a slot for the next visible tile.
        Revision++;
        return true;
    }

    private void MarkUnavailable(string path)
    {
        if (_unavailable.Count >= MaximumLookups)
            _unavailable.Clear();
        _unavailable[path] = System.Environment.TickCount64 + 120_000;
    }

    private void SaveRemote(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _cacheBytes ??= Directory.EnumerateFiles(_remoteRoot, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        if (_cacheBytes + bytes.Length > MaximumCacheBytes)
            return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            _cacheBytes += bytes.Length;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static async Task<byte[]?> Download(Uri url, int limit, CancellationToken lifetime)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
                return null;
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > limit)
                    return null;
                output.Write(buffer, 0, count);
            }
            return output.Length == 0 ? null : output.ToArray();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    private static bool ValidRemoteImage(ReadOnlySpan<byte> bytes, LocalSkySurvey survey, bool allSky)
    {
        if (survey.Extension == "png")
            return ValidPhotographHeader(bytes) && ValidSize(
                BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]), BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]));
        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8)
            return false;
        int index = 2;
        while (index + 4 <= bytes.Length && bytes[index++] == 0xff)
        {
            int marker = bytes[index++];
            if (marker == 0xff)
            {
                index--;
                continue;
            }
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[index..]);
            if (length < 2 || length > bytes.Length - index || marker is 0xda or 0xd9)
                return false;
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3)
                return length >= 8 && ValidSize(BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..]));
            index += length;
        }
        return false;

        bool ValidSize(int width, int height) => width is >= 2 and <= 4096 && height is >= 2 and <= 4096 &&
            (allSky ? LocalSkySurvey.ValidAllSky(width, height) : width == height && width == survey.TileWidth);
    }

    public void Dispose()
    {
        _remoteLifetime.Cancel();
        _remoteLifetime.Dispose();
        _pending.Clear();
    }
}
