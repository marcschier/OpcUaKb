using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// UA-CloudLibrary Client — downloads NodeSet XMLs from the OPC Foundation
// Cloud Library REST API. Enabled only when credentials are provided.
// ═══════════════════════════════════════════════════════════════════════

sealed class CloudLibraryClient
{
    const string BaseUrl = "https://uacloudlibrary.opcfoundation.org";
    const string BlobPrefix = "cloudlib/";
    const string ContainerName = "opcua-content";
    const int PageSize = 100;
    const int DelayBetweenDownloadsMs = 500;
    const int MaxRetries = 5;

    readonly HttpClient _http;
    readonly BlobContainerClient _container;
    readonly ILogger _log;

    CloudLibraryClient(string username, string password, string storageConnectionString, ILogger logger)
    {
        _log = logger;
        _container = new BlobContainerClient(storageConnectionString, ContainerName);
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Creates a client if credentials are available, otherwise returns null.
    /// </summary>
    public static CloudLibraryClient? TryCreate(string storageConnectionString, ILogger logger)
    {
        var username = Environment.GetEnvironmentVariable("CLOUDLIB_USERNAME");
        var password = Environment.GetEnvironmentVariable("CLOUDLIB_PASSWORD");
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            logger.LogInformation("[CLOUDLIB] Skipping — CLOUDLIB_USERNAME/CLOUDLIB_PASSWORD not set");
            return null;
        }

        logger.LogInformation("[CLOUDLIB] Credentials found, CloudLibrary integration enabled");
        return new CloudLibraryClient(username, password, storageConnectionString, logger);
    }

    /// <summary>
    /// Downloads all NodeSet XMLs from the Cloud Library and stores them in blob storage.
    /// Returns the list of blob names for downstream parsing.
    /// </summary>
    public async Task<List<string>> DownloadAllNodeSetsAsync()
    {
        var blobNames = new List<string>();

        // Step 1: List all available NodeSets with pagination
        var allModels = new List<(string id, string namespaceUri, string title)>();
        int offset = 0;

        while (true)
        {
            var url = $"/infomodel/find?keywords=*&offset={offset}&limit={PageSize}";
            var response = await RetryRequestAsync(url);
            if (response == null) break;

            var json = JsonNode.Parse(response);
            var results = json?.AsArray();
            if (results == null || results.Count == 0) break;

            foreach (var item in results)
            {
                // API nests nodeset metadata under "nodeset" object
                var nodeset = item?["nodeset"];
                var identifier = nodeset?["identifier"]?.ToString();
                var nsUri = nodeset?["namespaceUri"]?.GetValue<string>() ?? "";
                var title = item?["title"]?.GetValue<string>() ?? nsUri;
                if (!string.IsNullOrEmpty(identifier))
                    allModels.Add((identifier, nsUri, title));
            }

            _log.LogInformation("[CLOUDLIB] Listed {Count} models (offset={Offset})",
                allModels.Count, offset);

            if (results.Count < PageSize) break;
            offset += PageSize;
        }

        _log.LogInformation("[CLOUDLIB] Total models found: {Count}", allModels.Count);

        // Step 2: Download each NodeSet XML
        int downloaded = 0, skipped = 0, errors = 0;

        foreach (var (id, nsUri, title) in allModels)
        {
            try
            {
                // Use the namespace URI hash as blob name to avoid duplicates
                var safeName = SanitizeBlobName(nsUri, id);
                var blobName = $"{BlobPrefix}{safeName}.xml";

                // Check if already downloaded (skip if recent)
                var blobClient = _container.GetBlobClient(blobName);
                if (await blobClient.ExistsAsync())
                {
                    var props = (await blobClient.GetPropertiesAsync()).Value;
                    if (props.LastModified > DateTimeOffset.UtcNow.AddDays(-7))
                    {
                        skipped++;
                        blobNames.Add(blobName);
                        continue;
                    }
                }

                // Download NodeSet XML
                var xml = await RetryRequestAsync($"/infomodel/download/{Uri.EscapeDataString(id)}?nodesetXMLOnly=true");
                if (xml == null)
                {
                    errors++;
                    continue;
                }

                // The response is either:
                // - A JSON string (quoted XML): "<?xml ..."
                // - A JSON object with nodeset.nodesetXml
                // - Raw XML: <?xml ...
                string nodesetXml;
                var trimmed = xml.TrimStart();
                if (trimmed.StartsWith('"'))
                {
                    // JSON-quoted string — unwrap
                    nodesetXml = JsonSerializer.Deserialize<string>(trimmed) ?? xml;
                }
                else if (trimmed.StartsWith('{'))
                {
                    // JSON object — extract nodesetXml field
                    var jsonResp = JsonNode.Parse(trimmed);
                    nodesetXml = jsonResp?["nodeset"]?["nodesetXml"]?.GetValue<string>() ?? xml;
                }
                else
                {
                    nodesetXml = xml;
                }

                // Upload to blob storage
                await blobClient.UploadAsync(
                    BinaryData.FromString(nodesetXml),
                    overwrite: true);

                blobNames.Add(blobName);
                downloaded++;

                if (downloaded % 20 == 0)
                    _log.LogInformation("[CLOUDLIB] Downloaded={D} Skipped={S} Errors={E} Total={T}",
                        downloaded, skipped, errors, allModels.Count);

                // Rate limit
                await Task.Delay(DelayBetweenDownloadsMs);
            }
            catch (Exception ex)
            {
                errors++;
                _log.LogWarning("[CLOUDLIB] Error downloading {Id}: {Error}", id, ex.Message);
            }
        }

        _log.LogInformation("[CLOUDLIB] Completed: Downloaded={D} Skipped={S} Errors={E}",
            downloaded, skipped, errors);

        return blobNames;
    }

    async Task<string?> RetryRequestAsync(string url)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var response = await _http.GetAsync(url);

                if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                    response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    var delay = ComputeRetryDelay(response, attempt);
                    _log.LogWarning("[CLOUDLIB] {Status} on {Url}, retrying in {Delay}s",
                        response.StatusCode, url, delay.TotalSeconds);
                    await Task.Delay(delay);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("[CLOUDLIB] {Status} on {Url}", response.StatusCode, url);
                    return null;
                }

                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (attempt < MaxRetries - 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _log.LogWarning("[CLOUDLIB] Request error on {Url}: {Error}, retrying in {Delay}s",
                    url, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay);
            }
        }

        _log.LogWarning("[CLOUDLIB] Failed after {MaxRetries} retries: {Url}", MaxRetries, url);
        return null;
    }

    static TimeSpan ComputeRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var secs)) return TimeSpan.FromSeconds(secs);
        }
        var baseSec = Math.Pow(2, attempt + 1);
        var jitter = baseSec * (0.75 + Random.Shared.NextDouble() * 0.5);
        return TimeSpan.FromSeconds(jitter);
    }

    static string SanitizeBlobName(string nsUri, string id)
    {
        // Use namespace URI to create a readable blob name, fall back to ID
        if (!string.IsNullOrEmpty(nsUri))
        {
            var name = nsUri
                .Replace("http://", "").Replace("https://", "")
                .Replace("opcfoundation.org/UA/", "")
                .TrimEnd('/')
                .Replace('/', '_').Replace('\\', '_')
                .Replace(':', '_').Replace(' ', '_');
            if (name.Length > 100) name = name[..100];
            return name;
        }
        return id.Replace('/', '_').Replace('\\', '_');
    }
}
