using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class CompanionProjectionWorker : BackgroundService
{
    static readonly TimeSpan MessageVisibility = TimeSpan.FromMinutes(5);
    static readonly TimeSpan StatusLeaseDuration = TimeSpan.FromSeconds(60);

    readonly CompanionProjectionJobStore _store;
    readonly QueueClient _queue;
    readonly ICompanionProjectionProcessor _processor;
    readonly CompanionProjectionJobService _jobs;
    readonly ILogger<CompanionProjectionWorker> _logger;
    readonly int _maxAttempts;

    public CompanionProjectionWorker(
        CompanionProjectionJobStore store,
        QueueClient queue,
        ICompanionProjectionProcessor processor,
        CompanionProjectionJobService jobs,
        ILogger<CompanionProjectionWorker> logger)
    {
        _store = store;
        _queue = queue;
        _processor = processor;
        _jobs = jobs;
        _logger = logger;
        _maxAttempts = int.TryParse(
            Environment.GetEnvironmentVariable("MAPPING_MAX_ATTEMPTS"), out var attempts)
            && attempts > 0
                ? attempts
                : 5;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _queue.CreateIfNotExistsAsync(cancellationToken: stoppingToken);
        _logger.LogInformation(
            "[MAPPING_WORKER] Phase=start Queue={Queue} Prefix={Prefix}",
            _queue.Name, _store.Prefix);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _queue.ReceiveMessagesAsync(
                    maxMessages: 1,
                    visibilityTimeout: MessageVisibility,
                    cancellationToken: stoppingToken);
                var message = response.Value.FirstOrDefault();
                if (message == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                await ProcessMessageAsync(message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(
                    "[MAPPING_WORKER] Phase=poll Error={Status}:{ErrorCode}",
                    ex.Status, ex.ErrorCode);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MAPPING_WORKER] Phase=poll Error=unexpected");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    async Task ProcessMessageAsync(QueueMessage message, CancellationToken stoppingToken)
    {
        CompanionProjectionQueueMessage? payload;
        try
        {
            payload = message.Body.ToObjectFromJson<CompanionProjectionQueueMessage>(
                CompanionProjectionJobStore.JsonOptions);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            _logger.LogError(
                "[MAPPING_WORKER] Phase=receive Error=invalid_message MessageId={MessageId}",
                message.MessageId);
            await _queue.DeleteMessageAsync(
                message.MessageId, message.PopReceipt, stoppingToken);
            return;
        }

        if (payload == null
            || payload.Version != 1
            || !CompanionProjectionJobService.IsValidJobId(payload.JobId))
        {
            _logger.LogError(
                "[MAPPING_WORKER] Phase=receive Error=invalid_payload MessageId={MessageId}",
                message.MessageId);
            await _queue.DeleteMessageAsync(
                message.MessageId, message.PopReceipt, stoppingToken);
            return;
        }

        var jobId = payload.JobId;
        var statusBlob = _store.GetStatusBlob(jobId);
        var leaseClient = statusBlob.GetBlobLeaseClient();
        string leaseId;
        try
        {
            var lease = await leaseClient.AcquireAsync(StatusLeaseDuration, cancellationToken: stoppingToken);
            leaseId = lease.Value.LeaseId;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
        {
            // Missing jobs and concurrently-active jobs are both left for their
            // visibility timeout. A valid creator may still be finishing writes.
            _logger.LogWarning(
                "[MAPPING_WORKER] Phase=lock JobId={JobId} Status={Status} Error={ErrorCode}",
                jobId, ex.Status, ex.ErrorCode);
            if (ex.Status == 404 && message.DequeueCount >= _maxAttempts)
            {
                var terminal = await TryMarkMissingStatusTerminalAsync(
                    jobId, message.DequeueCount, stoppingToken);
                if (terminal)
                {
                    await _queue.DeleteMessageAsync(
                        message.MessageId, message.PopReceipt, stoppingToken);
                }
            }
            return;
        }

        await using var heartbeat = new WorkerHeartbeat(
            _queue, message, leaseClient, _logger, stoppingToken);
        heartbeat.Start();
        var processingToken = heartbeat.ProcessingToken;

        try
        {
            var request = await _store.TryReadRequestAsync(jobId, processingToken);
            var status = await _store.TryReadStatusAsync(jobId, processingToken);
            if (request == null || status == null)
                throw new CompanionProjectionTerminalException("Job request or status metadata is missing.");

            if (status.State is CompanionProjectionQueueJobState.Completed
                or CompanionProjectionQueueJobState.Cancelled
                || status.State == CompanionProjectionQueueJobState.Failed && status.Terminal)
            {
                await TryDeleteTerminalMessageAsync(
                    heartbeat, jobId, processingToken);
                return;
            }

            var startedAt = status.StartedAt ?? DateTimeOffset.UtcNow;
            status = status with
            {
                State = CompanionProjectionQueueJobState.Running,
                StartedAt = startedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Attempt = checked((int)Math.Min(message.DequeueCount, int.MaxValue)),
                Terminal = false,
                Errors = [],
                Progress = status.Progress with
                {
                    Checkpoint = status.Progress.Checkpoint ?? "starting",
                    Message = "Mapping worker is processing the NodeSet.",
                },
            };
            await _store.WriteStatusAsync(
                status, leaseId, cancellationToken: processingToken);

            var statusGate = new SemaphoreSlim(1, 1);
            var artifacts = status.Artifacts.ToList();

            async Task ReportProgressAsync(
                CompanionProjectionProgress progress,
                CancellationToken cancellationToken)
            {
                await statusGate.WaitAsync(cancellationToken);
                try
                {
                    status = status with
                    {
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Progress = progress,
                        Artifacts = artifacts.ToArray(),
                    };
                    await _store.WriteStatusAsync(
                        status, leaseId, cancellationToken: cancellationToken);
                }
                finally
                {
                    statusGate.Release();
                }
            }

            var artifactSink = new WorkerArtifactSink(
                _store,
                jobId,
                async (artifact, cancellationToken) =>
                {
                    await statusGate.WaitAsync(cancellationToken);
                    try
                    {
                        artifacts.RemoveAll(a =>
                            string.Equals(a.FileName, artifact.FileName, StringComparison.Ordinal));
                        artifacts.Add(artifact);
                        artifacts.Sort((a, b) =>
                            string.CompareOrdinal(a.FileName, b.FileName));
                        status = status with
                        {
                            UpdatedAt = DateTimeOffset.UtcNow,
                            Artifacts = artifacts.ToArray(),
                        };
                        await _store.WriteStatusAsync(
                            status, leaseId, cancellationToken: cancellationToken);
                    }
                    finally
                    {
                        statusGate.Release();
                    }
                });

            // Stage deferred (blob:/URL) sources now; inline jobs and retries
            // short-circuit when input.xml already exists.
            await _jobs.EnsureInputStagedAsync(request, processingToken);

            await using var input = await _store.GetInputBlob(jobId)
                .OpenReadAsync(cancellationToken: processingToken);
            var result = await _processor.ProcessAsync(
                new CompanionProjectionProcessContext
                {
                    JobId = jobId,
                    Input = input,
                    Options = request.Options,
                    Checkpoint = status.Progress.Checkpoint,
                    Artifacts = artifactSink,
                    ReportProgressAsync = ReportProgressAsync,
                },
                processingToken);

            await statusGate.WaitAsync(processingToken);
            try
            {
                status = status with
                {
                    State = CompanionProjectionQueueJobState.Completed,
                    Terminal = true,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Errors = [],
                    Artifacts = artifacts.ToArray(),
                    Progress = new CompanionProjectionProgress
                    {
                        Checkpoint = result.Checkpoint ?? "completed",
                        ProcessedNodes = result.ProcessedNodes,
                        TotalNodes = result.TotalNodes,
                        CandidateCount = result.CandidateCount,
                        ProjectionCount = result.ProjectionCount,
                        WarningCount = result.WarningCount,
                        Message = result.Message ?? "Companion projection completed.",
                    },
                };
                await _store.WriteStatusAsync(
                    status, leaseId, cancellationToken: processingToken);
            }
            finally
            {
                statusGate.Release();
            }

            await TryDeleteTerminalMessageAsync(
                heartbeat, jobId, processingToken);
            _logger.LogInformation(
                "[MAPPING_WORKER] Phase=complete JobId={JobId} Projections={ProjectionCount} Artifacts={ArtifactCount}",
                jobId, result.ProjectionCount, artifacts.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Leave the message and running checkpoint for an idempotent restart.
        }
        catch (OperationCanceledException) when (heartbeat.LeaseLost)
        {
            // Lease ownership is no longer guaranteed. Stop immediately and
            // leave the message/checkpoint for a different replica to retry.
            _logger.LogError(
                "[MAPPING_WORKER] Phase=process JobId={JobId} Error=lease_lost",
                jobId);
        }
        catch (Exception ex)
        {
            var terminal = IsTerminal(ex) || message.DequeueCount >= _maxAttempts;
            var error = SafeError(ex);

            try
            {
                var prior = await _store.TryReadStatusAsync(jobId, CancellationToken.None);
                if (prior != null)
                {
                    var failed = prior with
                    {
                        State = terminal
                            ? CompanionProjectionQueueJobState.Failed
                            : CompanionProjectionQueueJobState.Queued,
                        Terminal = terminal,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        CompletedAt = terminal ? DateTimeOffset.UtcNow : null,
                        Attempt = checked((int)Math.Min(message.DequeueCount, int.MaxValue)),
                        Errors = [error],
                        Progress = prior.Progress with
                        {
                            Checkpoint = prior.Progress.Checkpoint ?? "failed",
                            Message = terminal
                                ? "Companion projection failed."
                                : "Processing failed and will be retried.",
                        },
                    };
                    await _store.WriteStatusAsync(
                        failed, leaseId, cancellationToken: CancellationToken.None);
                }
            }

            catch (Exception statusEx)
            {
                _logger.LogError(
                    statusEx,
                    "[MAPPING_WORKER] Phase=status JobId={JobId} Error=write_failed",
                    jobId);
                terminal = false;
            }

            if (terminal)
                await heartbeat.DeleteMessageAsync(CancellationToken.None);
            else
                await heartbeat.MakeVisibleAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            _logger.LogError(
                ex,
                "[MAPPING_WORKER] Phase=process JobId={JobId} Attempt={Attempt} Terminal={Terminal}",
                jobId, message.DequeueCount, terminal);
        }
        finally
        {
            await heartbeat.StopAsync();
            try
            {
                await leaseClient.ReleaseAsync(cancellationToken: CancellationToken.None);
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 409 or 412)
            {
                // Lease expired or was already released.
            }
        }
    }

    async Task TryDeleteTerminalMessageAsync(
        WorkerHeartbeat heartbeat,
        string jobId,
        CancellationToken cancellationToken)
    {
        try
        {
            await heartbeat.DeleteMessageAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is RequestFailedException
            or OperationCanceledException)
        {
            // Status is already terminal. Preserve it and let queue redelivery
            // retry acknowledgement idempotently rather than downgrading a
            // successfully completed job.
            _logger.LogWarning(
                ex,
                "[MAPPING_WORKER] Phase=ack JobId={JobId} Error=delete_failed",
                jobId);
        }
    }

    static string SafeError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (message.Length == 0)
            return exception.GetType().Name;
        return message.Length <= 2048 ? message : message[..2048];
    }

    async Task<bool> TryMarkMissingStatusTerminalAsync(
        string jobId,
        long dequeueCount,
        CancellationToken cancellationToken)
    {
        var request = await _store.TryReadRequestAsync(jobId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var status = new CompanionProjectionDurableJobStatus
        {
            JobId = jobId,
            State = CompanionProjectionQueueJobState.Failed,
            CreatedAt = request?.CreatedAt ?? now,
            UpdatedAt = now,
            CompletedAt = now,
            Attempt = checked((int)Math.Min(dequeueCount, int.MaxValue)),
            Terminal = true,
            Errors =
            [
                $"status.json was not found after {dequeueCount} dequeue attempts.",
            ],
            Progress = new CompanionProjectionProgress
            {
                Checkpoint = "status_missing",
                Message = "Queue message dropped after the durable status record remained missing.",
            },
        };

        try
        {
            await _store.WriteStatusAsync(
                status,
                onlyIfAbsent: true,
                cancellationToken: cancellationToken);
            _logger.LogError(
                "[MAPPING_WORKER] Phase=poison JobId={JobId} Attempts={Attempts} Terminal=true Error=status_missing",
                jobId, dequeueCount);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // A creator may have repaired status.json between lease failure and
            // poison handling. Only drop if that record is already terminal.
            var existing = await _store.TryReadStatusAsync(jobId, cancellationToken);
            return existing is
            {
                Terminal: true,
                State: CompanionProjectionQueueJobState.Completed
                    or CompanionProjectionQueueJobState.Failed
                    or CompanionProjectionQueueJobState.Cancelled,
            };
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(
                "[MAPPING_WORKER] Phase=poison JobId={JobId} Attempts={Attempts} Error={Status}:{ErrorCode}",
                jobId, dequeueCount, ex.Status, ex.ErrorCode);
            return false;
        }
    }

    static bool IsTerminal(Exception exception) =>
        exception is CompanionProjectionTerminalException
            or System.Xml.XmlException
            or InvalidDataException
            or System.Text.Json.JsonException
        || exception is RequestFailedException { Status: 404 };

    sealed class WorkerArtifactSink : ICompanionProjectionArtifactSink
    {
        readonly CompanionProjectionJobStore _store;
        readonly string _jobId;
        readonly Func<CompanionProjectionArtifact, CancellationToken, Task> _onWritten;

        public WorkerArtifactSink(
            CompanionProjectionJobStore store,
            string jobId,
            Func<CompanionProjectionArtifact, CancellationToken, Task> onWritten)
        {
            _store = store;
            _jobId = jobId;
            _onWritten = onWritten;
        }

        public async Task<CompanionProjectionArtifact> WriteAsync(
            string fileName,
            Stream content,
            string? contentType = null,
            CancellationToken cancellationToken = default)
        {
            if (!CompanionProjectionJobStore.IsArtifactFileName(fileName))
                throw new CompanionProjectionTerminalException(
                    $"Unsupported artifact file name '{fileName}'.");

            var blob = _store.GetArtifactBlob(_jobId, fileName);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var hashing = new HashingStream(content, hash, leaveInnerOpen: true);
            await blob.UploadAsync(hashing, new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = contentType
                        ?? CompanionProjectionJobStore.ContentTypeForArtifact(fileName),
                },
                TransferOptions = new Azure.Storage.StorageTransferOptions
                {
                    InitialTransferSize = 4 * 1024 * 1024,
                    MaximumTransferSize = 4 * 1024 * 1024,
                    MaximumConcurrency = 1,
                },
            }, cancellationToken);

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            await blob.SetMetadataAsync(
                new Dictionary<string, string> { ["sha256"] = sha256 },
                cancellationToken: cancellationToken);
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
            var artifact = new CompanionProjectionArtifact
            {
                FileName = fileName,
                BlobRef = $"blob:{blob.Name}",
                ContentType = properties.Value.ContentType
                    ?? CompanionProjectionJobStore.ContentTypeForArtifact(fileName),
                SizeBytes = properties.Value.ContentLength,
                Sha256 = sha256,
                ETag = properties.Value.ETag.ToString(),
                DownloadUrl = CompanionProjectionJobStore.DownloadPath(_jobId, fileName),
            };
            await _onWritten(artifact, cancellationToken);
            return artifact;
        }
    }

    sealed class WorkerHeartbeat : IAsyncDisposable
    {
        readonly QueueClient _queue;
        readonly string _messageId;
        readonly BinaryData _body;
        readonly BlobLeaseClient _lease;
        readonly ILogger _logger;
        readonly CancellationToken _stoppingToken;
        readonly SemaphoreSlim _receiptGate = new(1, 1);
        readonly CancellationTokenSource _heartbeatCts = new();
        readonly CancellationTokenSource _processingCts;
        string _popReceipt;
        Task? _loop;
        int _leaseLost;

        public WorkerHeartbeat(
            QueueClient queue,
            QueueMessage message,
            BlobLeaseClient lease,
            ILogger logger,
            CancellationToken stoppingToken)
        {
            _queue = queue;
            _messageId = message.MessageId;
            _popReceipt = message.PopReceipt;
            _body = message.Body;
            _lease = lease;
            _logger = logger;
            _stoppingToken = stoppingToken;
            _processingCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        }

        public CancellationToken ProcessingToken => _processingCts.Token;
        public bool LeaseLost => Volatile.Read(ref _leaseLost) != 0;

        public void Start() => _loop = RunAsync();

        public async Task DeleteMessageAsync(CancellationToken cancellationToken)
        {
            await _receiptGate.WaitAsync(cancellationToken);
            try
            {
                await _queue.DeleteMessageAsync(
                    _messageId, _popReceipt, cancellationToken);
            }
            finally
            {
                _receiptGate.Release();
            }
        }

        public async Task MakeVisibleAsync(
            TimeSpan visibility,
            CancellationToken cancellationToken)
        {
            await _receiptGate.WaitAsync(cancellationToken);
            try
            {
                var update = await _queue.UpdateMessageAsync(
                    _messageId, _popReceipt, _body, visibility, cancellationToken);
                _popReceipt = update.Value.PopReceipt;
            }
            finally
            {
                _receiptGate.Release();
            }
        }

        public async Task StopAsync()
        {
            _heartbeatCts.Cancel();
            if (_loop == null)
                return;
            try { await _loop; }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _processingCts.Dispose();
            _heartbeatCts.Dispose();
            _receiptGate.Dispose();
        }

        async Task RunAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _heartbeatCts.Token, _stoppingToken);
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), linked.Token);
                try
                {
                    await _lease.RenewAsync(cancellationToken: linked.Token);
                    await MakeVisibleAsync(MessageVisibility, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _leaseLost, 1);
                    _processingCts.Cancel();
                    _logger.LogError(
                        ex,
                        "[MAPPING_WORKER] Phase=heartbeat MessageId={MessageId} Error=lease_lost",
                        _messageId);
                    break;
                }
            }
        }
    }
}
