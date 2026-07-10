using System.IO.Compression;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

sealed class NodeSetModelCatalog
{
    public required List<NodeSetModelCatalogEntry> Models { get; init; }
}

sealed class NodeSetModelCatalogEntry
{
    public required string ModelUri { get; init; }
    public required string Version { get; init; }
    public required string PublicationDate { get; init; }
    public required string SourceBlob { get; init; }
    public string Source { get; init; } = "opcfoundation";
    public bool IsOfficial { get; init; } = true;
    public bool IsLatest { get; set; }
    public required List<string> NamespaceUris { get; init; }
    public required List<NodeSetRequiredModel> RequiredModels { get; init; }
}

sealed class NodeSetRequiredModel
{
    public required string ModelUri { get; init; }
    public required string Version { get; init; }
    public required string PublicationDate { get; init; }
}

static class NodeSetModelCatalogStore
{
    public const string BlobName = "nodesets/model-catalog.json.gz";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task WriteAsync(
        BlobServiceClient blobs,
        IReadOnlyList<NodeSetModelCatalogEntry> entries,
        ILogger log)
    {
        MarkLatest(entries);
        var catalog = new NodeSetModelCatalog
        {
            Models = entries
                .OrderBy(e => e.ModelUri, StringComparer.Ordinal)
                .ThenBy(e => e.Version, StringComparer.Ordinal)
                .ThenBy(e => e.PublicationDate, StringComparer.Ordinal)
                .ThenBy(e => e.SourceBlob, StringComparer.Ordinal)
                .ThenBy(e => string.Join('\u001F', e.NamespaceUris), StringComparer.Ordinal)
                .ThenBy(e => string.Join('\u001E', e.RequiredModels.Select(r =>
                    $"{r.ModelUri}\u001F{r.Version}\u001F{r.PublicationDate}")),
                    StringComparer.Ordinal)
                .ToList(),
        };

        log.LogInformation(
            "[MODEL_CATALOG] Phase=write_start Blob={Blob} Models={Models}",
            BlobName, catalog.Models.Count);

        var json = JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions);
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(json);

        await blobs.GetBlobContainerClient("opcua-content")
            .GetBlobClient(BlobName)
            .UploadAsync(BinaryData.FromBytes(compressed.ToArray()), overwrite: true);

        log.LogInformation(
            "[MODEL_CATALOG] Phase=write_complete Blob={Blob} Models={Models} Latest={Latest} RawBytes={RawBytes} CompressedBytes={CompressedBytes}",
            BlobName, catalog.Models.Count, catalog.Models.Count(e => e.IsLatest),
            json.Length, compressed.Length);
    }

    public static async Task<IReadOnlyList<NodeSetModelCatalogEntry>> MergeWithExistingAsync(
        BlobServiceClient blobs,
        IReadOnlyList<NodeSetModelCatalogEntry> entries,
        IReadOnlyCollection<string> failedSourceBlobs,
        ILogger log)
    {
        var mergedEntries = entries.ToList();
        var preserved = 0;
        if (failedSourceBlobs.Count > 0)
        {
            var existing = await TryReadExistingAsync(blobs);
            foreach (var entry in existing.Where(entry =>
                         failedSourceBlobs.Contains(entry.SourceBlob, StringComparer.Ordinal)))
            {
                if (mergedEntries.Any(current =>
                        string.Equals(current.ModelUri, entry.ModelUri, StringComparison.Ordinal)
                        && string.Equals(current.Version, entry.Version, StringComparison.Ordinal)
                        && string.Equals(current.SourceBlob, entry.SourceBlob, StringComparison.Ordinal)))
                    continue;
                mergedEntries.Add(entry);
                preserved++;
            }
            log.LogWarning(
                "[MODEL_CATALOG] Phase=merge_failed_sources FailedSources={FailedSources} PreservedModels={PreservedModels}",
                failedSourceBlobs.Count, preserved);
        }
        BuildVersionMetadata(mergedEntries);
        return mergedEntries;
    }

    public static void ApplyVersionMetadata(
        IEnumerable<Azure.Search.Documents.Models.SearchDocument> documents,
        IReadOnlyList<NodeSetModelCatalogEntry> catalog)
    {
        var versionMetadata = BuildVersionMetadata(catalog);
        foreach (var doc in documents)
        {
            var modelUri = doc.TryGetValue("model_uri", out var model)
                ? model?.ToString() ?? "" : "";
            var version = doc.TryGetValue("model_version", out var modelVersion)
                ? modelVersion?.ToString() ?? "" : "";
            var sourceBlob = doc.TryGetValue("source_blob", out var source)
                ? source?.ToString() ?? "" : "";
            if (versionMetadata.TryGetValue(
                    (modelUri, version, sourceBlob), out var metadata))
            {
                doc["is_latest"] = metadata.IsLatest;
                doc["version_rank"] = metadata.VersionRank;
            }
            else
            {
                doc["is_latest"] = false;
                doc["version_rank"] = int.MaxValue;
            }
        }
    }

    static async Task<IReadOnlyList<NodeSetModelCatalogEntry>> TryReadExistingAsync(
        BlobServiceClient blobs)
    {
        var blob = blobs.GetBlobContainerClient("opcua-content").GetBlobClient(BlobName);
        try
        {
            await using var source = await blob.OpenReadAsync();
            await using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var existing = await JsonSerializer.DeserializeAsync<NodeSetModelCatalog>(
                gzip, JsonOptions);
            return existing?.Models ?? [];
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return [];
        }
    }

    public static Dictionary<(string ModelUri, string Version, string SourceBlob), (bool IsLatest, int VersionRank)>
        BuildVersionMetadata(IReadOnlyList<NodeSetModelCatalogEntry> entries)
    {
        MarkLatest(entries);
        var result = new Dictionary<
            (string ModelUri, string Version, string SourceBlob),
            (bool IsLatest, int VersionRank)>();
        foreach (var group in entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.ModelUri))
                     .GroupBy(e => e.ModelUri, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(e => ParsePublicationDate(e.PublicationDate))
                .ThenByDescending(e => e.Version, ModelVersionComparer.Instance)
                .ThenBy(e => e.SourceBlob, StringComparer.Ordinal)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                result[(entry.ModelUri, entry.Version, entry.SourceBlob)] =
                    (entry.IsLatest, i + 1);
            }
        }
        return result;
    }

    static void MarkLatest(IReadOnlyList<NodeSetModelCatalogEntry> entries)
    {
        foreach (var entry in entries)
            entry.IsLatest = false;

        foreach (var group in entries
                     .Where(e => !string.IsNullOrWhiteSpace(e.ModelUri))
                     .GroupBy(e => e.ModelUri, StringComparer.Ordinal))
        {
            group
                .OrderByDescending(e => ParsePublicationDate(e.PublicationDate))
                .ThenByDescending(e => e.Version, ModelVersionComparer.Instance)
                .ThenBy(e => e.SourceBlob, StringComparer.Ordinal)
                .First()
                .IsLatest = true;
        }
    }

    static DateTimeOffset? ParsePublicationDate(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    sealed class ModelVersionComparer : IComparer<string>
    {
        public static ModelVersionComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftParts = left.Split(['.', '-', '+']);
            var rightParts = right.Split(['.', '-', '+']);
            for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
            {
                if (i >= leftParts.Length) return -1;
                if (i >= rightParts.Length) return 1;
                var leftNumber = int.TryParse(leftParts[i], out var ln);
                var rightNumber = int.TryParse(rightParts[i], out var rn);
                var comparison = leftNumber && rightNumber
                    ? ln.CompareTo(rn)
                    : StringComparer.OrdinalIgnoreCase.Compare(leftParts[i], rightParts[i]);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }
}
