public sealed class CompanionProjectionEngineProcessor : ICompanionProjectionProcessor
{
    readonly ICompanionModelRepository _models;
    readonly SearchService _search;
    readonly AoaiChatClient _chat;
    readonly AddressSpaceNodeSetReader _reader;
    readonly ModelMappingArtifactWriter _artifacts;

    public CompanionProjectionEngineProcessor(
        ICompanionModelRepository models,
        SearchService search,
        AoaiChatClient chat,
        AddressSpaceNodeSetReader reader,
        ModelMappingArtifactWriter artifacts)
    {
        _models = models;
        _search = search;
        _chat = chat;
        _reader = reader;
        _artifacts = artifacts;
    }

    public async Task<CompanionProjectionProcessResult> ProcessAsync(
        CompanionProjectionProcessContext context,
        CancellationToken cancellationToken)
    {
        await context.ReportProgressAsync(new CompanionProjectionProgress
        {
            Checkpoint = "reading_nodeset",
            Message = "Reading the staged NodeSet.",
        }, cancellationToken);

        var graph = await _reader.ReadAsync(context.Input, cancellationToken);
        await context.ReportProgressAsync(new CompanionProjectionProgress
        {
            Checkpoint = "matching_companion_types",
            ProcessedNodes = 0,
            TotalNodes = graph.Nodes.Count,
            Message = "Filtering the address space and matching companion types.",
        }, cancellationToken);

        var filter = new AddressSpaceCoreFilter(_models);
        var candidates = new CompanionCandidateService(_search, _models);
        var reasoner = new CompanionSemanticReasoner(_chat, new CompanionReasonerOptions
        {
            AcceptedThreshold = context.Options.AcceptedConfidenceThreshold,
            ProposedThreshold = context.Options.ProposedConfidenceThreshold,
            MaxProjectionsPerSource = context.Options.MaxProjectionsPerSource,
        });
        var engine = new CompanionProjectionEngine(filter, candidates, reasoner, _models);
        var engineRequest = new CompanionProjectionJobRequest
        {
            JobId = context.JobId,
            OutputModelUri = context.Options.OutputModelUri
                ?? $"urn:opcua-kb:companion-projection:{context.JobId}/",
            SourceName = $"{context.JobId}/input.xml",
            IncludePaths = context.Options.IncludeBrowsePaths,
            ExcludePaths = context.Options.ExcludeBrowsePaths,
            MaxProjectionsPerSource = context.Options.MaxProjectionsPerSource,
            AcceptedThreshold = context.Options.AcceptedConfidenceThreshold,
            ProposedThreshold = context.Options.ProposedConfidenceThreshold,
        };
        var result = await engine.ProjectAsync(graph, engineRequest, cancellationToken);

        await context.ReportProgressAsync(new CompanionProjectionProgress
        {
            Checkpoint = "writing_artifacts",
            ProcessedNodes = graph.Nodes.Count,
            TotalNodes = graph.Nodes.Count,
            CandidateCount = result.Mapping.Projections.Count,
            ProjectionCount = result.Mapping.Projections.Count,
            WarningCount = result.Report.Warnings.Count,
            Message = "Writing mapping, report, NodeSet, and bundle artifacts.",
        }, cancellationToken);

        var bundle = await _artifacts.WriteAsync(result, cancellationToken);
        await WriteArtifactAsync(
            context, "projection.nodeset2.xml", bundle.ProjectionNodeSet, cancellationToken);
        await WriteArtifactAsync(
            context, "mapping.json", bundle.MappingJson, cancellationToken);
        await WriteArtifactAsync(
            context, "mapping.csv", bundle.MappingCsv, cancellationToken);
        await WriteArtifactAsync(
            context, "report.json", bundle.ReportJson, cancellationToken);
        await WriteArtifactAsync(
            context, "report.md", bundle.ReportMarkdown, cancellationToken);
        await WriteArtifactAsync(
            context, "bundle.zip", bundle.Zip, cancellationToken);

        return new CompanionProjectionProcessResult
        {
            Checkpoint = "completed",
            ProcessedNodes = graph.Nodes.Count,
            TotalNodes = graph.Nodes.Count,
            CandidateCount = result.Mapping.Projections.Count,
            ProjectionCount = result.Mapping.Projections.Count,
            WarningCount = result.Report.Warnings.Count,
            Message = "Companion projection artifacts are ready.",
        };
    }

    static async Task WriteArtifactAsync(
        CompanionProjectionProcessContext context,
        string fileName,
        CompanionGeneratedArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var content = artifact.OpenRead();
        await context.Artifacts.WriteAsync(
            fileName, content, artifact.Metadata.MediaType, cancellationToken);
    }

}
