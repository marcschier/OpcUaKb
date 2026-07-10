using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record CompanionGeneratedArtifact
{
    public required CompanionArtifactMetadata Metadata { get; init; }
    public required byte[] Content { get; init; }

    public Stream OpenRead() => new MemoryStream(Content, writable: false);
}

public sealed record CompanionArtifactBundle
{
    public required CompanionGeneratedArtifact ProjectionNodeSet { get; init; }
    public required CompanionGeneratedArtifact MappingJson { get; init; }
    public required CompanionGeneratedArtifact MappingCsv { get; init; }
    public required CompanionGeneratedArtifact ReportJson { get; init; }
    public required CompanionGeneratedArtifact ReportMarkdown { get; init; }
    public required CompanionGeneratedArtifact Zip { get; init; }

    public IReadOnlyList<CompanionGeneratedArtifact> Files =>
        [ProjectionNodeSet, MappingJson, MappingCsv, ReportJson, ReportMarkdown, Zip];
}

public sealed class ModelMappingArtifactWriter
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    readonly CompanionProjectionNodeSetWriter _nodeSetWriter;

    public ModelMappingArtifactWriter(CompanionProjectionNodeSetWriter? nodeSetWriter = null) =>
        _nodeSetWriter = nodeSetWriter ?? new CompanionProjectionNodeSetWriter();

    public async Task<CompanionArtifactBundle> WriteAsync(
        CompanionProjectionEngineResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var xml = Artifact(
            "projection.nodeset2.xml",
            "application/xml",
            await _nodeSetWriter.WriteBytesAsync(result.Mapping, cancellationToken));
        var mappingJson = Artifact(
            "mapping.json",
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(result.Mapping, JsonOptions));
        var csv = Artifact(
            "mapping.csv",
            "text/csv; charset=utf-8",
            Encoding.UTF8.GetBytes(CreateCsv(result.Mapping)));
        var reportJson = Artifact(
            "report.json",
            "application/json",
            JsonSerializer.SerializeToUtf8Bytes(result.Report, JsonOptions));
        var reportMarkdown = Artifact(
            "report.md",
            "text/markdown; charset=utf-8",
            Encoding.UTF8.GetBytes(CreateMarkdown(result.Report)));

        var zipBytes = await CreateZipAsync(
            [xml, mappingJson, csv, reportJson, reportMarkdown], cancellationToken);
        var zip = Artifact("bundle.zip", "application/zip", zipBytes);
        return new CompanionArtifactBundle
        {
            ProjectionNodeSet = xml,
            MappingJson = mappingJson,
            MappingCsv = csv,
            ReportJson = reportJson,
            ReportMarkdown = reportMarkdown,
            Zip = zip,
        };
    }

    static CompanionGeneratedArtifact Artifact(string name, string mediaType, byte[] content)
    {
        var hash = SHA256.HashData(content);
        return new CompanionGeneratedArtifact
        {
            Content = content,
            Metadata = new CompanionArtifactMetadata
            {
                Name = name,
                MediaType = mediaType,
                Length = content.LongLength,
                Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            },
        };
    }

    static async Task<byte[]> CreateZipAsync(
        IReadOnlyList<CompanionGeneratedArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(artifact.Metadata.Name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var stream = entry.Open();
                await stream.WriteAsync(artifact.Content, cancellationToken);
            }
        }
        return output.ToArray();
    }

    static string CreateCsv(CompanionMappingDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "projection_ordinal,projection_status,mapping_id,kind,direction,decision_status,confidence,is_mandatory,source_node_id,source_browse_path,target_node_id,target_browse_path,target_type,model_uri,declaration_path,target_reference_type,target_browse_name_namespace_uri");
        foreach (var projection in document.Projections.OrderBy(p => p.Ordinal))
        {
            foreach (var mapping in projection.Mappings.OrderBy(m => m.DeclarationPath, StringComparer.Ordinal))
            {
                AppendCsvRow(builder,
                    projection.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    projection.Status.ToString(),
                    mapping.MappingId,
                    mapping.Kind.ToString(),
                    mapping.Direction?.ToString(),
                    mapping.Status.ToString(),
                    mapping.Confidence.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
                    mapping.IsMandatory.ToString().ToLowerInvariant(),
                    mapping.Source.ExpandedNodeId,
                    mapping.Source.BrowsePath,
                    mapping.Target?.ExpandedNodeId,
                    mapping.Target?.BrowsePath,
                    projection.TargetType?.ExpandedNodeId,
                    projection.TargetType?.ModelUri,
                    mapping.DeclarationPath,
                    mapping.TargetReferenceType,
                    mapping.TargetBrowseNameNamespaceUri);
            }
        }
        return builder.ToString();
    }

    static void AppendCsvRow(StringBuilder builder, params string?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(',');
            var value = values[i] ?? "";
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            else
                builder.Append(value);
        }
        builder.AppendLine();
    }

    static string CreateMarkdown(CompanionProjectionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Companion projection report");
        builder.AppendLine();
        builder.AppendLine($"- Output model: `{report.Model.OutputModelUri}`");
        builder.AppendLine($"- Source nodes: {report.SourceNodeCount}");
        builder.AppendLine($"- Included nodes: {report.IncludedNodeCount}");
        builder.AppendLine($"- Accepted projections: {report.AcceptedProjectionCount}");
        builder.AppendLine($"- Proposed projections: {report.ProposedProjectionCount}");
        builder.AppendLine($"- Unresolved projections: {report.UnresolvedProjectionCount}");
        builder.AppendLine();
        builder.AppendLine("| Source | Target type | Status | Confidence | Blocking gaps |");
        builder.AppendLine("|---|---|---:|---:|---|");
        foreach (var projection in report.Projections.OrderBy(p => p.Ordinal))
        {
            builder.Append("| ").Append(EscapeMarkdown(projection.SourceRoot.BrowsePath ?? projection.SourceRoot.BrowseName))
                .Append(" | ").Append(EscapeMarkdown(projection.TargetType?.BrowseName ?? "—"))
                .Append(" | ").Append(projection.Status)
                .Append(" | ").Append(projection.Confidence.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" | ").Append(EscapeMarkdown(string.Join("; ", projection.BlockingGaps)))
                .AppendLine(" |");
        }
        if (report.Warnings.Count > 0)
        {
            builder.AppendLine().AppendLine("## Warnings").AppendLine();
            foreach (var warning in report.Warnings)
                builder.Append("- ").AppendLine(warning);
        }
        return builder.ToString();
    }

    static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
