using System.Text.Json.Serialization;

public enum CompanionProjectionQueueJobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
}

public sealed record CompanionProjectionJobOptions
{
    public string? OutputModelUri { get; init; }
    public IReadOnlyList<string> IncludeBrowsePaths { get; init; } = [];
    public IReadOnlyList<string> ExcludeBrowsePaths { get; init; } = [];
    public double AcceptedConfidenceThreshold { get; init; } = 0.82;
    public double ProposedConfidenceThreshold { get; init; } = 0.55;
    public int MaxProjectionsPerSource { get; init; } = 3;
}

public sealed record CompanionProjectionDurableJobRequest
{
    public required string JobId { get; init; }
    public required string InputSha256 { get; init; }
    public required long InputSizeBytes { get; init; }
    public required string InputBlobName { get; init; }
    public required string SourceMode { get; init; }
    public string? SourceRef { get; init; }
    public string? SourceUrl { get; init; }
    public required CompanionProjectionJobOptions Options { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record CompanionProjectionArtifact
{
    public required string FileName { get; init; }
    public required string BlobRef { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required string ETag { get; init; }
    public string? DownloadUrl { get; init; }
}

public sealed record CompanionProjectionProgress
{
    public string? Checkpoint { get; init; }
    public int ProcessedNodes { get; init; }
    public int TotalNodes { get; init; }
    public int CandidateCount { get; init; }
    public int ProjectionCount { get; init; }
    public int WarningCount { get; init; }
    public string? Message { get; init; }
}

public sealed record CompanionProjectionDurableJobStatus
{
    public required string JobId { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<CompanionProjectionQueueJobState>))]
    public CompanionProjectionQueueJobState State { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Attempt { get; init; }
    public bool Terminal { get; init; }
    public CompanionProjectionProgress Progress { get; init; } = new();
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<CompanionProjectionArtifact> Artifacts { get; init; } = [];
}

public sealed record CompanionProjectionJobSnapshot
{
    public required CompanionProjectionDurableJobRequest Request { get; init; }
    public required CompanionProjectionDurableJobStatus Status { get; init; }
}

public sealed record CompanionProjectionJobCreation
{
    public required CompanionProjectionJobSnapshot Job { get; init; }
    public bool Existing { get; init; }
}

public sealed record CompanionProjectionQueueMessage
{
    public int Version { get; init; } = 1;
    public required string JobId { get; init; }
}

public sealed record CompanionProjectionProcessContext
{
    public required string JobId { get; init; }
    public required Stream Input { get; init; }
    public required CompanionProjectionJobOptions Options { get; init; }
    public string? Checkpoint { get; init; }
    public required ICompanionProjectionArtifactSink Artifacts { get; init; }
    public required Func<CompanionProjectionProgress, CancellationToken, Task> ReportProgressAsync { get; init; }
}

public sealed record CompanionProjectionProcessResult
{
    public string? Checkpoint { get; init; }
    public int ProcessedNodes { get; init; }
    public int TotalNodes { get; init; }
    public int CandidateCount { get; init; }
    public int ProjectionCount { get; init; }
    public int WarningCount { get; init; }
    public string? Message { get; init; }
}

public interface ICompanionProjectionProcessor
{
    Task<CompanionProjectionProcessResult> ProcessAsync(
        CompanionProjectionProcessContext context,
        CancellationToken cancellationToken);
}

public interface ICompanionProjectionArtifactSink
{
    Task<CompanionProjectionArtifact> WriteAsync(
        string fileName,
        Stream content,
        string? contentType = null,
        CancellationToken cancellationToken = default);
}

public sealed class CompanionProjectionTerminalException : Exception
{
    public CompanionProjectionTerminalException(string message) : base(message) { }
    public CompanionProjectionTerminalException(string message, Exception innerException)
        : base(message, innerException) { }
}
