using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// ═══════════════════════════════════════════════════════════════════════
// OPC UA Knowledge Base — Custom MCP Server
// Exposes structured search tools over the Azure AI Search index.
//
// Supports three transport modes:
//   HTTP/SSE (default): Run as a web server for hosted deployment
//   stdio:              Pass --stdio for local Copilot CLI usage
//   mapping worker:     Pass --mapping-worker for queue processing only
//
// Required env vars: SEARCH_ENDPOINT, SEARCH_API_KEY
// Optional: SEARCH_INDEX_NAME (default: opcua-content-index-v2)
//           AOAI_ENDPOINT — enables search_docs_rag tool (KB retrieve + GPT-4o)
//           AOAI_API_KEY  — AOAI key auth (falls back to Managed Identity)
//           KB_NAME       — knowledge base name (default: opcua-kb)
//           STORAGE_ACCOUNT_NAME — enables nodeset_ref=blob:... and the
//                                  POST /upload-nodeset endpoint
//           MAPPING_QUEUE_NAME — default model-mapping-jobs
//           MAPPING_PREFIX — default model-mappings/jobs
//           MCP_ARTIFACT_KEY — gates private mapping artifact downloads;
//                              falls back only to MCP_UPLOAD_KEY
//           MCP_MAPPING_KEY — gates create_companion_projection;
//                             falls back to MCP_UPLOAD_KEY, then explicit
//                             MCP_API_KEY (never SEARCH_API_KEY)
//
// Rate limiting env vars:
//   MCP_API_KEY           — API key for authenticated access
//   MCP_REQUIRE_AUTH      — "true" to reject all unauthenticated requests
//   MCP_ANON_RATE_LIMIT   — Max requests/min for anonymous callers (default: 10)
//   MCP_AUTH_RATE_LIMIT   — Max requests/min for authenticated callers (default: 0 = unlimited)
//
// NodeSet input limits:
//   MCP_NODESET_MAX_BYTES — default 52428800 (50 MB) — caps both inline
//                           uploads and outbound URL fetches.
//   MCP_NODESET_URL_ALLOWLIST — comma-separated host patterns for
//                               nodeset_url fetches.
// ═══════════════════════════════════════════════════════════════════════

const long DefaultMaxRequestBodyBytes = 64L * 1024 * 1024;
const int MaxMcpAuthInspectionBytes = 1024 * 1024;

var useStdio = args.Contains("--stdio");
var useMappingWorker = args.Contains("--mapping-worker");

if (useMappingWorker)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
    builder.Services.AddSingleton<ProfileGraphService>();

    var storage = RegisterMappingStorage(builder.Services, required: true);
    builder.Services.AddSingleton(_ => CreateNodeSetLoader(storage.Container));

    builder.Services.AddSingleton<
        ICompanionProjectionProcessor,
        CompanionProjectionEngineProcessor>();
    builder.Services.AddHostedService<CompanionProjectionWorker>();
    await builder.Build().RunAsync();
}
else if (useStdio)
{
    // stdio transport for local CLI usage — no auth or rate limiting needed
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
    builder.Services.AddSingleton<ProfileGraphService>();
    var storage = RegisterMappingStorage(builder.Services, required: false);
    builder.Services.AddSingleton(_ => CreateNodeSetLoader(storage.Container));
    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(SearchNodesTool).Assembly);
    await builder.Build().RunAsync();
}
else
{
    // HTTP/SSE transport for hosted deployment
    var builder = WebApplication.CreateBuilder(args);

    // Allow the upload endpoint to receive NodeSets up to MCP_NODESET_MAX_BYTES
    // (default 50 MB) — Kestrel's default 30 MB cap is too small.
    var maxFetchBytes = long.TryParse(
        Environment.GetEnvironmentVariable("MCP_NODESET_MAX_BYTES"), out var mfb) && mfb > 0
            ? mfb : NodeSetLoader.DefaultMaxFetchBytes;
    var maxBodyBytes = Math.Max(maxFetchBytes + 1024 * 1024, DefaultMaxRequestBodyBytes);
    builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);

    builder.Services.AddSingleton<SearchService>();
    builder.Services.AddSingleton<KbService>();
    builder.Services.AddSingleton<ProfileGraphService>();

    // Optional BlobContainerClient for the upload endpoint. When the env
    // var isn't set, /upload-nodeset returns 503.
    var storage = RegisterMappingStorage(builder.Services, required: false);
    var uploadContainer = storage.Container;
    builder.Services.AddSingleton(_ => CreateNodeSetLoader(uploadContainer));

    builder.Services
        .AddMcpServer(o => o.ServerInfo = new() { Name = "opcua-kb", Version = "1.0.0" })
        .WithHttpTransport(o => o.Stateless = true)
        .WithToolsFromAssembly(typeof(SearchNodesTool).Assembly);

    // Configuration. We split two keys:
    //   • MCP_API_KEY (read) — gates anonymous read tier (falls back to
    //                          SEARCH_API_KEY for backward compat).
    //   • MCP_UPLOAD_KEY (write) — REQUIRED for /upload-nodeset. Defaults
    //                              to MCP_API_KEY when not set; falls back
    //                              to nothing (endpoint disabled) when MCP_API_KEY
    //                              also isn't set. Never falls back to SEARCH_API_KEY.
    var readApiKey = Environment.GetEnvironmentVariable("MCP_API_KEY")
        ?? Environment.GetEnvironmentVariable("SEARCH_API_KEY");
    var explicitMcpApiKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
    var uploadApiKey = Environment.GetEnvironmentVariable("MCP_UPLOAD_KEY")
        ?? explicitMcpApiKey;
    var artifactApiKey = Environment.GetEnvironmentVariable("MCP_ARTIFACT_KEY")
        ?? Environment.GetEnvironmentVariable("MCP_UPLOAD_KEY");
    var mappingApiKey = Environment.GetEnvironmentVariable("MCP_MAPPING_KEY")
        ?? Environment.GetEnvironmentVariable("MCP_UPLOAD_KEY")
        ?? explicitMcpApiKey;

    var requireAuth = string.Equals(
        Environment.GetEnvironmentVariable("MCP_REQUIRE_AUTH"), "true", StringComparison.OrdinalIgnoreCase);
    var anonRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_ANON_RATE_LIMIT"), out var arl) ? arl : 10;
    var authRateLimit = int.TryParse(Environment.GetEnvironmentVariable("MCP_AUTH_RATE_LIMIT"), out var atrl) ? atrl : 0;

    // Rate limiting — partitioned by authenticated vs anonymous (per-IP)
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsync(
                """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Rate limit exceeded. Provide an api-key header for higher limits."},"id":""}""", ct);
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            var providedKey = context.Request.Headers.TryGetValue("api-key", out var key)
                ? key.ToString()
                : null;
            var expectedKey = context.Request.Path.StartsWithSegments("/upload-nodeset")
                ? uploadApiKey
                : context.Request.Path.StartsWithSegments("/mapping-artifacts")
                    ? artifactApiKey
                    : readApiKey;
            var hasValidKey = KeyEquals(providedKey, expectedKey)
                || KeyEquals(providedKey, mappingApiKey);

            if (hasValidKey)
            {
                // Authenticated tier — unlimited or configurable
                return authRateLimit > 0
                    ? RateLimitPartition.GetFixedWindowLimiter("authenticated", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = authRateLimit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter("authenticated");
            }

            // Anonymous tier — rate limited per IP
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"anon:{ip}", _ =>
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = anonRateLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });
    });

    var app = builder.Build();

    // Middleware order: rate limiting → OAuth endpoint handling → auth → MCP
    app.UseRateLimiter();

    // Handle OAuth discovery and fallback endpoints explicitly.
    // MCP clients MUST check /.well-known/oauth-authorization-server per RFC 8414.
    // Returning 404 tells clients there is no authorization server — auth is not required.
    // The fallback /authorize, /token, /register paths return 400 with a clear message
    // instead of falling through to the MCP handler (which would return confusing errors).
    app.Map("/authorize", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/token", () => Results.Json(
        new { error = "unsupported_grant_type", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));
    app.Map("/register", () => Results.Json(
        new { error = "invalid_client", error_description = "This server uses api-key header authentication. OAuth is not supported." },
        statusCode: 400));

    // Auth middleware — block or allow anonymous based on config.
    // /upload-nodeset always uses its own explicit MCP_UPLOAD_KEY/MCP_API_KEY
    // (never SEARCH_API_KEY) — see below.
    app.Use(async (context, next) =>
    {
        var isUploadEndpoint = context.Request.Path.StartsWithSegments("/upload-nodeset");
        var isArtifactEndpoint = context.Request.Path.StartsWithSegments("/mapping-artifacts");

        if (isArtifactEndpoint)
        {
            if (string.IsNullOrEmpty(artifactApiKey))
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"artifact_download_disabled","error_description":"Set MCP_ARTIFACT_KEY (or MCP_UPLOAD_KEY) to enable mapping artifact downloads."}""");
                return;
            }

            var providedArtifact = context.Request.Headers.TryGetValue("api-key", out var a)
                ? a.ToString()
                : null;
            if (!KeyEquals(providedArtifact, artifactApiKey))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"unauthorized","error_description":"Valid api-key header required for mapping artifact downloads."}""");
                return;
            }

            await next();
            return;
        }

        if (isUploadEndpoint)
        {
            if (string.IsNullOrEmpty(uploadApiKey))
            {
                context.Response.StatusCode = 503;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"upload_disabled","error_description":"Set MCP_API_KEY (or MCP_UPLOAD_KEY) to enable the upload endpoint."}""");
                return;
            }

            var providedUpload = context.Request.Headers.TryGetValue("api-key", out var u) ? u.ToString() : null;
            if (!KeyEquals(providedUpload, uploadApiKey))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"unauthorized","error_description":"Valid api-key header required for /upload-nodeset."}""");
                return;
            }

            await next();
            return;
        }

        var mappingInspection = await InspectMappingCallAsync(
            context.Request, MaxMcpAuthInspectionBytes, context.RequestAborted);
        if (mappingInspection == McpCallInspection.Oversized)
        {
            var providedMapping = context.Request.Headers.TryGetValue("api-key", out var oversizedKey)
                ? oversizedKey.ToString()
                : null;
            if (!KeyEquals(providedMapping, mappingApiKey))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","error":{"code":-32002,"message":"MCP request is too large for bounded write-auth inspection."},"id":null}""");
                return;
            }

            // A valid mapping key authorizes an oversized request without
            // buffering it. The MCP transport still enforces its normal limits.
            await next();
            return;
        }

        if (mappingInspection == McpCallInspection.CreateCompanionProjection)
        {
            // create_companion_projection is gated like the read tools: the
            // mapping key is enforced only when the server requires auth
            // (MCP_REQUIRE_AUTH=true). When anonymous reads are allowed it is
            // permitted anonymously too — bounded by the anonymous rate limit —
            // so MCP clients that don't forward the api-key header can invoke it.
            if (requireAuth)
            {
                if (string.IsNullOrEmpty(mappingApiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"jsonrpc":"2.0","error":{"code":-32001,"message":"create_companion_projection is disabled because MCP_MAPPING_KEY, MCP_UPLOAD_KEY, and explicit MCP_API_KEY are not configured."},"id":null}""");
                    return;
                }

                var providedMapping = context.Request.Headers.TryGetValue("api-key", out var mappingKey)
                    ? mappingKey.ToString()
                    : null;
                if (!KeyEquals(providedMapping, mappingApiKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Valid api-key header required for create_companion_projection."},"id":null}""");
                    return;
                }
            }

            await next();
            return;
        }

        // MCP / other endpoints — fall back to the original read-side policy.
        if (!string.IsNullOrEmpty(readApiKey) && requireAuth)
        {
            var hasValidKey = context.Request.Headers.TryGetValue("api-key", out var providedKey)
                && KeyEquals(providedKey.ToString(), readApiKey);
            if (!hasValidKey)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Unauthorized: provide a valid api-key header"},"id":""}""");
                return;
            }
        }

        await next();
    });

    // POST /upload-nodeset — content-addressed NodeSet upload, fully streamed
    // (no whole-payload memory buffering).
    //
    // Pipeline: req.Body → SizeBoundedStream → HashingStream(IncrementalHash)
    //                    → BlobClient.UploadAsync(stream) staging blob.
    //
    // After upload completes, the SHA256 is finalized and we attempt to
    // server-side copy the staging blob to the content-addressed final name
    // (uploads/{sha256}.xml). If copy fails — e.g. behind a private endpoint
    // where source authorization can be touchy — we gracefully fall back to
    // returning the staging blob ref. Lifecycle policy cleans both paths
    // (uploads/.staging/* and uploads/*) after 1 day either way.
    app.MapPost("/upload-nodeset", async (HttpRequest req, CancellationToken ct) =>
    {
        if (uploadContainer == null)
        {
            return Results.Json(
                new { error = "upload_disabled", error_description = "STORAGE_ACCOUNT_NAME is not configured." },
                statusCode: 503);
        }

        Stream? input = null;
        var stagingName = $"uploads/.staging/{Guid.NewGuid():N}.xml";
        var stagingBlob = uploadContainer.GetBlobClient(stagingName);
        try
        {
            input = await ExtractUploadStreamAsync(req, ct);
            if (input == null)
            {
                return Results.Json(
                    new { error = "no_content", error_description = "Provide an XML body, or multipart/form-data with a 'file' part." },
                    statusCode: 400);
            }

            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Order: input → bounded → hashing tee. The SDK pulls from the
            // hashing tee, which feeds every byte into IncrementalHash *and*
            // forwards it to the SDK. No CryptoStream — its Dispose/FlushFinal
            // semantics fight with Azure SDK's read-to-EOF pattern.
            // leaveInnerOpen=true so disposing the wrapper doesn't cascade
            // into closing req.Body (Kestrel manages that).
            using var bounded = new SizeBoundedStream(input, maxFetchBytes, leaveInnerOpen: true);
            using var tee = new HashingStream(bounded, incrementalHash, leaveInnerOpen: true);

            // Stream the upload to staging. Azure SDK chunks into block
            // uploads — no whole-payload memory materialization.
            await stagingBlob.UploadAsync(tee, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/xml" },
                TransferOptions = new Azure.Storage.StorageTransferOptions
                {
                    InitialTransferSize = 4 * 1024 * 1024,
                    MaximumTransferSize = 4 * 1024 * 1024,
                    MaximumConcurrency = 1, // sequential — preserves hash order
                },
            }, ct);

            // Hash is now final.
            var sha256 = Convert.ToHexStringLower(incrementalHash.GetHashAndReset());
            var size = bounded.BytesRead;

            // Attempt content-addressed promotion.
            var finalName = $"uploads/{sha256}.xml";
            var finalBlob = uploadContainer.GetBlobClient(finalName);
            var finalRef = $"blob:{finalName}";

            try
            {
                // If the final already exists, dedup hit — skip the copy.
                var exists = await finalBlob.ExistsAsync(ct);
                if (!exists.Value)
                {
                    var copyOp = await finalBlob.SyncCopyFromUriAsync(stagingBlob.Uri, cancellationToken: ct);
                    if (copyOp.Value.CopyStatus != CopyStatus.Success)
                    {
                        // Server-side copy didn't complete synchronously (rare for
                        // intra-account copies). Fall back to returning the staging
                        // ref — still valid, lifecycle policy reaps it.
                        finalRef = $"blob:{stagingName}";
                        return Results.Json(new
                        {
                            nodeset_ref = finalRef,
                            size_bytes = size,
                            sha256,
                            dedup = false,
                            note = "Server-side copy did not complete; returning staging ref.",
                        });
                    }
                }

                // Successful copy or dedup hit — drop staging.
                await stagingBlob.DeleteIfExistsAsync(cancellationToken: ct);

                return Results.Json(new
                {
                    nodeset_ref = finalRef,
                    size_bytes = size,
                    sha256,
                    dedup = exists.Value,
                });
            }
            catch (RequestFailedException copyEx)
            {
                // Copy failed — return staging ref as fallback. Lifecycle
                // policy will reap both paths after 1 day either way.
                return Results.Json(new
                {
                    nodeset_ref = $"blob:{stagingName}",
                    size_bytes = size,
                    sha256,
                    dedup = false,
                    note = $"Content-addressed copy failed ({copyEx.Status} {copyEx.ErrorCode}); returning staging ref.",
                });
            }
        }
        catch (NodeSetLoadException ex)
        {
            // Try to clean up the partial staging blob.
            try { await stagingBlob.DeleteIfExistsAsync(cancellationToken: ct); } catch { /* best-effort */ }
            return Results.Json(
                new { error = "upload_failed", error_description = ex.Message },
                statusCode: 400);
        }
        catch (RequestFailedException ex)
        {
            try { await stagingBlob.DeleteIfExistsAsync(cancellationToken: ct); } catch { /* best-effort */ }
            return Results.Json(
                new { error = "storage_failure", error_description = $"{ex.Status} {ex.ErrorCode}" },
                statusCode: 502);
        }
        finally
        {
            if (input != null) await input.DisposeAsync();
        }
    }).DisableAntiforgery();

    app.MapGet("/mapping-artifacts/{jobId}/{fileName}", async (
        string jobId,
        string fileName,
        HttpResponse response,
        CancellationToken ct) =>
    {
        if (uploadContainer == null)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await response.WriteAsJsonAsync(new
            {
                error = "artifact_download_disabled",
                error_description = "STORAGE_ACCOUNT_NAME is not configured.",
            }, cancellationToken: ct);
            return;
        }

        if (!CompanionProjectionJobService.IsValidJobId(jobId)
            || !CompanionProjectionJobStore.IsArtifactFileName(fileName))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var blobName = $"{storage.Prefix}/{jobId}/artifacts/{fileName}";
        var blob = uploadContainer.GetBlobClient(blobName);
        try
        {
            var download = await blob.DownloadStreamingAsync(cancellationToken: ct);
            await using var content = download.Value.Content;
            var details = download.Value.Details;
            var contentType = details.ContentType
                ?? CompanionProjectionJobStore.ContentTypeForArtifact(fileName);

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = contentType;
            response.ContentLength = details.ContentLength;
            response.Headers.ETag = details.ETag.ToString();
            response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            response.Headers.CacheControl = "private, no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";

            if (details.Metadata.TryGetValue("sha256", out var sha256)
                && sha256.Length == 64)
            {
                response.Headers["X-Content-SHA256"] = sha256;
                try
                {
                    var digest = Convert.ToBase64String(Convert.FromHexString(sha256));
                    response.Headers["Content-Digest"] = $"sha-256=:{digest}:";
                }
                catch (FormatException)
                {
                    // Ignore malformed legacy metadata; the blob still streams.
                }
            }

            await content.CopyToAsync(response.Body, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (RequestFailedException ex)
        {
            response.StatusCode = StatusCodes.Status502BadGateway;
            await response.WriteAsJsonAsync(new
            {
                error = "artifact_storage_failure",
                error_description = $"{ex.Status} {ex.ErrorCode}",
            }, cancellationToken: ct);
        }
    });

    app.MapMcp();
    app.Run();
}

static async Task<Stream?> ExtractUploadStreamAsync(HttpRequest req, CancellationToken ct)
{
    var contentType = req.ContentType ?? "";
    if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
    {
        var media = MediaTypeHeaderValue.Parse(contentType);
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return null;

        var reader = new MultipartReader(boundary, req.Body);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) != null)
        {
            var disp = section.GetContentDispositionHeader();
            if (disp == null) continue;
            var name = disp.Name.HasValue ? disp.Name.Value : null;
            var isFile = !string.IsNullOrEmpty(disp.FileName.Value) || !string.IsNullOrEmpty(disp.FileNameStar.Value);
            if (isFile && string.Equals(name, "file", StringComparison.OrdinalIgnoreCase))
            {
                // Hand back the section body directly — caller wraps in
                // SizeBoundedStream + CryptoStream and pipes to blob upload
                // without buffering in this process.
                return section.Body;
            }
        }
        return null;
    }

    // Treat anything else as a raw XML body — text/xml, application/xml,
    // application/octet-stream, or empty Content-Type all work.
    if (req.ContentLength == 0) return null;
    return req.Body;
}

static MappingStorageRegistration RegisterMappingStorage(
    IServiceCollection services,
    bool required)
{
    var accountName = Environment.GetEnvironmentVariable("STORAGE_ACCOUNT_NAME");
    var containerName = Environment.GetEnvironmentVariable("MCP_NODESET_CONTAINER")
        ?? "opcua-content";
    var queueName = Environment.GetEnvironmentVariable("MAPPING_QUEUE_NAME")
        ?? "model-mapping-jobs";
    var prefix = Environment.GetEnvironmentVariable("MAPPING_PREFIX")
        ?? "model-mappings/jobs";

    if (string.IsNullOrWhiteSpace(accountName))
    {
        if (required)
            throw new InvalidOperationException(
                "STORAGE_ACCOUNT_NAME is required for --mapping-worker.");
        services.AddSingleton(new CompanionProjectionJobService(
            "Companion projection is unavailable because STORAGE_ACCOUNT_NAME is not configured."));
        return new MappingStorageRegistration(null, prefix);
    }

    var credential = new DefaultAzureCredential();
    var blobService = new BlobServiceClient(
        new Uri($"https://{accountName}.blob.core.windows.net"),
        credential);
    var container = blobService.GetBlobContainerClient(containerName);
    var queue = new QueueClient(
        new Uri($"https://{accountName}.queue.core.windows.net/{queueName}"),
        credential);
    var store = new CompanionProjectionJobStore(container, prefix);

    services.AddSingleton(blobService);
    services.AddSingleton(container);
    services.AddSingleton(queue);
    services.AddSingleton(store);
    services.AddSingleton<CompanionProjectionJobService>();
    services.AddSingleton<AddressSpaceNodeSetReader>();
    services.AddSingleton<AoaiChatClient>();
    services.AddSingleton<ModelMappingArtifactWriter>();
    services.AddSingleton<CompanionModelRepository>();
    services.AddSingleton<ICompanionModelRepository>(provider =>
        provider.GetRequiredService<CompanionModelRepository>());
    services.AddSingleton<ICompanionModelCatalog>(provider =>
        provider.GetRequiredService<CompanionModelRepository>());
    return new MappingStorageRegistration(container, store.Prefix);
}

static NodeSetLoader CreateNodeSetLoader(BlobContainerClient? container)
{
    var handler = new HttpClientHandler { AllowAutoRedirect = false };
    var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    return new NodeSetLoader(client, container);
}

static async Task<McpCallInspection> InspectMappingCallAsync(
    HttpRequest request,
    int maxBytes,
    CancellationToken cancellationToken)
{
    if (!HttpMethods.IsPost(request.Method) || request.ContentLength == 0)
        return McpCallInspection.Other;
    if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
        return McpCallInspection.Oversized;

    request.EnableBuffering(bufferThreshold: maxBytes, bufferLimit: maxBytes);
    try
    {
        using var document = await JsonDocument.ParseAsync(
            request.Body,
            new JsonDocumentOptions { MaxDepth = 64 },
            cancellationToken);
        return ContainsCreateCompanionProjectionCall(document.RootElement)
            ? McpCallInspection.CreateCompanionProjection
            : McpCallInspection.Other;
    }
    catch (IOException)
    {
        // A chunked body exceeded the in-memory inspection bound.
        return McpCallInspection.Oversized;
    }
    catch (JsonException)
    {
        // Let the MCP transport return its normal JSON-RPC parse error.
        return McpCallInspection.Other;
    }
    finally
    {
        if (request.Body.CanSeek)
            request.Body.Position = 0;
    }
}

static bool ContainsCreateCompanionProjectionCall(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Array)
        return element.EnumerateArray().Any(ContainsCreateCompanionProjectionCall);
    if (element.ValueKind != JsonValueKind.Object)
        return false;
    if (!element.TryGetProperty("method", out var method)
        || method.ValueKind != JsonValueKind.String
        || !string.Equals(method.GetString(), "tools/call", StringComparison.Ordinal))
        return false;
    if (!element.TryGetProperty("params", out var parameters)
        || parameters.ValueKind != JsonValueKind.Object
        || !parameters.TryGetProperty("name", out var name)
        || name.ValueKind != JsonValueKind.String)
        return false;
    return string.Equals(
        name.GetString(), "create_companion_projection", StringComparison.Ordinal);
}

static bool KeyEquals(string? provided, string? expected)
{
    if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        return false;
    var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
    var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(providedHash, expectedHash);
}

sealed record MappingStorageRegistration(
    BlobContainerClient? Container,
    string Prefix);

enum McpCallInspection
{
    Other,
    CreateCompanionProjection,
    Oversized,
}
