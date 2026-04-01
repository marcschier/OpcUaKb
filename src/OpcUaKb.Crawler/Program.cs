using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    });
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("Crawler");

var connectionString = Environment.GetEnvironmentVariable("STORAGE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    logger.LogCritical("STORAGE_CONNECTION_STRING environment variable is not set.");
    logger.LogInformation(
        "Set it via: az storage account show-connection-string " +
        "--name opcuakbstorage --resource-group <rg> --query connectionString -o tsv");
    return 1;
}

using var crawler = new Crawler(connectionString, loggerFactory.CreateLogger<Crawler>());
await crawler.RunAsync();
return 0;

// ─── Crawl state ────────────────────────────────────────────────────────────

sealed class CrawlState
{
    public Dictionary<string, DateTimeOffset> CrawledUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldRecrawl(string url)
    {
        if (!CrawledUrls.TryGetValue(url, out var lastCrawled))
            return true;
        return DateTimeOffset.UtcNow - lastCrawled > TimeSpan.FromHours(Crawler.RecrawlThresholdHours);
    }

    public void MarkCrawled(string url) => CrawledUrls[url] = DateTimeOffset.UtcNow;
}

// ─── Main crawler ───────────────────────────────────────────────────────────

sealed class Crawler : IDisposable
{
    public const string BaseUrl = "https://reference.opcfoundation.org/";
    public const string AllowedHost = "reference.opcfoundation.org";
    public const string ContainerName = "opcua-content";
    public const string CrawlStateBlob = "_crawl-state.json";
    public const int MaxConcurrency = 5;
    public const int DelayBetweenRequestsMs = 200;
    public const int RecrawlThresholdHours = 24;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> s_imageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico" };

    private readonly BlobContainerClient _container;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _throttle = new(MaxConcurrency, MaxConcurrency);
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private CrawlState _state = new();
    private int _downloaded;
    private int _skipped;
    private int _errors;

    public Crawler(string connectionString, ILogger logger)
    {
        _logger = logger;
        _container = new BlobContainerClient(connectionString, ContainerName);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "OpcUaKb-Crawler/1.0 (+https://github.com/opcuakb)");
        _http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task RunAsync()
    {
        _logger.LogInformation("Starting OPC UA Knowledge Base crawler");
        _logger.LogInformation("Target: {BaseUrl}", BaseUrl);

        await _container.CreateIfNotExistsAsync();
        await LoadStateAsync();

        _logger.LogInformation(
            "Loaded crawl state: {Count} previously crawled URLs",
            _state.CrawledUrls.Count);

        var queue = new ConcurrentQueue<CrawlItem>();
        EnqueueUrl(queue, BaseUrl, isPage: true);

        while (!queue.IsEmpty)
        {
            var batch = DequeueBatch(queue, MaxConcurrency * 2);
            await Task.WhenAll(batch.Select(item => ProcessItemAsync(item, queue)));
        }

        await SaveStateAsync();

        _logger.LogInformation(
            "Crawl complete. Downloaded: {Downloaded}, Skipped: {Skipped}, Errors: {Errors}",
            _downloaded, _skipped, _errors);
    }

    private static List<CrawlItem> DequeueBatch(ConcurrentQueue<CrawlItem> queue, int max)
    {
        var batch = new List<CrawlItem>(max);
        while (batch.Count < max && queue.TryDequeue(out var item))
            batch.Add(item);
        return batch;
    }

    private async Task ProcessItemAsync(CrawlItem item, ConcurrentQueue<CrawlItem> queue)
    {
        await _throttle.WaitAsync();
        try
        {
            await Task.Delay(DelayBetweenRequestsMs);

            if (!_state.ShouldRecrawl(item.Url))
            {
                Interlocked.Increment(ref _skipped);
                _logger.LogDebug("Skipping (recently crawled): {Url}", item.Url);
                return;
            }

            var (content, contentType) = await DownloadAsync(item.Url);
            if (content is null) return;

            var blobPath = UrlToBlobPath(item.Url, contentType);
            await UploadBlobAsync(blobPath, content, contentType);
            _state.MarkCrawled(item.Url);
            Interlocked.Increment(ref _downloaded);

            _logger.LogInformation("[{Count}] {Url} -> {Blob} ({Size})",
                _downloaded, item.Url, blobPath, FormatSize(content.Length));

            if (item.IsPage && IsHtmlContent(contentType))
            {
                var links = await ExtractLinksAsync(content, item.Url);
                foreach (var link in links)
                    EnqueueUrl(queue, link.Url, link.IsPage);
            }

            if (_downloaded % 50 == 0)
                await SaveStateAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            _logger.LogError(ex, "Error processing {Url}", item.Url);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<(byte[]? Content, string ContentType)> DownloadAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HTTP {Status} for {Url}", (int)response.StatusCode, url);
                Interlocked.Increment(ref _errors);
                return (null, string.Empty);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType
                              ?? "application/octet-stream";
            var content = await response.Content.ReadAsByteArrayAsync();
            return (content, contentType);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Timeout downloading {Url}", url);
            Interlocked.Increment(ref _errors);
            return (null, string.Empty);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("HTTP error for {Url}: {Message}", url, ex.Message);
            Interlocked.Increment(ref _errors);
            return (null, string.Empty);
        }
    }

    // ── Link extraction ─────────────────────────────────────────────────────

    private async Task<List<CrawlLink>> ExtractLinksAsync(byte[] htmlBytes, string pageUrl)
    {
        var links = new List<CrawlLink>();
        try
        {
            var html = Encoding.UTF8.GetString(htmlBytes);
            var context = BrowsingContext.New(Configuration.Default);
            var parser = context.GetService<IHtmlParser>()!;
            var document = await parser.ParseDocumentAsync(html);

            ExtractAnchors(document, pageUrl, links);
            ExtractResources(document, pageUrl, links);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error parsing links from {Url}: {Message}", pageUrl, ex.Message);
        }
        return links;
    }

    private static void ExtractAnchors(IDocument document, string pageUrl, List<CrawlLink> links)
    {
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = anchor.GetAttribute("href");
            if (TryResolveLink(href, pageUrl, out var resolved))
            {
                var ext = GetExtension(resolved);
                links.Add(new CrawlLink(resolved, IsPage: !IsAssetExtension(ext)));
            }
        }
    }

    private static void ExtractResources(IDocument document, string pageUrl, List<CrawlLink> links)
    {
        // Images
        foreach (var img in document.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src");
            if (TryResolveLink(src, pageUrl, out var resolved))
                links.Add(new CrawlLink(resolved, IsPage: false));
        }

        // Responsive images
        foreach (var img in document.QuerySelectorAll("img[srcset]"))
        {
            var srcset = img.GetAttribute("srcset");
            if (srcset is null) continue;
            foreach (var entry in srcset.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var src = entry.Trim().Split(' ')[0];
                if (TryResolveLink(src, pageUrl, out var resolved))
                    links.Add(new CrawlLink(resolved, IsPage: false));
            }
        }

        // Stylesheets, icons, etc.
        foreach (var link in document.QuerySelectorAll("link[href]"))
        {
            var href = link.GetAttribute("href");
            if (TryResolveLink(href, pageUrl, out var resolved))
                links.Add(new CrawlLink(resolved, IsPage: false));
        }

        // Scripts
        foreach (var script in document.QuerySelectorAll("script[src]"))
        {
            var src = script.GetAttribute("src");
            if (TryResolveLink(src, pageUrl, out var resolved))
                links.Add(new CrawlLink(resolved, IsPage: false));
        }

        // Audio/video sources
        foreach (var source in document.QuerySelectorAll("source[src]"))
        {
            var src = source.GetAttribute("src");
            if (TryResolveLink(src, pageUrl, out var resolved))
                links.Add(new CrawlLink(resolved, IsPage: false));
        }
    }

    // ── URL helpers ─────────────────────────────────────────────────────────

    private static bool TryResolveLink(string? href, string baseUrl, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(href)) return false;

        if (href.StartsWith('#')
            || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(new Uri(baseUrl), href, out var uri))
            return false;

        if (!uri.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
            return false;

        resolved = uri.GetLeftPart(UriPartial.Query);
        return true;
    }

    private void EnqueueUrl(ConcurrentQueue<CrawlItem> queue, string url, bool isPage)
    {
        var normalized = url.TrimEnd('/');
        // Keep the root URL with its trailing slash
        if (string.IsNullOrEmpty(new Uri(url).AbsolutePath.TrimStart('/')))
            normalized = url;

        if (_queued.TryAdd(normalized, 0))
            queue.Enqueue(new CrawlItem(normalized, isPage));
    }

    internal static string UrlToBlobPath(string url, string contentType)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath.TrimStart('/');

        if (string.IsNullOrEmpty(path))
            path = "index.html";
        else if (path.EndsWith('/'))
            path += "index.html";
        else if (!Path.HasExtension(path) && IsHtmlContent(contentType))
            path += "/index.html";

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var query = uri.Query.TrimStart('?').Replace('&', '_').Replace('=', '-');
            var ext = Path.GetExtension(path);
            if (!string.IsNullOrEmpty(ext))
                path = path[..^ext.Length] + "_" + query + ext;
            else
                path += "_" + query;
        }

        return path;
    }

    // ── Blob storage ────────────────────────────────────────────────────────

    private async Task UploadBlobAsync(string blobPath, byte[] content, string contentType)
    {
        var blobClient = _container.GetBlobClient(blobPath);
        using var stream = new MemoryStream(content);
        var headers = new BlobHttpHeaders { ContentType = MapContentType(contentType, blobPath) };
        await blobClient.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers });
    }

    private async Task LoadStateAsync()
    {
        try
        {
            var blobClient = _container.GetBlobClient(CrawlStateBlob);
            if (await blobClient.ExistsAsync())
            {
                var response = await blobClient.DownloadContentAsync();
                _state = response.Value.Content.ToObjectFromJson<CrawlState>(s_jsonOptions)
                         ?? new CrawlState();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not load crawl state, starting fresh: {Message}", ex.Message);
            _state = new CrawlState();
        }
    }

    private async Task SaveStateAsync()
    {
        try
        {
            var blobClient = _container.GetBlobClient(CrawlStateBlob);
            var json = JsonSerializer.SerializeToUtf8Bytes(_state, s_jsonOptions);
            using var stream = new MemoryStream(json);
            var headers = new BlobHttpHeaders { ContentType = "application/json" };
            await blobClient.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers });
            _logger.LogInformation("Saved crawl state ({Count} URLs)", _state.CrawledUrls.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to save crawl state: {Message}", ex.Message);
        }
    }

    // ── Classification helpers ──────────────────────────────────────────────

    private static bool IsHtmlContent(string contentType) =>
        contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssetExtension(string extension) =>
        s_imageExtensions.Contains(extension)
        || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".css", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".js", StringComparison.OrdinalIgnoreCase);

    private static string GetExtension(string url)
    {
        try { return Path.GetExtension(new Uri(url).AbsolutePath); }
        catch { return string.Empty; }
    }

    private static string MapContentType(string contentType, string blobPath)
    {
        if (!string.IsNullOrEmpty(contentType) && contentType != "application/octet-stream")
            return contentType;

        return Path.GetExtension(blobPath).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".ico" => "image/x-icon",
            ".xml" => "application/xml",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream"
        };
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}

// ─── Supporting records ─────────────────────────────────────────────────────

record CrawlItem(string Url, bool IsPage);
record CrawlLink(string Url, bool IsPage);
