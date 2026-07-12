using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;

public sealed class CompanionProjectionJobService
{
    const long DefaultMaxInputBytes = NodeSetLoader.DefaultMaxFetchBytes;

    static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    readonly CompanionProjectionJobStore _store;
    readonly QueueClient _queue;
    readonly NodeSetLoader _loader;
    readonly long _maxInputBytes;
    readonly string? _unavailableReason;

    public CompanionProjectionJobService(
        CompanionProjectionJobStore store,
        QueueClient queue,
        NodeSetLoader loader)
    {
        _store = store;
        _queue = queue;
        _loader = loader;
        _maxInputBytes = long.TryParse(
            Environment.GetEnvironmentVariable("MCP_NODESET_MAX_BYTES"), out var configured)
            && configured > 0
                ? configured
                : DefaultMaxInputBytes;
    }

    public CompanionProjectionJobService(string unavailableReason)
    {
        _unavailableReason = string.IsNullOrWhiteSpace(unavailableReason)
            ? "Companion projection storage is not configured."
            : unavailableReason;
        _store = null!;
        _queue = null!;
        _loader = null!;
        _maxInputBytes = DefaultMaxInputBytes;
    }

    public async Task<CompanionProjectionJobCreation> CreateAsync(
        string? nodesetXml,
        string? nodesetRef,
        string? nodesetUrl,
        CompanionProjectionJobOptions options,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var normalizedOptions = NormalizeOptions(options);
        var source = await _loader.ResolveAsync(
            nodesetXml, nodesetRef, nodesetUrl, cancellationToken);

        string inputSha256;
        long inputSize;
        await using (var hashStream = await source.OpenAsync())
        {
            (inputSha256, inputSize) = await CompanionProjectionJobStore.HashAsync(
                hashStream, _maxInputBytes, cancellationToken);
        }

        var canonicalOptions = CanonicalizeOptions(normalizedOptions);
        var jobId = DeriveJobId(inputSha256, canonicalOptions);
        var inputBlobName = _store.GetInputBlobName(jobId);
        var now = DateTimeOffset.UtcNow;
        var request = new CompanionProjectionDurableJobRequest
        {
            JobId = jobId,
            InputSha256 = inputSha256,
            InputSizeBytes = inputSize,
            InputBlobName = inputBlobName,
            SourceMode = SourceMode(nodesetXml, nodesetRef),
            SourceRef = string.IsNullOrWhiteSpace(nodesetRef) ? null : nodesetRef,
            SourceUrl = string.IsNullOrWhiteSpace(nodesetUrl) ? null : nodesetUrl,
            Options = normalizedOptions,
            CreatedAt = now,
        };
        var initialStatus = new CompanionProjectionDurableJobStatus
        {
            JobId = jobId,
            State = CompanionProjectionQueueJobState.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Progress = new CompanionProjectionProgress
            {
                Checkpoint = "input_staged",
                Message = "Input staged; waiting for a mapping worker.",
            },
        };

        // Fast-return: only inline sources are staged here (they cannot be
        // re-fetched later). blob:/URL sources are staged by the worker via
        // EnsureInputStagedAsync so the tool call returns promptly instead of
        // blocking on private-endpoint blob round-trips.
        if (string.IsNullOrWhiteSpace(nodesetRef) && string.IsNullOrWhiteSpace(nodesetUrl))
        {
            await StageFromSourceAsync(jobId, source, inputSha256, cancellationToken);
        }

        try
        {
            await _store.WriteRequestAsync(request, onlyIfAbsent: true, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Content-derived job already exists.
        }

        var created = false;
        try
        {
            await _store.WriteStatusAsync(
                initialStatus, onlyIfAbsent: true, cancellationToken: cancellationToken);
            created = true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Content-derived job already exists.
        }

        var shouldEnqueue = created;
        if (!created)
        {
            var existingStatus = await _store.TryReadStatusAsync(jobId, cancellationToken);
            if (existingStatus is
                {
                    State: CompanionProjectionQueueJobState.Failed,
                    Terminal: true,
                })
            {
                // An explicit resubmission after a terminal failure is a retry.
                // This allows corrected service/model state to recover the same
                // content-derived job without inventing a different input hash.
                initialStatus = initialStatus with
                {
                    CreatedAt = existingStatus.CreatedAt,
                    Progress = initialStatus.Progress with
                    {
                        Message = "Failed job resubmitted; waiting for a mapping worker.",
                    },
                };
                await _store.WriteStatusAsync(
                    initialStatus, cancellationToken: cancellationToken);
                shouldEnqueue = true;
            }
        }

        // Queue send and blob writes cannot be transactional. The durable marker
        // makes retries at-least-once; duplicate messages are harmless because the
        // worker locks status.json and treats terminal states idempotently.
        if (shouldEnqueue || !await _store.HasEnqueuedMarkerAsync(jobId, cancellationToken))
        {
            await _queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            var message = BinaryData.FromObjectAsJson(
                new CompanionProjectionQueueMessage { JobId = jobId }, CompactJson);
            await _queue.SendMessageAsync(message.ToString(), cancellationToken);
            await _store.TryWriteEnqueuedMarkerAsync(jobId, cancellationToken);
        }

        var snapshot = await GetAsync(jobId, cancellationToken)
            ?? new CompanionProjectionJobSnapshot { Request = request, Status = initialStatus };
        return new CompanionProjectionJobCreation
        {
            Job = snapshot,
            Existing = !created,
        };
    }

    public async Task<CompanionProjectionJobSnapshot?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        if (!IsValidJobId(jobId))
            throw new ArgumentException("Invalid companion projection job ID.", nameof(jobId));

        var requestTask = _store.TryReadRequestAsync(jobId, cancellationToken);
        var statusTask = _store.TryReadStatusAsync(jobId, cancellationToken);
        await Task.WhenAll(requestTask, statusTask);

        var request = await requestTask;
        var status = await statusTask;
        if (request == null || status == null)
            return null;

        var artifacts = status.Artifacts.Select(a => a with
        {
            DownloadUrl = CompanionProjectionJobStore.DownloadPath(jobId, a.FileName),
        }).ToArray();

        return new CompanionProjectionJobSnapshot
        {
            Request = request,
            Status = status with { Artifacts = artifacts },
        };
    }

    public static bool IsValidJobId(string? jobId)
    {
        if (jobId is not { Length: 43 } || !jobId.StartsWith("cp-", StringComparison.Ordinal))
            return false;
        return jobId.AsSpan(3).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    static CompanionProjectionJobOptions NormalizeOptions(CompanionProjectionJobOptions options)
    {
        if (options.AcceptedConfidenceThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(options.AcceptedConfidenceThreshold), "Accepted confidence threshold must be between 0 and 1.");
        if (options.ProposedConfidenceThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(options.ProposedConfidenceThreshold), "Proposed confidence threshold must be between 0 and 1.");
        if (options.ProposedConfidenceThreshold > options.AcceptedConfidenceThreshold)
            throw new ArgumentException(
                "Proposed confidence threshold must be less than or equal to accepted confidence threshold.");
        if (options.MaxProjectionsPerSource is < 1 or > 10)
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxProjectionsPerSource), "max_projections_per_source must be between 1 and 10.");

        string? outputModelUri = null;
        if (!string.IsNullOrWhiteSpace(options.OutputModelUri))
        {
            outputModelUri = options.OutputModelUri.Trim();
            if (!Uri.TryCreate(outputModelUri, UriKind.Absolute, out _))
                throw new ArgumentException("output_model_uri must be an absolute URI.");
        }

        return options with
        {
            OutputModelUri = outputModelUri,
            IncludeBrowsePaths = NormalizePaths(options.IncludeBrowsePaths, "include_browse_paths"),
            ExcludeBrowsePaths = NormalizePaths(options.ExcludeBrowsePaths, "exclude_browse_paths"),
        };
    }

    static IReadOnlyList<string> NormalizePaths(
        IReadOnlyList<string>? paths,
        string parameterName)
    {
        if (paths == null || paths.Count == 0)
            return [];
        if (paths.Count > 256)
            throw new ArgumentException($"{parameterName} accepts at most 256 paths.");

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in paths)
        {
            var path = raw?.Trim();
            if (string.IsNullOrEmpty(path))
                continue;
            if (path.Length > 1024)
                throw new ArgumentException($"{parameterName} paths must be at most 1024 characters.");
            normalized.Add(path);
        }
        return [.. normalized];
    }

    static string CanonicalizeOptions(CompanionProjectionJobOptions options)
    {
        return JsonSerializer.Serialize(new
        {
            output_model_uri = options.OutputModelUri?.Normalize(NormalizationForm.FormC),
            include_browse_paths = options.IncludeBrowsePaths
                .Select(value => value.Normalize(NormalizationForm.FormC))
                .ToArray(),
            exclude_browse_paths = options.ExcludeBrowsePaths
                .Select(value => value.Normalize(NormalizationForm.FormC))
                .ToArray(),
            accepted_confidence_threshold = options.AcceptedConfidenceThreshold,
            proposed_confidence_threshold = options.ProposedConfidenceThreshold,
            max_projections_per_source = options.MaxProjectionsPerSource,
        }, CompactJson);
    }

    static string DeriveJobId(string inputSha256, string canonicalOptions)
    {
        var material = Encoding.UTF8.GetBytes($"{inputSha256}\n{canonicalOptions}");
        var hash = SHA256.HashData(material);
        return $"cp-{Convert.ToHexStringLower(hash)[..40]}";
    }

    static string SourceMode(string? nodesetXml, string? nodesetRef) =>
        !string.IsNullOrWhiteSpace(nodesetXml)
            ? "inline"
            : !string.IsNullOrWhiteSpace(nodesetRef) ? "ref" : "url";

    // Stage the source NodeSet into the job's content-addressed input.xml in a
    // single pass: stream source -> input.xml while computing SHA256, then
    // compare to the expected content hash. No SyncCopyFromUri (rejected behind
    // private endpoints) and no separate verify GET.
    async Task StageFromSourceAsync(
        string jobId,
        NodeSetSource source,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var destination = _store.GetInputBlob(jobId);
        if ((await destination.ExistsAsync(cancellationToken)).Value)
            return;

        await using var input = await source.OpenAsync();
        using var bounded = new SizeBoundedStream(input, _maxInputBytes, leaveInnerOpen: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var tee = new HashingStream(bounded, hash, leaveInnerOpen: true);
        try
        {
            await destination.UploadAsync(tee, new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/xml" },
                Metadata = new Dictionary<string, string> { ["sha256"] = expectedSha256 },
                TransferOptions = new Azure.Storage.StorageTransferOptions
                {
                    InitialTransferSize = 4 * 1024 * 1024,
                    MaximumTransferSize = 4 * 1024 * 1024,
                    MaximumConcurrency = 1,
                },
            }, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Another idempotent stager wrote the same content-addressed input.
            return;
        }

        var actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            await destination.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            throw new NodeSetLoadException(
                "The source NodeSet changed while it was being staged. Retry the request so the job ID matches the staged bytes.");
        }
    }

    // Worker-side staging for deferred (blob:/URL) sources. Idempotent: returns
    // immediately when input.xml already exists (inline jobs, retries, or an
    // earlier attempt). Throws a terminal error when neither a staged input nor
    // a re-fetchable source reference is available.
    public async Task EnsureInputStagedAsync(
        CompanionProjectionDurableJobRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAvailable();
        var destination = _store.GetInputBlob(request.JobId);
        if ((await destination.ExistsAsync(cancellationToken)).Value)
            return;

        if (string.IsNullOrWhiteSpace(request.SourceRef)
            && string.IsNullOrWhiteSpace(request.SourceUrl))
        {
            throw new CompanionProjectionTerminalException(
                "Job input is not staged and no re-fetchable source reference is available.");
        }

        var source = await _loader.ResolveAsync(
            null, request.SourceRef, request.SourceUrl, cancellationToken);
        await StageFromSourceAsync(
            request.JobId, source, request.InputSha256, cancellationToken);
    }

    void EnsureAvailable()
    {
        if (_unavailableReason is not null)
            throw new InvalidOperationException(_unavailableReason);
    }
}
