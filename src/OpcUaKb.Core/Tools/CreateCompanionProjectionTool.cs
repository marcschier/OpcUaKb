using System.ComponentModel;
using System.Text.Json;
using Azure;
using ModelContextProtocol.Server;

[McpServerToolType]
static class CreateCompanionProjectionTool
{
    [McpServerTool(Name = "create_companion_projection"),
     Description("Create a durable background job that analyzes a source OPC UA NodeSet and projects it " +
        "into companion-model mapping artifacts. The job is content-addressed and idempotent: submitting " +
        "the same NodeSet and canonical options returns the same job_id. Provide exactly ONE input source: " +
        "(a) nodeset_xml only for tiny snippets (≤30 KB); (b) nodeset_ref such as " +
        "blob:uploads/{sha256}.xml from POST /upload-nodeset; or (c) an allow-listed HTTPS nodeset_url. " +
        "Real NodeSets are usually large, so prefer nodeset_ref or nodeset_url. The call only stages and " +
        "queues work; poll get_companion_projection for progress and authenticated artifact downloads.")]
    public static async Task<string> CreateCompanionProjection(
        CompanionProjectionJobService jobs,
        [Description("Optional inline NodeSet XML, hard-capped at 30 KB. Do not use for normal full NodeSets.")]
        string? nodeset_xml = null,
        [Description("Optional durable source reference, normally blob:uploads/{sha256}.xml or another blob path in MCP_NODESET_CONTAINER.")]
        string? nodeset_ref = null,
        [Description("Optional allow-listed absolute HTTPS URL. Redirects are not followed.")]
        string? nodeset_url = null,
        [Description("Optional absolute URI for the projected companion model namespace/model URI.")]
        string? output_model_uri = null,
        [Description("Optional browse paths to include. Empty means all eligible paths. Maximum 256.")]
        string[]? include_browse_paths = null,
        [Description("Optional browse paths to exclude. Exclusions are applied after inclusions. Maximum 256.")]
        string[]? exclude_browse_paths = null,
        [Description("Confidence threshold from 0 to 1 for automatically accepted projections. Default 0.82.")]
        double accepted_confidence_threshold = 0.82,
        [Description("Confidence threshold from 0 to 1 for proposed projections. Default 0.55 and must not exceed accepted_confidence_threshold.")]
        double proposed_confidence_threshold = 0.55,
        [Description("Maximum projection alternatives retained per source entity, from 1 to 10. Default 3.")]
        int max_projections_per_source = 3)
    {
        try
        {
            var creation = await jobs.CreateAsync(
                nodeset_xml,
                nodeset_ref,
                nodeset_url,
                new CompanionProjectionJobOptions
                {
                    OutputModelUri = output_model_uri,
                    IncludeBrowsePaths = include_browse_paths ?? [],
                    ExcludeBrowsePaths = exclude_browse_paths ?? [],
                    AcceptedConfidenceThreshold = accepted_confidence_threshold,
                    ProposedConfidenceThreshold = proposed_confidence_threshold,
                    MaxProjectionsPerSource = max_projections_per_source,
                });
            var status = creation.Job.Status;
            return JsonSerializer.Serialize(new
            {
                job_id = creation.Job.Request.JobId,
                status = status.State.ToString().ToLowerInvariant(),
                existing = creation.Existing,
                status_hint = "Poll get_companion_projection with this job_id. Completed artifact URLs require the MCP_ARTIFACT_KEY api-key.",
                progress = status.Progress,
            }, CompanionProjectionJobStore.JsonOptions);
        }
        catch (Exception ex) when (ex is NodeSetLoadException
            or ArgumentException
            or InvalidOperationException
            or RequestFailedException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "job_not_created",
                error_description = SafeMessage(ex),
            }, CompanionProjectionJobStore.JsonOptions);
        }
    }

    static string SafeMessage(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 2048 ? message : message[..2048];
    }
}
