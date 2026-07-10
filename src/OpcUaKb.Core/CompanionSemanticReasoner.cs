using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record CompanionReasonerOptions
{
    public double AcceptedThreshold { get; init; } = 0.82;
    public double ProposedThreshold { get; init; } = 0.55;
    public double DeterministicWeight { get; init; } = 0.65;
    public int BatchSize { get; init; } = 20;
    public int MaxProjectionsPerSource { get; init; } = 3;
}

public sealed record CompanionCandidateDecision
{
    public required string EntityId { get; init; }
    public required string CandidateId { get; init; }
    public required CompanionDecisionStatus Status { get; init; }
    public required double Confidence { get; init; }
    public required double DeterministicScore { get; init; }
    public double? SemanticScore { get; init; }
    public string? Rationale { get; init; }
}

public sealed record CompanionEntityDecision
{
    public required string EntityId { get; init; }
    public IReadOnlyList<CompanionCandidateDecision> SelectedCandidates { get; init; } = [];
    public string? Rationale { get; init; }
    public bool IsUnresolved => SelectedCandidates.Count == 0;
}

public interface ICompanionChatClient
{
    bool Available { get; }
    Task<JsonNode?> CompleteAsync(object body, CancellationToken cancellationToken = default);
}

public sealed class AoaiCompanionChatClient(AoaiChatClient client) : ICompanionChatClient
{
    public bool Available => client.Available;

    public Task<JsonNode?> CompleteAsync(object body, CancellationToken cancellationToken = default) =>
        client.ChatCompletionAsync(body, cancellationToken);
}

public sealed class CompanionSemanticReasoner
{
    readonly ICompanionChatClient _chat;
    readonly CompanionReasonerOptions _options;

    public CompanionSemanticReasoner(
        ICompanionChatClient chat,
        CompanionReasonerOptions? options = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _options = options ?? new CompanionReasonerOptions();
        ValidateOptions(_options);
    }

    public CompanionSemanticReasoner(
        AoaiChatClient chat,
        CompanionReasonerOptions? options = null)
        : this(new AoaiCompanionChatClient(chat), options)
    {
    }

    public async Task<IReadOnlyList<CompanionEntityDecision>> DecideAsync(
        IReadOnlyList<CompanionEntityCandidates> entities,
        CancellationToken cancellationToken = default)
        => await DecideAsync(entities, _options.MaxProjectionsPerSource, cancellationToken);

    public async Task<IReadOnlyList<CompanionEntityDecision>> DecideAsync(
        IReadOnlyList<CompanionEntityCandidates> entities,
        int maxProjectionsPerSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProjectionsPerSource, 1);
        if (!_chat.Available)
            return entities.Select(entity =>
                DeterministicDecision(entity, maxProjectionsPerSource)).ToArray();

        var decisions = new List<CompanionEntityDecision>(entities.Count);
        foreach (var batch in entities.Chunk(_options.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nonEmpty = batch.Where(entity => entity.Candidates.Count > 0).ToArray();
            decisions.AddRange(batch.Where(entity => entity.Candidates.Count == 0)
                .Select(entity => DeterministicDecision(entity, maxProjectionsPerSource)));
            if (nonEmpty.Length == 0) continue;

            var response = await _chat.CompleteAsync(
                CreateRequest(nonEmpty, maxProjectionsPerSource), cancellationToken);
            var semantic = ParseResponse(response, nonEmpty);
            decisions.AddRange(nonEmpty.Select(entity =>
                Combine(entity, semantic[entity.Entity.EntityId], maxProjectionsPerSource)));
        }
        return decisions.OrderBy(d => d.EntityId, StringComparer.Ordinal).ToArray();
    }

    object CreateRequest(
        IReadOnlyList<CompanionEntityCandidates> batch,
        int maxProjectionsPerSource)
    {
        var allowedCandidateIds = batch.SelectMany(e => e.Candidates)
            .Select(c => c.CandidateId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var payload = batch.Select(entity => new
        {
            entity_id = entity.Entity.EntityId,
            name = entity.Entity.Root.BrowseName.Name,
            parent_context = entity.Entity.ParentContextTokens.OrderBy(
                token => token, StringComparer.Ordinal),
            description = entity.Entity.Root.Description,
            members = entity.Entity.MemberNames.OrderBy(x => x, StringComparer.Ordinal),
            data_types = entity.Entity.DataTypes.OrderBy(x => x, StringComparer.Ordinal),
            methods = entity.Entity.MethodNames.OrderBy(x => x, StringComparer.Ordinal),
            value_keywords = entity.Entity.ValueTokens.OrderBy(x => x, StringComparer.Ordinal),
            candidates = entity.Candidates.Select(candidate => new
            {
                candidate_id = candidate.CandidateId,
                model_uri = candidate.ModelUri,
                name = candidate.BrowseName,
                description = candidate.Description,
                members = candidate.MemberNames,
                data_types = candidate.DataTypes,
                methods = candidate.MethodNames,
                deterministic_score = candidate.DeterministicScore,
            }),
        });

        return new
        {
            temperature = 0,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        $"Select up to {maxProjectionsPerSource} unrelated companion ObjectTypes per source entity. " +
                        "Multiple candidates from different specifications may all be selected when independently strong. " +
                        "Do not select a supertype when a selected more-specific subtype represents the same projection. " +
                        "Use only candidate_id values present in that entity's closed candidate list. " +
                        "Return an empty candidates array when none are semantically compatible. Scores are 0..1.",
                },
                new { role = "user", content = JsonSerializer.Serialize(payload) },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "companion_mapping_decisions",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "decisions" },
                        properties = new
                        {
                            decisions = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    required = new[] { "entity_id", "candidates" },
                                    properties = new
                                    {
                                        entity_id = new
                                        {
                                            type = "string",
                                            @enum = batch.Select(e => e.Entity.EntityId).ToArray(),
                                        },
                                        candidates = new
                                        {
                                            type = "array",
                                            maxItems = maxProjectionsPerSource,
                                            items = new
                                            {
                                                type = "object",
                                                additionalProperties = false,
                                                required = new[] { "candidate_id", "score", "rationale" },
                                                properties = new
                                                {
                                                    candidate_id = new
                                                    {
                                                        type = "string",
                                                        @enum = allowedCandidateIds,
                                                    },
                                                    score = new { type = "number", minimum = 0, maximum = 1 },
                                                    rationale = new { type = "string" },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    static Dictionary<string, IReadOnlyList<SemanticDecision>> ParseResponse(
        JsonNode? response,
        IReadOnlyList<CompanionEntityCandidates> batch)
    {
        var contentNode = response?["choices"]?[0]?["message"]?["content"]
            ?? throw new JsonException("AOAI response did not contain choices[0].message.content.");
        var parsed = JsonNode.Parse(StripCodeFence(ExtractContent(contentNode)))
            ?? throw new JsonException("AOAI structured response was empty.");
        var array = parsed["decisions"]?.AsArray()
            ?? throw new JsonException("AOAI structured response did not contain decisions.");
        var allowed = batch.ToDictionary(
            e => e.Entity.EntityId,
            e => e.Candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<SemanticDecision>>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var entityId = item?["entity_id"]?.GetValue<string>()
                ?? throw new JsonException("A semantic decision has no entity_id.");
            if (!allowed.TryGetValue(entityId, out var candidateIds))
                throw new JsonException($"AOAI returned unknown entity_id '{entityId}'.");
            if (result.ContainsKey(entityId))
                throw new JsonException($"AOAI returned duplicate decision for entity '{entityId}'.");
            var selected = item?["candidates"]?.AsArray()
                ?? throw new JsonException($"AOAI decision '{entityId}' has no candidates array.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var semantic = new List<SemanticDecision>(selected.Count);
            foreach (var candidateNode in selected)
            {
                var candidateId = candidateNode?["candidate_id"]?.GetValue<string>()
                    ?? throw new JsonException($"AOAI decision '{entityId}' has a candidate without candidate_id.");
                if (!candidateIds.Contains(candidateId))
                    throw new JsonException(
                        $"AOAI returned candidate_id '{candidateId}' outside entity '{entityId}' closed candidate set.");
                if (!seen.Add(candidateId))
                    throw new JsonException(
                        $"AOAI returned duplicate candidate_id '{candidateId}' for entity '{entityId}'.");
                var score = candidateNode?["score"]?.GetValue<double>()
                    ?? throw new JsonException($"AOAI candidate '{candidateId}' has no score.");
                if (score is < 0 or > 1)
                    throw new JsonException($"AOAI candidate '{candidateId}' score is outside 0..1.");
                semantic.Add(new SemanticDecision(
                    candidateId, score, candidateNode?["rationale"]?.GetValue<string>()));
            }
            result[entityId] = semantic;
        }

        foreach (var entity in batch)
            result.TryAdd(entity.Entity.EntityId, []);
        return result;
    }

    CompanionEntityDecision Combine(
        CompanionEntityCandidates entity,
        IReadOnlyList<SemanticDecision> semantic,
        int maxProjectionsPerSource)
    {
        var selections = semantic
            .Select(selection =>
            {
                var candidate = entity.Candidates.Single(
                    c => c.CandidateId == selection.CandidateId);
                var confidence =
                    _options.DeterministicWeight * candidate.DeterministicScore +
                    (1 - _options.DeterministicWeight) * selection.Score;
                return CreateSelectedDecision(
                    entity.Entity.EntityId,
                    candidate,
                    confidence,
                    selection.Score,
                    selection.Rationale);
            })
            .Where(decision => decision.Confidence >= _options.ProposedThreshold)
            .OrderByDescending(decision => decision.Status == CompanionDecisionStatus.Accepted)
            .ThenByDescending(decision => decision.Confidence)
            .ThenBy(decision => decision.CandidateId, StringComparer.Ordinal)
            .Take(maxProjectionsPerSource)
            .ToArray();
        return new CompanionEntityDecision
        {
            EntityId = entity.Entity.EntityId,
            SelectedCandidates = selections,
            Rationale = selections.Length == 0
                ? "The semantic reasoner selected no compatible closed candidates."
                : null,
        };
    }

    CompanionEntityDecision DeterministicDecision(
        CompanionEntityCandidates entity,
        int maxProjectionsPerSource)
    {
        var selections = entity.Candidates
            .Where(candidate => candidate.DeterministicScore >= _options.ProposedThreshold)
            .OrderByDescending(candidate => candidate.DeterministicScore)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .Take(maxProjectionsPerSource)
            .Select(candidate => CreateSelectedDecision(
                entity.Entity.EntityId,
                candidate,
                candidate.DeterministicScore,
                null,
                "Deterministic scoring only; AOAI is unavailable."))
            .ToArray();
        return new CompanionEntityDecision
        {
            EntityId = entity.Entity.EntityId,
            SelectedCandidates = selections,
            Rationale = selections.Length == 0
                ? entity.Candidates.Count == 0
                    ? "No official ObjectType candidates were found."
                    : "No candidate reached the proposed threshold."
                : null,
        };
    }

    CompanionCandidateDecision CreateSelectedDecision(
        string entityId,
        CompanionTypeCandidate candidate,
        double confidence,
        double? semanticScore,
        string? rationale) =>
        new()
        {
            EntityId = entityId,
            CandidateId = candidate.CandidateId,
            Status = confidence >= _options.AcceptedThreshold
                ? CompanionDecisionStatus.Accepted
                : CompanionDecisionStatus.Proposed,
            Confidence = Math.Clamp(confidence, 0, 1),
            DeterministicScore = candidate.DeterministicScore,
            SemanticScore = semanticScore,
            Rationale = rationale,
        };

    static string ExtractContent(JsonNode content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (content is JsonArray array)
        {
            return string.Concat(array.Select(part =>
                part?["text"]?.GetValue<string>() ??
                part?["content"]?.GetValue<string>() ??
                ""));
        }
        throw new JsonException("AOAI message content was not text.");
    }

    static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLine < 0 || lastFence <= firstLine)
            throw new JsonException("AOAI response contained an invalid code fence.");
        return trimmed[(firstLine + 1)..lastFence].Trim();
    }

    static void ValidateOptions(CompanionReasonerOptions options)
    {
        if (options.AcceptedThreshold is < 0 or > 1 ||
            options.ProposedThreshold is < 0 or > 1 ||
            options.ProposedThreshold > options.AcceptedThreshold)
            throw new ArgumentOutOfRangeException(nameof(options), "Thresholds must satisfy 0 <= proposed <= accepted <= 1.");
        if (options.DeterministicWeight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Deterministic weight must be within 0..1.");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxProjectionsPerSource, 1);
    }

    sealed record SemanticDecision(string CandidateId, double Score, string? Rationale);
}
