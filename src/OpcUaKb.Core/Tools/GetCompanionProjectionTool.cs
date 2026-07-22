using System.ComponentModel;
using System.Text.Json;
using Azure;
using ModelContextProtocol.Server;

[McpServerToolType]
static class GetCompanionProjectionTool
{
    [McpServerTool(Name = "get_companion_projection"),
     Description("Get durable status, checkpoints, progress counters, errors, and artifact references for " +
        "a companion projection job. Poll this after create_companion_projection. When completed, each " +
        "artifact includes a private download_url under /mapping-artifacts; call it with the configured " +
        "MCP_ARTIFACT_KEY (or MCP_UPLOAD_KEY fallback) in the api-key header. No SAS or public blob URLs are returned. " +
        "Returns the job's state, progress counters, errors, and — when complete — artifact references with private download URLs.")]
    public static async Task<string> GetCompanionProjection(
        CompanionProjectionJobService jobs,
        [Description("Content-derived job ID returned by create_companion_projection, e.g. cp-{40 lowercase hex characters}.")]
        string job_id)
    {
        try
        {
            var snapshot = await jobs.GetAsync(job_id);
            if (snapshot == null)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "job_not_found",
                    job_id,
                }, CompanionProjectionJobStore.JsonOptions);
            }

            var status = snapshot.Status;
            return JsonSerializer.Serialize(new
            {
                job_id,
                status = status.State.ToString().ToLowerInvariant(),
                status.Terminal,
                status.CreatedAt,
                status.StartedAt,
                status.UpdatedAt,
                status.CompletedAt,
                status.Attempt,
                status.Progress,
                status.Errors,
                artifacts = status.Artifacts,
            }, CompanionProjectionJobStore.JsonOptions);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "invalid_job_id",
                error_description = ex.Message,
            }, CompanionProjectionJobStore.JsonOptions);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "projection_unavailable",
                error_description = ex.Message,
            }, CompanionProjectionJobStore.JsonOptions);
        }
        catch (RequestFailedException ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = "status_unavailable",
                error_description = $"{ex.Status} {ex.ErrorCode}",
            }, CompanionProjectionJobStore.JsonOptions);
        }
    }
}
