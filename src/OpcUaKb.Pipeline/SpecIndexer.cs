using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════
// SpecIndexer — V2 pipeline.
//
// Combines parsed HTML sections (SpecHtmlParser) with STS-XML metadata
// (StsMetadataParser) to produce per-section SearchDocuments, embeds the
// page_chunk text, and uploads to the v2 Azure AI Search index
// (opcua-content-index-v2) for a blue-green cutover from the legacy v1
// index. Input blobs are produced by SpecDownloader:
//   html/{spec_id}-{version}.html
//   sts-xml/{spec_id}-{version}.xml
// ═══════════════════════════════════════════════════════════════════════

public sealed record EstimationResult(int VersionCount, int EstimatedChunkCount, long EstimatedTokens);
public sealed record IndexResult(int Chunks, int Embedded, int Uploaded, int Errors);

sealed class SpecIndexer
{
    const string ContainerName = "opcua-content";

    // TODO: Make configurable via SEARCH_INDEX_NAME env var in a future revision.
    const string IndexName = "opcua-content-index-v2";

    const int EmbeddingBatchSize = 50;
    const int UploadBatchSize = 100;
    const int EmbeddingDimensions = 3072;
    const string EmbeddingDeployment = "text-embedding-3-large";

    // Estimate ~200 tokens per chunk and ~120 K TPM of embedding capacity.
    const int AvgTokensPerChunk = 200;
    const int EmbeddingTpmCapacity = 120_000;

    static readonly Regex BlobNameRe = new(
        @"^html/(?<spec>OPC-[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)-(?<ver>\d+(?:\.\d+)+)\.html$",
        RegexOptions.Compiled);

    // Counts the H2/H3/H4 headings that drive section emission in SpecHtmlParser.
    static readonly Regex HeadingRe = new(
        @"<h[234]\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex Part10000Re = new(
        @"^OPC-10000-(?<num>\d+)$", RegexOptions.Compiled);

    static readonly Regex Part5DigitRe = new(
        @"^OPC-(?<num>\d{5})$", RegexOptions.Compiled);

    readonly BlobContainerClient _container;
    readonly SearchIndexClient _indexClient;
    readonly SearchClient _searchClient;
    readonly HttpClient _http;
    readonly string _aoaiEndpoint;
    readonly TokenCredential _credential;
    readonly ILogger _log;

    public SpecIndexer(
        BlobServiceClient blobs,
        string searchEndpoint,
        string searchApiKey,
        string aoaiEndpoint,
        TokenCredential credential,
        HttpClient http,
        ILogger log)
    {
        _container = blobs.GetBlobContainerClient(ContainerName);
        _indexClient = new SearchIndexClient(new Uri(searchEndpoint), new AzureKeyCredential(searchApiKey));
        _searchClient = _indexClient.GetSearchClient(IndexName);
        _http = http;
        _aoaiEndpoint = aoaiEndpoint;
        _credential = credential;
        _log = log;
    }

    /// <summary>
    /// Phase A — preflight estimation. Counts versions and approximates the
    /// per-version section count via a cheap regex over the HTML; sums for
    /// a coarse token-budget projection. Does not parse HTML or call the
    /// embedding API.
    /// </summary>
    public async Task<EstimationResult> EstimateAsync(CancellationToken ct = default)
    {
        var versionCount = 0;
        var chunkCount = 0;

        await foreach (var item in _container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, prefix: "html/", cancellationToken: ct))
        {
            if (!BlobNameRe.IsMatch(item.Name)) continue;
            versionCount++;

            try
            {
                var dl = await _container.GetBlobClient(item.Name).DownloadContentAsync(ct);
                var html = dl.Value.Content.ToString();
                chunkCount += HeadingRe.Matches(html).Count;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "[SPEC_INDEX] Phase=estimate Blob={Blob} Error={Error}",
                    item.Name, ex.Message);
            }
        }

        long tokens = (long)chunkCount * AvgTokensPerChunk;
        var durationMin = tokens / (double)EmbeddingTpmCapacity / 60.0;

        _log.LogInformation(
            "[SPEC_INDEX] Phase=estimate Versions={V} EstimatedChunks={C} EstimatedTokens={T} EstimatedDuration={D}min",
            versionCount, chunkCount, tokens, durationMin.ToString("F1"));

        return new EstimationResult(versionCount, chunkCount, tokens);
    }

    /// <summary>
    /// Phase B — parse, embed, upload. Walks every html/{spec}-{ver}.html
    /// blob, pairs it with its sts-xml/{spec}-{ver}.xml metadata when
    /// available, emits one SearchDocument per heading, then embeds and
    /// uploads in batches.
    /// </summary>
    public async Task<IndexResult> IndexAsync(CancellationToken ct = default)
    {
        await EnsureIndexV2Async();
        _log.LogInformation("[SPEC_INDEX] Phase=index_ready Index={Name}", IndexName);

        var versions = new List<(string SpecId, string Version, string BlobName)>();
        await foreach (var item in _container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, prefix: "html/", cancellationToken: ct))
        {
            var m = BlobNameRe.Match(item.Name);
            if (!m.Success) continue;
            versions.Add((m.Groups["spec"].Value, m.Groups["ver"].Value, item.Name));
        }
        _log.LogInformation("[SPEC_INDEX] Phase=index_start Versions={N}", versions.Count);

        var htmlParser = new SpecHtmlParser(_log);
        var stsParser = new StsMetadataParser(_log);

        // unembedded: docs whose page_chunk_vector hasn't been filled in yet
        // ready:      docs with vectors, queued for upload
        var unembedded = new List<SearchDocument>(EmbeddingBatchSize);
        var ready = new List<SearchDocument>(UploadBatchSize);

        var totalChunks = 0;
        var embedded = 0;
        var uploaded = 0;
        var errors = 0;
        var processedBlobs = 0;
        var lastChunkLogged = 0;

        foreach (var (specId, version, htmlBlobName) in versions)
        {
            processedBlobs++;
            try
            {
                var htmlDl = await _container.GetBlobClient(htmlBlobName).DownloadContentAsync(ct);
                var html = htmlDl.Value.Content.ToString();

                StsMetadata? sts = null;
                var stsBlobName = $"sts-xml/{specId}-{version}.xml";
                var stsBlob = _container.GetBlobClient(stsBlobName);
                if ((await stsBlob.ExistsAsync(ct)).Value)
                {
                    try
                    {
                        var stsDl = await stsBlob.DownloadContentAsync(ct);
                        sts = stsParser.Parse(stsDl.Value.Content.ToString());
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(
                            "[SPEC_INDEX] Phase=parse Spec={Spec} Version={Version} StsParseError={Error}",
                            specId, version, ex.Message);
                    }
                }
                else
                {
                    _log.LogWarning(
                        "[SPEC_INDEX] Phase=parse Spec={Spec} Version={Version} MissingStsXml={Blob}",
                        specId, version, stsBlobName);
                }

                var metadata = new SpecMetadata(
                    SpecId: sts?.SpecId ?? specId,
                    SpecTitle: sts?.SpecTitle ?? specId,
                    SpecVersion: sts?.SpecVersion ?? version,
                    PublicationDate: sts?.PublicationDate,
                    NamespaceUri: sts?.NamespaceUri,
                    GitHubTag: sts?.GitHubTag);

                var chunkIndex = 0;
                foreach (var chunk in htmlParser.ParseSections(html, metadata, sts?.SectionSlugByNumber))
                {
                    unembedded.Add(BuildSearchDocument(metadata, chunk, chunkIndex++));
                    totalChunks++;

                    if (unembedded.Count >= EmbeddingBatchSize)
                    {
                        var ok = await EmbedBatchAsync(unembedded);
                        if (ok) embedded += unembedded.Count;
                        else errors += unembedded.Count;
                        ready.AddRange(unembedded);
                        unembedded.Clear();

                        while (ready.Count >= UploadBatchSize)
                        {
                            var slice = ready.GetRange(0, UploadBatchSize);
                            ready.RemoveRange(0, UploadBatchSize);
                            var uploadedNow = await UploadBatchAsync(slice);
                            if (uploadedNow > 0) uploaded += uploadedNow;
                            else errors += slice.Count;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    "[SPEC_INDEX] Phase=parse Spec={Spec} Version={Version} Error={Error}",
                    specId, version, ex.Message);
                errors++;
            }

            if (processedBlobs % 10 == 0)
            {
                _log.LogInformation(
                    "[SPEC_INDEX] Phase=progress ProcessedBlobs={P} TotalVersions={T} Chunks={C} Embedded={E} Uploaded={U} Errors={Err}",
                    processedBlobs, versions.Count, totalChunks, embedded, uploaded, errors);
            }
            if (totalChunks - lastChunkLogged >= 500)
            {
                lastChunkLogged = totalChunks - (totalChunks % 500);
                _log.LogInformation(
                    "[SPEC_INDEX] Phase=progress Chunks={C} Embedded={E} Uploaded={U}",
                    totalChunks, embedded, uploaded);
            }
        }

        // Final flush: embed any remaining unembedded docs, then upload everything.
        if (unembedded.Count > 0)
        {
            var ok = await EmbedBatchAsync(unembedded);
            if (ok) embedded += unembedded.Count;
            else errors += unembedded.Count;
            ready.AddRange(unembedded);
            unembedded.Clear();
        }

        while (ready.Count > 0)
        {
            var size = Math.Min(UploadBatchSize, ready.Count);
            var slice = ready.GetRange(0, size);
            ready.RemoveRange(0, size);
            var uploadedNow = await UploadBatchAsync(slice);
            if (uploadedNow > 0) uploaded += uploadedNow;
            else errors += slice.Count;
        }

        _log.LogInformation(
            "[SPEC_INDEX] Phase=upload Status=completed Indexed={N}", uploaded);

        return new IndexResult(totalChunks, embedded, uploaded, errors);
    }

    async Task EnsureIndexV2Async()
    {
        var index = SearchIndexFactory.Build(IndexName, _aoaiEndpoint);
        await _indexClient.CreateOrUpdateIndexAsync(index);
    }

    SearchDocument BuildSearchDocument(SpecMetadata metadata, SectionChunk chunk, int chunkIndex)
    {
        var key = $"{metadata.SpecId}|{metadata.SpecVersion}|{chunk.SectionId}";
        var id = Base64UrlEncode(key);

        var dict = new Dictionary<string, object>
        {
            ["id"] = id,
            ["page_chunk"] = chunk.PageChunk,
            ["source_url"] = chunk.SourceUrl,
            ["spec_id"] = metadata.SpecId,
            ["spec_part"] = DeriveSpecPart(metadata.SpecId),
            ["spec_title"] = metadata.SpecTitle,
            ["spec_version"] = metadata.SpecVersion,
            ["section_id"] = chunk.SectionId,
            ["section_number"] = chunk.SectionNumber,
            ["section_title"] = chunk.SectionTitle,
            ["section_path"] = chunk.SectionPath,
            ["breadcrumb"] = chunk.Breadcrumb.ToList(),
            ["figures"] = chunk.Figures.ToList(),
            ["chunk_index"] = chunkIndex,
            ["content_type"] = "spec_section",
            ["is_latest"] = true,
            ["version_rank"] = 1,
            ["source"] = "opcfoundation",
            ["popularity"] = 1_000_000_000L,
            ["in_opcfoundation_index"] = true,
            ["namespace_uri"] = metadata.NamespaceUri ?? "",
            ["title"] = metadata.SpecTitle,
            ["description"] = chunk.SectionTitle,
        };

        if (metadata.PublicationDate.HasValue)
        {
            dict["publication_date"] = new DateTimeOffset(
                metadata.PublicationDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        }

        return new SearchDocument(dict);
    }

    async Task<bool> EmbedBatchAsync(List<SearchDocument> batch)
    {
        if (batch.Count == 0) return true;
        try
        {
            var texts = batch.Select(d => (string)d["page_chunk"]).ToList();
            var vectors = await EmbeddingClient.GetEmbeddingsAsync(
                _http, _credential, _aoaiEndpoint, EmbeddingDeployment, texts, _log);

            for (var i = 0; i < batch.Count && i < vectors.Count; i++)
                batch[i]["page_chunk_vector"] = vectors[i];

            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "[SPEC_INDEX] Phase=embedding BatchSize={N} Error={Error}",
                batch.Count, ex.Message);
            return false;
        }
    }

    async Task<int> UploadBatchAsync(List<SearchDocument> batch)
    {
        if (batch.Count == 0) return 0;
        try
        {
            await RetryHelper.RetrySearchAsync(
                () => _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(batch)), _log);
            return batch.Count;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "[SPEC_INDEX] Phase=upload BatchSize={N} Error={Error}",
                batch.Count, ex.Message);
            return 0;
        }
    }

    static string Base64UrlEncode(string s) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    // Derive a stable `spec_part` facet value from a SpecId.
    //   OPC-10000-3   → Part3            (core spec parts)
    //   OPC-30060     → Spec30060        (5-digit companion specs without a part suffix)
    //   anything else → SpecId unchanged
    internal static string DeriveSpecPart(string specId)
    {
        var m = Part10000Re.Match(specId);
        if (m.Success) return "Part" + m.Groups["num"].Value;
        m = Part5DigitRe.Match(specId);
        if (m.Success) return "Spec" + m.Groups["num"].Value;
        return specId;
    }
}
