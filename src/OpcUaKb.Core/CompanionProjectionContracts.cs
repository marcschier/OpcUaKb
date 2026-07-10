using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

public static class CompanionProjectionContractVersions
{
    public const string Job = "1.0";
    public const string Mapping = "1.0";
    public const string Report = "1.0";
}

public enum CompanionProjectionJobState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public enum CompanionMappingKind
{
    Variable,
    Object,
    Method,
    Event,
    PassThrough,
    UnboundRequired,
}

public enum CompanionMappingDirection
{
    Read,
    ReadWrite,
    MethodForward,
}

public enum CompanionDecisionStatus
{
    Accepted,
    Proposed,
    Unresolved,
}

public sealed record CompanionProjectionJobRequest
{
    public string ContractVersion { get; init; } = CompanionProjectionContractVersions.Job;
    public required string JobId { get; init; }
    public required string OutputModelUri { get; init; }
    public string? OutputVersion { get; init; }
    public required string SourceName { get; init; }
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
    public IReadOnlyList<string> ExcludePaths { get; init; } = [];
    public int CandidateLimit { get; init; } = 8;
    public int MaxProjectionsPerSource { get; init; } = 3;
    public double AcceptedThreshold { get; init; } = 0.82;
    public double ProposedThreshold { get; init; } = 0.55;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CompanionProjectionJobStatus
{
    public string ContractVersion { get; init; } = CompanionProjectionContractVersions.Job;
    public required string JobId { get; init; }
    public required CompanionProjectionJobState State { get; init; }
    public string? Phase { get; init; }
    public double Progress { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CompanionArtifactMetadata
{
    public required string Name { get; init; }
    public required string MediaType { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
}

public sealed record CompanionProjectionJobResult
{
    public string ContractVersion { get; init; } = CompanionProjectionContractVersions.Job;
    public required string JobId { get; init; }
    public required CompanionProjectionJobState State { get; init; }
    public IReadOnlyList<CompanionArtifactMetadata> Artifacts { get; init; } = [];
    public CompanionProjectionReport? Report { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CompanionNodeIdentifier
{
    public required string ExpandedNodeId { get; init; }
    public required string BrowseName { get; init; }
    public string? BrowsePath { get; init; }
    public string? ModelUri { get; init; }
    public string? ModelVersion { get; init; }
    public DateTimeOffset? ModelPublicationDate { get; init; }
}

public sealed record CompanionMappingEvidence
{
    public double DeterministicScore { get; init; }
    public double? SemanticScore { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public IReadOnlyDictionary<string, double> Features { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}

public sealed record CompanionMappingEntry
{
    public required string MappingId { get; init; }
    public required CompanionNodeIdentifier Source { get; init; }
    public CompanionNodeIdentifier? Target { get; init; }
    public required CompanionMappingKind Kind { get; init; }
    public CompanionMappingDirection? Direction { get; init; }
    public required CompanionDecisionStatus Status { get; init; }
    public required double Confidence { get; init; }
    public required CompanionMappingEvidence Evidence { get; init; }
    public bool IsMandatory { get; init; }
    public string? DeclarationPath { get; init; }
    public string? TargetNodeClass { get; init; }
    public string? TargetTypeDefinition { get; init; }
    public string? TargetDataType { get; init; }
    public string? TargetReferenceType { get; init; }
    public string? TargetBrowseNameNamespaceUri { get; init; }
    public string? Notes { get; init; }
}

public sealed record CompanionProjectionModelMetadata
{
    public string SchemaVersion { get; init; } = CompanionProjectionContractVersions.Mapping;
    public required string OutputModelUri { get; init; }
    public string? OutputVersion { get; init; }
    public required string SourceName { get; init; }
    public IReadOnlyList<string> SourceModelUris { get; init; } = [];
    public IReadOnlyList<AddressSpaceRequiredModel> RequiredModels { get; init; } = [];
    public IReadOnlyList<string> RequiredModelUris { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record CompanionProjectionDefinition
{
    public required int Ordinal { get; init; }
    public required CompanionNodeIdentifier SourceRoot { get; init; }
    public CompanionNodeIdentifier? TargetType { get; init; }
    public required CompanionDecisionStatus Status { get; init; }
    public required double Confidence { get; init; }
    public IReadOnlyList<CompanionMappingEntry> Mappings { get; init; } = [];
    public IReadOnlyList<string> BlockingGaps { get; init; } = [];
}

public sealed record CompanionMappingDocument
{
    public string SchemaVersion { get; init; } = CompanionProjectionContractVersions.Mapping;
    public required CompanionProjectionModelMetadata Model { get; init; }
    public IReadOnlyList<CompanionProjectionDefinition> Projections { get; init; } = [];
}

public sealed record CompanionProjectionReport
{
    public string SchemaVersion { get; init; } = CompanionProjectionContractVersions.Report;
    public required CompanionProjectionModelMetadata Model { get; init; }
    public int SourceNodeCount { get; init; }
    public int IncludedNodeCount { get; init; }
    public int AcceptedProjectionCount { get; init; }
    public int ProposedProjectionCount { get; init; }
    public int UnresolvedProjectionCount { get; init; }
    public IReadOnlyList<CompanionProjectionDefinition> Projections { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public static class CompanionTargetNodeId
{
    public static string Create(
        string outputModelUri,
        string sourceCanonicalExpandedNodeId,
        string targetTypeExpandedNodeId,
        string declarationPath,
        int projectionOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputModelUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCanonicalExpandedNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTypeExpandedNodeId);
        ArgumentNullException.ThrowIfNull(declarationPath);
        ArgumentOutOfRangeException.ThrowIfNegative(projectionOrdinal);
        _ = projectionOrdinal;

        var canonicalInput = string.Join(
            "\n",
            outputModelUri.Trim(),
            sourceCanonicalExpandedNodeId.Trim(),
            targetTypeExpandedNodeId.Trim(),
            declarationPath.Trim());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalInput));
        return $"nsu={EscapeNamespaceUri(outputModelUri)};s=projection-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    public static string EscapeNamespaceUri(string namespaceUri) =>
        AddressSpaceExpandedNodeId.EscapeNamespaceUri(namespaceUri);
}
