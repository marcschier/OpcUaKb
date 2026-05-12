using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// GitHub Fetcher — enumerates a repository tree at a specific tag and
// downloads filtered files (.xml, .xsd, .csv) into blob storage.
//
// Used to mirror the "Supplementary Files" referenced from
// reference.opcfoundation.org spec landing pages, which point at
// GitHub trees such as
//   https://github.com/OPCFoundation/UA-Nodeset/tree/UA-1.05.06-2025-11-08/Schema
//
// Idempotent: per-blob metadata "github_sha" is compared against the tree
// entry SHA so re-runs only download changed files.
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Describes a slice of a GitHub repository to mirror into blob storage.
/// Only descendants of <see cref="PathFilter"/> are downloaded.
/// Pass an empty <see cref="PathFilter"/> to mirror the entire tree.
/// </summary>
public sealed record GitHubRef(string Owner, string Repo, string Tag, string PathFilter);

/// <summary>
/// Mirrors filtered files (.xml, .xsd, .csv) from one or more GitHub
/// repository trees into the <c>opcua-content</c> blob container.
///
/// In production set <c>GITHUB_TOKEN</c> for the 5000/hr rate limit;
/// unauthenticated quota is 60/hr/IP.
/// </summary>
sealed class GitHubFetcher
{
    const string ContainerName = "opcua-content";
    const string ApiBaseUrl = "https://api.github.com";
    const string RawBaseUrl = "https://raw.githubusercontent.com";
    const int MaxConcurrency = 3;

    static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xsd", ".csv"
    };

    readonly HttpClient _http;
    readonly BlobContainerClient _container;
    readonly string? _token;
    readonly ILogger _log;
    readonly SemaphoreSlim _throttle = new(MaxConcurrency);

    public GitHubFetcher(HttpClient http, BlobServiceClient blobs, string? githubToken, ILogger log)
    {
        _http = http;
        _container = blobs.GetBlobContainerClient(ContainerName);
        _token = githubToken;
        _log = log;
    }

    /// <summary>
    /// Fetches all eligible files for each <see cref="GitHubRef"/>.
    /// Returns aggregate counters across all refs.
    /// </summary>
    public async Task<(int Downloaded, int Skipped, int Errors)> FetchAsync(
        IEnumerable<GitHubRef> refs, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

        int totalDownloaded = 0, totalSkipped = 0, totalErrors = 0;

        foreach (var gh in refs)
        {
            var (d, s, e) = await FetchRefAsync(gh, ct);
            totalDownloaded += d;
            totalSkipped += s;
            totalErrors += e;
        }

        return (totalDownloaded, totalSkipped, totalErrors);
    }

    async Task<(int Downloaded, int Skipped, int Errors)> FetchRefAsync(GitHubRef gh, CancellationToken ct)
    {
        var pathFilter = (gh.PathFilter ?? "").Trim('/');
        var treeUrl = $"{ApiBaseUrl}/repos/{gh.Owner}/{gh.Repo}/git/trees/{Uri.EscapeDataString(gh.Tag)}?recursive=1";

        JsonNode? root;
        try
        {
            root = await GetTreeAsync(treeUrl, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Error=tree-fetch-failed Message={Message}",
                gh.Owner, gh.Repo, gh.Tag, pathFilter, ex.Message);
            return (0, 0, 1);
        }

        if (root == null)
        {
            _log.LogWarning("[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Error=empty-tree",
                gh.Owner, gh.Repo, gh.Tag, pathFilter);
            return (0, 0, 1);
        }

        if (root["truncated"]?.GetValue<bool>() == true)
        {
            _log.LogWarning("[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Truncated=true — recursive tree exceeded GitHub's 100K-entry cap; some files will be missing",
                gh.Owner, gh.Repo, gh.Tag, pathFilter);
        }

        var tree = root["tree"]?.AsArray();
        if (tree == null)
        {
            _log.LogWarning("[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Error=no-tree-array",
                gh.Owner, gh.Repo, gh.Tag, pathFilter);
            return (0, 0, 1);
        }

        var candidates = new List<(string Path, string Sha)>();
        foreach (var entry in tree)
        {
            if (entry == null) continue;
            if (entry["type"]?.GetValue<string>() != "blob") continue;

            var path = entry["path"]?.GetValue<string>();
            if (string.IsNullOrEmpty(path)) continue;

            if (!MatchesPathFilter(path, pathFilter)) continue;

            var ext = Path.GetExtension(path);
            if (!AllowedExtensions.Contains(ext)) continue;

            var sha = entry["sha"]?.GetValue<string>() ?? "";
            candidates.Add((path, sha));
        }

        _log.LogInformation("[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Candidates={Count}",
            gh.Owner, gh.Repo, gh.Tag, pathFilter, candidates.Count);

        int downloaded = 0, skipped = 0, errors = 0;
        var tasks = candidates.Select(async c =>
        {
            await _throttle.WaitAsync(ct);
            try
            {
                var outcome = await DownloadOneAsync(gh, c.Path, c.Sha, ct);
                switch (outcome)
                {
                    case FetchOutcome.Downloaded: Interlocked.Increment(ref downloaded); break;
                    case FetchOutcome.Skipped:    Interlocked.Increment(ref skipped); break;
                    default:                      Interlocked.Increment(ref errors); break;
                }
            }
            finally
            {
                _throttle.Release();
            }
        });

        await Task.WhenAll(tasks);

        _log.LogInformation("[GITHUB_FETCH] Repo={Owner}/{Repo}@{Tag} Path={Path} Downloaded={Downloaded} Skipped={Skipped} Errors={Errors}",
            gh.Owner, gh.Repo, gh.Tag, pathFilter, downloaded, skipped, errors);

        return (downloaded, skipped, errors);
    }

    enum FetchOutcome { Downloaded, Skipped, Error }

    async Task<FetchOutcome> DownloadOneAsync(GitHubRef gh, string path, string sha, CancellationToken ct)
    {
        var blobName = BuildBlobName(gh, path);
        var blob = _container.GetBlobClient(blobName);

        try
        {
            if (await blob.ExistsAsync(ct))
            {
                var props = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
                if (props.Metadata.TryGetValue("github_sha", out var existing) &&
                    string.Equals(existing, sha, StringComparison.OrdinalIgnoreCase))
                {
                    return FetchOutcome.Skipped;
                }
            }

            // Build the raw URL. raw.githubusercontent.com accepts the original
            // path components and tolerates spaces, but each segment must be
            // URI-encoded except for the forward slashes.
            var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
            var rawUrl = $"{RawBaseUrl}/{gh.Owner}/{gh.Repo}/{Uri.EscapeDataString(gh.Tag)}/{encodedPath}";

            using var response = await RetryHelper.RetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, rawUrl);
                ApplyRawHeaders(req);
                return _http.SendAsync(req, ct);
            }, _log);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("[GITHUB_FETCH] Status={Status} Url={Url}",
                    (int)response.StatusCode, rawUrl);
                return FetchOutcome.Error;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            using var ms = new MemoryStream(bytes);
            await blob.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = ContentTypeFor(path)
                },
                Metadata = new Dictionary<string, string>
                {
                    ["github_sha"] = sha,
                    ["github_owner"] = gh.Owner,
                    ["github_repo"] = gh.Repo,
                    ["github_tag"] = gh.Tag,
                    ["github_path"] = path,
                }
            }, ct);

            return FetchOutcome.Downloaded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("[GITHUB_FETCH] Error={Error} Repo={Owner}/{Repo}@{Tag} Path={Path}",
                ex.Message, gh.Owner, gh.Repo, gh.Tag, path);
            return FetchOutcome.Error;
        }
    }

    async Task<JsonNode?> GetTreeAsync(string url, CancellationToken ct)
    {
        using var response = await RetryHelper.RetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyApiHeaders(req);
            return _http.SendAsync(req, ct);
        }, _log);

        LogRateLimit(response);

        if (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimitExhausted(response))
        {
            var reset = ParseRateLimitReset(response);
            var resetIso = reset?.ToString("o") ?? "unknown";
            throw new InvalidOperationException(
                $"GitHub rate limit exceeded; reset at {resetIso}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"GitHub tree request failed: {(int)response.StatusCode} {response.ReasonPhrase} — {Truncate(body, 500)}");
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(text);
    }

    void ApplyApiHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd("OpcUaKb-Crawler/1.0");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!string.IsNullOrEmpty(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    void ApplyRawHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd("OpcUaKb-Crawler/1.0");
        if (!string.IsNullOrEmpty(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    void LogRateLimit(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues))
            return;

        if (!int.TryParse(remainingValues.FirstOrDefault(), out var remaining))
            return;

        if (remaining < 10)
        {
            var reset = ParseRateLimitReset(response);
            _log.LogWarning("[GITHUB_FETCH] RateLimitRemaining={Remaining} ResetAt={Reset}",
                remaining, reset?.ToString("o") ?? "unknown");
        }
    }

    static bool IsRateLimitExhausted(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var values))
            return false;
        return int.TryParse(values.FirstOrDefault(), out var remaining) && remaining == 0;
    }

    static DateTimeOffset? ParseRateLimitReset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values))
            return null;
        if (!long.TryParse(values.FirstOrDefault(), out var epoch))
            return null;
        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    static bool MatchesPathFilter(string path, string pathFilter)
    {
        if (string.IsNullOrEmpty(pathFilter)) return true;
        if (string.Equals(path, pathFilter, StringComparison.Ordinal)) return true;
        return path.StartsWith(pathFilter + "/", StringComparison.Ordinal);
    }

    static string BuildBlobName(GitHubRef gh, string path)
    {
        // Normalize to forward slashes; tree paths are already POSIX-style
        // but be defensive about Windows separators.
        var normalized = path.Replace('\\', '/');
        return $"nodesets/{gh.Owner}-{gh.Repo}/{gh.Tag}/{normalized}";
    }

    static string ContentTypeFor(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xml" => "application/xml",
            ".xsd" => "application/xml",
            ".csv" => "text/csv",
            _      => "application/octet-stream",
        };
    }

    static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
