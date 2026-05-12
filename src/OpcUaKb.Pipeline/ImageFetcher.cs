using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// Image Fetcher — downloads content-addressed PNGs referenced by the
// reference.opcfoundation.org Single Page HTML view and uploads them to
// blob storage at images/{sha256}.png. The sha256 in the path makes the
// upstream URLs immutable, so the operation is idempotent: same sha ⇒
// same blob ⇒ skipped on subsequent runs. Across specs we get free dedup.
// ═══════════════════════════════════════════════════════════════════════

sealed partial class ImageFetcher
{
    const string BaseUrl = "https://reference.opcfoundation.org";
    const string ContainerName = "opcua-content";
    const string BlobPrefix = "images/";
    const int MaxConcurrency = 5;

    readonly HttpClient _http;
    readonly BlobContainerClient _container;
    readonly ILogger _log;
    readonly SemaphoreSlim _throttle = new(MaxConcurrency);

    public ImageFetcher(HttpClient http, BlobServiceClient blobs, ILogger log)
    {
        _http = http;
        _container = blobs.GetBlobContainerClient(ContainerName);
        _log = log;
    }

    /// <summary>
    /// Downloads each sha256-addressed image. Accepts inputs that are either
    /// a bare 64-char hex sha or a URL ending with /img/{sha}.png. Returns
    /// counts of (downloaded, skipped, errors). Idempotent — blobs that
    /// already exist are skipped without a network round-trip to the source.
    /// </summary>
    public async Task<(int Downloaded, int Skipped, int Errors)> FetchAsync(
        IEnumerable<string> sha256s, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sha256s)
        {
            if (TryNormalizeSha(raw, out var sha))
            {
                unique.Add(sha);
            }
            else
            {
                _log.LogWarning("[IMAGE_FETCH] Skipping invalid sha input: {Raw}", raw);
            }
        }

        var downloaded = 0;
        var skipped = 0;
        var errors = 0;
        var processed = 0;

        var tasks = unique.Select(async sha =>
        {
            await _throttle.WaitAsync(ct);
            try
            {
                var result = await FetchOneAsync(sha, ct);
                switch (result)
                {
                    case FetchResult.Downloaded:
                        Interlocked.Increment(ref downloaded);
                        break;
                    case FetchResult.Skipped:
                        Interlocked.Increment(ref skipped);
                        break;
                    case FetchResult.Error:
                        Interlocked.Increment(ref errors);
                        break;
                }

                var done = Interlocked.Increment(ref processed);
                if (done % 100 == 0)
                {
                    _log.LogInformation(
                        "[IMAGE_FETCH] Progress Processed={Done}/{Total} Downloaded={D} Skipped={S} Errors={E}",
                        done, unique.Count, downloaded, skipped, errors);
                }
            }
            finally
            {
                _throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        _log.LogInformation(
            "[IMAGE_FETCH] Downloaded={D} Skipped={S} Errors={E}",
            downloaded, skipped, errors);

        return (downloaded, skipped, errors);
    }

    async Task<FetchResult> FetchOneAsync(string sha, CancellationToken ct)
    {
        var blobName = $"{BlobPrefix}{sha}.png";
        var blob = _container.GetBlobClient(blobName);

        try
        {
            var exists = await blob.ExistsAsync(ct);
            if (exists.Value)
            {
                return FetchResult.Skipped;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[IMAGE_FETCH] Sha={Sha} ExistsCheck failed", sha);
            return FetchResult.Error;
        }

        var url = $"{BaseUrl}/img/{sha}.png";
        HttpResponseMessage response;
        try
        {
            response = await RetryHelper.RetryAsync(
                () => _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct),
                _log);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[IMAGE_FETCH] Sha={Sha} Url={Url} Error=RequestFailed", sha, url);
            return FetchResult.Error;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning(
                    "[IMAGE_FETCH] Sha={Sha} Url={Url} StatusCode={Code}",
                    sha, url, (int)response.StatusCode);
                return FetchResult.Error;
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await blob.UploadAsync(
                    stream,
                    new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = "image/png" }
                    },
                    ct);
                return FetchResult.Downloaded;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[IMAGE_FETCH] Sha={Sha} Error=UploadFailed", sha);
                return FetchResult.Error;
            }
        }
    }

    static bool TryNormalizeSha(string raw, out string sha)
    {
        sha = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var trimmed = raw.Trim();

        // Strip URL form: anything ending with /img/{sha}.png
        var urlMatch = ImgUrlRegex().Match(trimmed);
        if (urlMatch.Success)
        {
            trimmed = urlMatch.Groups[1].Value;
        }

        if (HexRegex().IsMatch(trimmed))
        {
            sha = trimmed.ToLowerInvariant();
            return true;
        }
        return false;
    }

    enum FetchResult { Downloaded, Skipped, Error }

    [GeneratedRegex(@"/img/([0-9a-fA-F]{64})\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex ImgUrlRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexRegex();
}
