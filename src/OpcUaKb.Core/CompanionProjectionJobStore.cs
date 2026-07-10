using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

public sealed class CompanionProjectionJobStore
{
    public static readonly string[] ArtifactFileNames =
    [
        "projection.nodeset2.xml",
        "mapping.json",
        "mapping.csv",
        "report.json",
        "report.md",
        "bundle.zip",
    ];

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    readonly BlobContainerClient _container;
    readonly string _prefix;

    public CompanionProjectionJobStore(BlobContainerClient container, string? prefix = null)
    {
        _container = container;
        _prefix = NormalizePrefix(prefix);
    }

    public BlobContainerClient Container => _container;
    public string Prefix => _prefix;

    public string GetJobPrefix(string jobId) => $"{_prefix}/{jobId}";
    public string GetInputBlobName(string jobId) => $"{GetJobPrefix(jobId)}/input.xml";
    public string GetRequestBlobName(string jobId) => $"{GetJobPrefix(jobId)}/request.json";
    public string GetStatusBlobName(string jobId) => $"{GetJobPrefix(jobId)}/status.json";
    public string GetEnqueuedBlobName(string jobId) => $"{GetJobPrefix(jobId)}/enqueued.json";
    public string GetArtifactBlobName(string jobId, string fileName) =>
        $"{GetJobPrefix(jobId)}/artifacts/{fileName}";

    public BlobClient GetInputBlob(string jobId) => _container.GetBlobClient(GetInputBlobName(jobId));
    public BlobClient GetRequestBlob(string jobId) => _container.GetBlobClient(GetRequestBlobName(jobId));
    public BlobClient GetStatusBlob(string jobId) => _container.GetBlobClient(GetStatusBlobName(jobId));
    public BlobClient GetEnqueuedBlob(string jobId) => _container.GetBlobClient(GetEnqueuedBlobName(jobId));
    public BlobClient GetArtifactBlob(string jobId, string fileName) =>
        _container.GetBlobClient(GetArtifactBlobName(jobId, fileName));

    public async Task<CompanionProjectionDurableJobRequest?> TryReadRequestAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return await TryReadJsonAsync<CompanionProjectionDurableJobRequest>(
            GetRequestBlob(jobId), cancellationToken);
    }

    public async Task<CompanionProjectionDurableJobStatus?> TryReadStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        return await TryReadJsonAsync<CompanionProjectionDurableJobStatus>(
            GetStatusBlob(jobId), cancellationToken);
    }

    public Task<Response<BlobContentInfo>> WriteRequestAsync(
        CompanionProjectionDurableJobRequest request,
        bool onlyIfAbsent,
        CancellationToken cancellationToken = default) =>
        WriteJsonAsync(
            GetRequestBlob(request.JobId),
            request,
            onlyIfAbsent ? new BlobRequestConditions { IfNoneMatch = ETag.All } : null,
            cancellationToken);

    public Task<Response<BlobContentInfo>> WriteStatusAsync(
        CompanionProjectionDurableJobStatus status,
        string? leaseId = null,
        bool onlyIfAbsent = false,
        CancellationToken cancellationToken = default) =>
        WriteJsonAsync(
            GetStatusBlob(status.JobId),
            status,
            new BlobRequestConditions
            {
                LeaseId = leaseId,
                IfNoneMatch = onlyIfAbsent ? ETag.All : default,
            },
            cancellationToken);

    public async Task<bool> HasEnqueuedMarkerAsync(
        string jobId,
        CancellationToken cancellationToken = default) =>
        (await GetEnqueuedBlob(jobId).ExistsAsync(cancellationToken)).Value;

    public async Task TryWriteEnqueuedMarkerAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteJsonAsync(
                GetEnqueuedBlob(jobId),
                new { job_id = jobId, enqueued_at = DateTimeOffset.UtcNow },
                new BlobRequestConditions { IfNoneMatch = ETag.All },
                cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Another idempotent creator wrote the marker.
        }
    }

    public static bool IsArtifactFileName(string fileName) =>
        ArtifactFileNames.Contains(fileName, StringComparer.Ordinal);

    public static string ContentTypeForArtifact(string fileName) => fileName switch
    {
        "projection.nodeset2.xml" => "application/xml",
        "mapping.json" or "report.json" => "application/json",
        "mapping.csv" => "text/csv; charset=utf-8",
        "report.md" => "text/markdown; charset=utf-8",
        "bundle.zip" => "application/zip",
        _ => "application/octet-stream",
    };

    public static string DownloadPath(string jobId, string fileName) =>
        $"/mapping-artifacts/{jobId}/{fileName}";

    static async Task<T?> TryReadJsonAsync<T>(
        BlobClient blob,
        CancellationToken cancellationToken)
    {
        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToObjectFromJson<T>(JsonOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return default;
        }
    }

    static Task<Response<BlobContentInfo>> WriteJsonAsync<T>(
        BlobClient blob,
        T value,
        BlobRequestConditions? conditions,
        CancellationToken cancellationToken)
    {
        var content = BinaryData.FromObjectAsJson(value, JsonOptions);
        return blob.UploadAsync(content, new BlobUploadOptions
        {
            Conditions = conditions,
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        }, cancellationToken);
    }

    public static async Task<(string Sha256, long Size)> HashAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var bounded = new SizeBoundedStream(source, maxBytes, leaveInnerOpen: true);
        using var hashing = new HashingStream(bounded, hash, leaveInnerOpen: true);
        await hashing.CopyToAsync(Stream.Null, cancellationToken);
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), bounded.BytesRead);
    }

    static string NormalizePrefix(string? prefix)
    {
        var value = string.IsNullOrWhiteSpace(prefix)
            ? "model-mappings/jobs"
            : prefix.Trim().Trim('/');
        if (value.Length == 0 || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Mapping blob prefix must be a non-empty container-relative path.", nameof(prefix));
        return value;
    }
}
