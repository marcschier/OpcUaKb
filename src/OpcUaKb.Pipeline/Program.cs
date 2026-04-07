using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base Pipeline — Crawl + Index
// Designed to run as an Azure Container Apps scheduled job.
// Emits structured JSON telemetry for Log Analytics dashboard.
// ═══════════════════════════════════════════════════════════════════════

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddJsonConsole(o =>
    {
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        o.UseUtcTimestamp = true;
    });
    b.SetMinimumLevel(LogLevel.Information);
});
var log = loggerFactory.CreateLogger("Pipeline");

var storageConnStr = Require("STORAGE_CONNECTION_STRING");
var searchEndpoint = Require("SEARCH_ENDPOINT");
var searchApiKey   = Require("SEARCH_API_KEY");
var aoaiEndpoint   = Require("AOAI_ENDPOINT");
var aoaiApiKey     = Require("AOAI_API_KEY");

var statusTracker = new PipelineStatusTracker(
    new BlobContainerClient(storageConnStr, "opcua-content"),
    loggerFactory.CreateLogger<PipelineStatusTracker>());

var sw = Stopwatch.StartNew();
var exitCode = 0;

try
{
    // ── Phase 1: Crawl ─────────────────────────────────────────────────
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "crawl", "started");
    await statusTracker.UpdateAsync("crawl", "running");

    var crawler = new OpcUaCrawler(storageConnStr, loggerFactory.CreateLogger<OpcUaCrawler>(), statusTracker);
    await crawler.RunAsync();

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} ElapsedSec={Elapsed}",
        "crawl", "completed", (int)sw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("crawl", "completed", elapsedSec: (int)sw.Elapsed.TotalSeconds);

    // ── Phase 2: Index ─────────────────────────────────────────────────
    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status}", "index", "started");
    await statusTracker.UpdateAsync("index", "running");

    var indexSw = Stopwatch.StartNew();
    var indexer = new OpcUaIndexer(
        storageConnStr, searchEndpoint, searchApiKey,
        aoaiEndpoint, aoaiApiKey,
        loggerFactory.CreateLogger<OpcUaIndexer>(), statusTracker);
    await indexer.RunAsync();

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} ElapsedSec={Elapsed}",
        "index", "completed", (int)indexSw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("index", "completed",
        elapsedSec: (int)indexSw.Elapsed.TotalSeconds);

    log.LogInformation("[PIPELINE] Phase={Phase} Status={Status} TotalElapsedSec={Elapsed}",
        "pipeline", "completed", (int)sw.Elapsed.TotalSeconds);
    await statusTracker.UpdateAsync("pipeline", "completed",
        elapsedSec: (int)sw.Elapsed.TotalSeconds);
}
catch (Exception ex)
{
    log.LogError(ex, "[PIPELINE] Phase={Phase} Status={Status} Error={Error}",
        statusTracker.CurrentPhase, "failed", ex.Message);
    await statusTracker.UpdateAsync(statusTracker.CurrentPhase, "failed", error: ex.Message);
    exitCode = 1;
}

return exitCode;

string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing environment variable: {name}");

// ═══════════════════════════════════════════════════════════════════════
// Pipeline Status Tracker — writes _pipeline-status.json to blob storage
// ═══════════════════════════════════════════════════════════════════════

sealed class PipelineStatusTracker
{
    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    readonly BlobContainerClient _container;
    readonly ILogger _log;
    PipelineStatus _status = new();

    public string CurrentPhase => _status.CurrentPhase;

    public PipelineStatusTracker(BlobContainerClient container, ILogger logger)
    {
        _container = container;
        _log = logger;
    }

    public async Task UpdateAsync(string phase, string status,
        int? downloaded = null, int? skipped = null, int? errors = null,
        int? queued = null, int? htmlBlobs = null, int? chunks = null,
        int? embedded = null, int? indexed = null,
        int? elapsedSec = null, string? error = null)
    {
        _status.CurrentPhase = phase;
        _status.Status = status;
        _status.LastUpdated = DateTimeOffset.UtcNow;
        if (downloaded.HasValue) _status.CrawlDownloaded = downloaded.Value;
        if (skipped.HasValue) _status.CrawlSkipped = skipped.Value;
        if (errors.HasValue) _status.CrawlErrors = errors.Value;
        if (queued.HasValue) _status.CrawlQueued = queued.Value;
        if (htmlBlobs.HasValue) _status.IndexHtmlBlobs = htmlBlobs.Value;
        if (chunks.HasValue) _status.IndexChunks = chunks.Value;
        if (embedded.HasValue) _status.IndexEmbedded = embedded.Value;
        if (indexed.HasValue) _status.IndexUploaded = indexed.Value;
        if (elapsedSec.HasValue) _status.ElapsedSeconds = elapsedSec.Value;
        if (error != null) _status.LastError = error;

        try
        {
            await _container.CreateIfNotExistsAsync();
            var blob = _container.GetBlobClient("_pipeline-status.json");
            var json = JsonSerializer.SerializeToUtf8Bytes(_status, s_json);
            using var ms = new MemoryStream(json);
            await blob.UploadAsync(ms, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not write status: {Msg}", ex.Message);
        }
    }
}

sealed class PipelineStatus
{
    public string CurrentPhase { get; set; } = "init";
    public string Status { get; set; } = "pending";
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    public int ElapsedSeconds { get; set; }
    public int CrawlDownloaded { get; set; }
    public int CrawlSkipped { get; set; }
    public int CrawlErrors { get; set; }
    public int CrawlQueued { get; set; }
    public int IndexHtmlBlobs { get; set; }
    public int IndexChunks { get; set; }
    public int IndexEmbedded { get; set; }
    public int IndexUploaded { get; set; }
    public string? LastError { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════
// Crawler
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaCrawler : IDisposable
{
    const string BaseUrl = "https://reference.opcfoundation.org/";
    const string AllowedHost = "reference.opcfoundation.org";
    const string ContainerName = "opcua-content";
    const string CrawlStateBlob = "_crawl-state.json";
    const int MaxConcurrency = 5;
    const int DelayMs = 200;
    const int RecrawlHours = 24;

    static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    static readonly HashSet<string> s_imgExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico" };

    readonly BlobContainerClient _container;
    readonly HttpClient _http;
    readonly SemaphoreSlim _throttle = new(MaxConcurrency);
    readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.OrdinalIgnoreCase);
    readonly ILogger _log;
    readonly PipelineStatusTracker _tracker;
    Dictionary<string, DateTimeOffset> _crawled = new(StringComparer.OrdinalIgnoreCase);
    int _downloaded, _skipped, _errors;

    public OpcUaCrawler(string connectionString, ILogger logger, PipelineStatusTracker tracker)
    {
        _log = logger;
        _tracker = tracker;
        _container = new BlobContainerClient(connectionString, ContainerName);
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = MaxConcurrency,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpcUaKb-Crawler/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
    }

    public async Task RunAsync()
    {
        await _container.CreateIfNotExistsAsync();
        await LoadStateAsync();
        _log.LogInformation("Crawl state: {Count} previously crawled URLs", _crawled.Count);

        var queue = new ConcurrentQueue<(string url, bool isPage)>();
        Enqueue(queue, BaseUrl, true);

        while (!queue.IsEmpty)
        {
            var batch = new List<(string url, bool isPage)>();
            while (batch.Count < MaxConcurrency * 2 && queue.TryDequeue(out var item))
                batch.Add(item);

            await Task.WhenAll(batch.Select(item => ProcessAsync(item.url, item.isPage, queue)));

            if (_downloaded > 0 && _downloaded % 50 == 0)
                await SaveStateAsync();
        }

        await SaveStateAsync();
        _log.LogInformation("[CRAWL] Status=completed Downloaded={D} Skipped={S} Errors={E}",
            _downloaded, _skipped, _errors);
        await _tracker.UpdateAsync("crawl", "completed",
            downloaded: _downloaded, skipped: _skipped, errors: _errors, queued: 0);
    }

    async Task ProcessAsync(string url, bool isPage, ConcurrentQueue<(string, bool)> queue)
    {
        await _throttle.WaitAsync();
        try
        {
            if (_crawled.TryGetValue(url, out var last) &&
                DateTimeOffset.UtcNow - last < TimeSpan.FromHours(RecrawlHours))
            {
                Interlocked.Increment(ref _skipped);
                return;
            }

            await Task.Delay(DelayMs);
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("HTTP {Code} for {Url}", (int)response.StatusCode, url);
                Interlocked.Increment(ref _errors);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var blobName = UrlToBlobName(url, response.Content.Headers.ContentType?.MediaType);
            var blobClient = _container.GetBlobClient(blobName);
            using var ms = new MemoryStream(bytes);
            await blobClient.UploadAsync(ms, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream"
                }
            });

            _crawled[url] = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _downloaded);

            if (isPage && (response.Content.Headers.ContentType?.MediaType?.Contains("html") == true))
            {
                var html = Encoding.UTF8.GetString(bytes);
                ExtractLinks(html, url, queue);
            }

            if (_downloaded % 25 == 0)
            {
                _log.LogInformation("[CRAWL] Downloaded={D} Skipped={S} Errors={E} Queued={Q}",
                    _downloaded, _skipped, _errors, queue.Count);
                if (_downloaded % 100 == 0)
                    await _tracker.UpdateAsync("crawl", "running",
                        downloaded: _downloaded, skipped: _skipped,
                        errors: _errors, queued: queue.Count);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("[CRAWL] Error={Error} Url={Url}", ex.Message, url);
            Interlocked.Increment(ref _errors);
        }
        finally
        {
            _throttle.Release();
        }
    }

    void ExtractLinks(string html, string baseUrl, ConcurrentQueue<(string, bool)> queue)
    {
        try
        {
            var parser = new HtmlParser();
            var doc = parser.ParseDocument(html);
            var baseUri = new Uri(baseUrl);

            foreach (var a in doc.QuerySelectorAll("a[href]"))
            {
                var href = a.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("javascript:"))
                    continue;
                if (Uri.TryCreate(baseUri, href, out var resolved) &&
                    resolved.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
                    Enqueue(queue, resolved.GetLeftPart(UriPartial.Path), true);
            }

            foreach (var img in doc.QuerySelectorAll("img[src], link[href], script[src]"))
            {
                var src = img.GetAttribute("src") ?? img.GetAttribute("href");
                if (string.IsNullOrWhiteSpace(src)) continue;
                if (Uri.TryCreate(baseUri, src, out var resolved) &&
                    resolved.Host.Equals(AllowedHost, StringComparison.OrdinalIgnoreCase))
                    Enqueue(queue, resolved.GetLeftPart(UriPartial.Query), false);
            }
        }
        catch { /* non-fatal */ }
    }

    void Enqueue(ConcurrentQueue<(string, bool)> queue, string url, bool isPage)
    {
        if (_queued.TryAdd(url, 0))
            queue.Enqueue((url, isPage));
    }

    static string UrlToBlobName(string url, string? contentType)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(path) || path.EndsWith('/'))
            path += "index.html";
        return path;
    }

    async Task LoadStateAsync()
    {
        try
        {
            var blob = _container.GetBlobClient(CrawlStateBlob);
            if (await blob.ExistsAsync())
            {
                var dl = await blob.DownloadContentAsync();
                var state = dl.Value.Content.ToObjectFromJson<Dictionary<string, DateTimeOffset>>(s_json);
                if (state != null) _crawled = new Dictionary<string, DateTimeOffset>(state, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { _log.LogWarning("Could not load crawl state: {Msg}", ex.Message); }
    }

    async Task SaveStateAsync()
    {
        try
        {
            var blob = _container.GetBlobClient(CrawlStateBlob);
            var json = JsonSerializer.SerializeToUtf8Bytes(_crawled, s_json);
            using var ms = new MemoryStream(json);
            await blob.UploadAsync(ms, overwrite: true);
        }
        catch (Exception ex) { _log.LogWarning("Could not save crawl state: {Msg}", ex.Message); }
    }

    public void Dispose() => _http.Dispose();
}

// ═══════════════════════════════════════════════════════════════════════
// Indexer
// ═══════════════════════════════════════════════════════════════════════

sealed class OpcUaIndexer
{
    const string IndexName = "opcua-content-index";
    const string ContainerName = "opcua-content";
    const string EmbeddingDeployment = "text-embedding-3-large";
    const int EmbeddingDimensions = 3072;
    const int ChunkSize = 512;
    const int ChunkOverlap = 50;
    const int EmbeddingBatchSize = 16;
    const int UploadBatchSize = 100;

    readonly SearchIndexClient _indexClient;
    readonly SearchClient _searchClient;
    readonly BlobContainerClient _container;
    readonly HttpClient _http;
    readonly string _aoaiEndpoint;
    readonly ILogger _log;
    readonly PipelineStatusTracker _tracker;

    public OpcUaIndexer(string storageConn, string searchEndpoint, string searchApiKey,
        string aoaiEndpoint, string aoaiApiKey, ILogger logger, PipelineStatusTracker tracker)
    {
        _log = logger;
        _tracker = tracker;
        _aoaiEndpoint = aoaiEndpoint;
        _indexClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey));
        _searchClient = _indexClient.GetSearchClient(IndexName);
        _container = new BlobContainerClient(storageConn, ContainerName);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("api-key", aoaiApiKey);
    }

    public async Task RunAsync()
    {
        await EnsureIndexAsync();
        _log.LogInformation("[INDEX] Status=index_ready Index={Name}", IndexName);

        var htmlBlobs = new List<string>();
        await foreach (var item in _container.GetBlobsAsync())
        {
            if (item.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                htmlBlobs.Add(item.Name);
        }
        _log.LogInformation("[INDEX] HtmlBlobs={Count}", htmlBlobs.Count);
        await _tracker.UpdateAsync("index", "running", htmlBlobs: htmlBlobs.Count);
        if (htmlBlobs.Count == 0) return;

        var allDocs = new List<SearchDocument>();
        var parser = new HtmlParser();
        int processed = 0;

        foreach (var blobName in htmlBlobs)
        {
            processed++;
            try
            {
                var dl = await _container.GetBlobClient(blobName).DownloadContentAsync();
                var html = dl.Value.Content.ToString();
                var sourceUrl = $"https://reference.opcfoundation.org/{blobName.Replace('\\', '/')}";
                var (part, ver) = ExtractSpecInfo(blobName);
                var doc = await parser.ParseDocumentAsync(html);
                allDocs.AddRange(ChunkDocument(doc, part, ver, sourceUrl));

                if (processed % 50 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=chunking Parsed={N} Total={T} Chunks={C}",
                        processed, htmlBlobs.Count, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running",
                        htmlBlobs: htmlBlobs.Count, chunks: allDocs.Count);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=chunking Error={Error} Blob={Blob}", ex.Message, blobName);
            }
        }
        _log.LogInformation("[INDEX] Phase=chunking Status=completed Chunks={Count}", allDocs.Count);
        await _tracker.UpdateAsync("index", "running", chunks: allDocs.Count);

        // Embeddings
        _log.LogInformation("[INDEX] Phase=embedding Total={Count}", allDocs.Count);
        var sem = new SemaphoreSlim(5);
        int embDone = 0;
        for (int i = 0; i < allDocs.Count; i += EmbeddingBatchSize)
        {
            var batch = allDocs.Skip(i).Take(EmbeddingBatchSize).ToList();
            await sem.WaitAsync();
            try
            {
                var texts = batch.Select(d => (string)d["page_chunk"]).ToList();
                var vectors = await GetEmbeddingsAsync(texts);
                for (int j = 0; j < batch.Count && j < vectors.Count; j++)
                    batch[j]["page_chunk_vector"] = vectors[j];
                embDone += batch.Count;
                if (embDone % 100 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=embedding Embedded={N} Total={T}", embDone, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running", embedded: embDone);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=embedding Error={Error} BatchStart={I}", ex.Message, i);
            }
            finally { sem.Release(); }
        }
        _log.LogInformation("[INDEX] Phase=embedding Status=completed Embedded={N}", embDone);
        await _tracker.UpdateAsync("index", "running", embedded: embDone);

        // Upload
        _log.LogInformation("[INDEX] Phase=upload Total={Count}", allDocs.Count);
        int uploaded = 0;
        for (int i = 0; i < allDocs.Count; i += UploadBatchSize)
        {
            var batch = allDocs.Skip(i).Take(UploadBatchSize).ToList();
            try
            {
                await _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch));
                uploaded += batch.Count;
                if (uploaded % 500 == 0)
                {
                    _log.LogInformation("[INDEX] Phase=upload Uploaded={N} Total={T}", uploaded, allDocs.Count);
                    await _tracker.UpdateAsync("index", "running", indexed: uploaded);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("[INDEX] Phase=upload Error={Error} BatchStart={I}", ex.Message, i);
            }
        }
        _log.LogInformation("[INDEX] Phase=upload Status=completed Indexed={N}", uploaded);
        await _tracker.UpdateAsync("index", "completed", indexed: uploaded);
    }

    async Task EnsureIndexAsync()
    {
        var index = new SearchIndex(IndexName)
        {
            Description = "OPC UA reference specification content chunks with vector embeddings.",
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("page_chunk") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchField("page_chunk_vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true, VectorSearchDimensions = EmbeddingDimensions, VectorSearchProfileName = "hnsw-embedding"
                },
                new SimpleField("source_url", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("spec_part", SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField("spec_version", SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("section_title"),
                new SimpleField("content_type", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("chunk_index", SearchFieldDataType.Int32) { IsSortable = true },
            },
            SemanticSearch = new SemanticSearch
            {
                DefaultConfigurationName = "semantic_config",
                Configurations =
                {
                    new SemanticConfiguration("semantic_config", new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField("section_title"),
                        ContentFields = { new SemanticField("page_chunk") }
                    })
                }
            },
            VectorSearch = new VectorSearch
            {
                Algorithms = { new HnswAlgorithmConfiguration("alg") { Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine } } },
                Profiles = { new VectorSearchProfile("hnsw-embedding", "alg") { VectorizerName = "aoai-vectorizer" } },
                Vectorizers =
                {
                    new AzureOpenAIVectorizer("aoai-vectorizer")
                    {
                        Parameters = new AzureOpenAIVectorizerParameters
                        {
                            ResourceUri = new Uri(_aoaiEndpoint),
                            DeploymentName = EmbeddingDeployment,
                            ModelName = EmbeddingDeployment
                        }
                    }
                }
            }
        };
        await _indexClient.CreateOrUpdateIndexAsync(index);
    }

    List<SearchDocument> ChunkDocument(AngleSharp.Dom.IDocument doc, string specPart, string specVersion, string sourceUrl)
    {
        var results = new List<SearchDocument>();
        var body = doc.Body;
        if (body == null) return results;

        string currentHeading = doc.Title ?? specPart;
        var blocks = new List<(string heading, string text, string type)>();

        foreach (var el in body.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,pre,code,li,td,th,figcaption,dt,dd"))
        {
            var tag = el.TagName.ToLowerInvariant();
            if (tag.StartsWith('h') && tag.Length == 2) { currentHeading = el.TextContent.Trim(); continue; }
            var text = el.TextContent.Trim();
            if (text.Length < 10) continue;
            blocks.Add((currentHeading, text, tag is "td" or "th" ? "table" : "text"));
        }

        foreach (var table in body.QuerySelectorAll("table"))
        {
            var heading = table.PreviousElementSibling?.TagName.StartsWith("H") == true
                ? table.PreviousElementSibling.TextContent.Trim() : currentHeading;
            var md = TableToMarkdown(table);
            if (md.Length > 20) blocks.Add((heading, md, "table"));
        }

        foreach (var img in body.QuerySelectorAll("img[src]"))
        {
            var alt = img.GetAttribute("alt") ?? "";
            var src = img.GetAttribute("src") ?? "";
            var cap = img.Closest("figure")?.QuerySelector("figcaption")?.TextContent ?? "";
            var ctx = $"[Image: {alt}] {cap} (src: {src})";
            if (ctx.Length > 20) blocks.Add((currentHeading, ctx, "image"));
        }

        int chunkIdx = 0;
        var buf = new StringBuilder();
        string bufHeading = currentHeading, bufType = "text";

        foreach (var (heading, text, type) in blocks)
        {
            if (buf.Length / 4 + text.Length / 4 > ChunkSize && buf.Length > 0)
            {
                results.Add(MakeDoc(buf.ToString(), bufHeading, bufType, sourceUrl, specPart, specVersion, chunkIdx++));
                var words = buf.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                buf.Clear();
                if (words.Length > ChunkOverlap)
                    buf.Append(string.Join(' ', words[^ChunkOverlap..]));
            }
            bufHeading = heading; bufType = type;
            if (buf.Length > 0) buf.Append('\n');
            buf.Append(text);
        }
        if (buf.Length > 0)
            results.Add(MakeDoc(buf.ToString(), bufHeading, bufType, sourceUrl, specPart, specVersion, chunkIdx));

        return results;
    }

    static SearchDocument MakeDoc(string text, string heading, string contentType,
        string sourceUrl, string specPart, string specVersion, int chunkIdx)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceUrl}:{chunkIdx}")))[..32].ToLowerInvariant();
        return new SearchDocument(new Dictionary<string, object>
        {
            ["id"] = id, ["page_chunk"] = text, ["source_url"] = sourceUrl,
            ["spec_part"] = specPart, ["spec_version"] = specVersion,
            ["section_title"] = heading, ["content_type"] = contentType, ["chunk_index"] = chunkIdx,
        });
    }

    async Task<List<float[]>> GetEmbeddingsAsync(List<string> texts)
    {
        var body = JsonSerializer.Serialize(new { input = texts, model = EmbeddingDeployment });
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_aoaiEndpoint}/openai/deployments/{EmbeddingDeployment}/embeddings?api-version=2024-06-01")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        return json["data"]!.AsArray()
            .Select(d => d!["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray())
            .ToList();
    }

    static (string part, string version) ExtractSpecInfo(string blobName)
    {
        var p = Regex.Match(blobName, @"Part(\d+)", RegexOptions.IgnoreCase);
        var v = Regex.Match(blobName, @"(v\d+)", RegexOptions.IgnoreCase);
        return (p.Success ? $"Part{p.Groups[1].Value}" : "Unknown",
                v.Success ? v.Groups[1].Value : "Unknown");
    }

    static string TableToMarkdown(IElement table)
    {
        var sb = new StringBuilder();
        foreach (var row in table.QuerySelectorAll("tr"))
        {
            var cells = row.QuerySelectorAll("th, td");
            sb.AppendLine("| " + string.Join(" | ", cells.Select(c => c.TextContent.Trim().Replace("|", "\\|"))) + " |");
        }
        return sb.ToString();
    }
}
