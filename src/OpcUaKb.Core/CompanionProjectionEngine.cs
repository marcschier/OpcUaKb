using System.Security.Cryptography;
using System.Text;

public sealed record CompanionProjectionEngineResult
{
    public required CompanionMappingDocument Mapping { get; init; }
    public required CompanionProjectionReport Report { get; init; }
    public required AddressSpaceFilterResult Filter { get; init; }
}

public sealed class CompanionProjectionEngine
{
    readonly AddressSpaceCoreFilter _filter;
    readonly CompanionCandidateService _candidates;
    readonly CompanionSemanticReasoner _reasoner;
    readonly ICompanionModelRepository _models;

    public CompanionProjectionEngine(
        AddressSpaceCoreFilter filter,
        CompanionCandidateService candidates,
        CompanionSemanticReasoner reasoner,
        ICompanionModelRepository models)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public async Task<CompanionProjectionEngineResult> ProjectAsync(
        AddressSpaceGraph source,
        CompanionProjectionJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxProjectionsPerSource, 1);

        var filtered = await _filter.FilterAsync(
            source,
            new AddressSpaceFilterOptions
            {
                IncludePaths = request.IncludePaths,
                ExcludePaths = request.ExcludePaths,
            },
            cancellationToken);
        var entities = _candidates.Segment(source, filtered.IncludedNodes);
        var candidateSets = await _candidates.FindCandidatesAsync(
            entities, request.CandidateLimit, cancellationToken);
        candidateSets = candidateSets.Select(CollapseInheritanceCandidates).ToArray();
        var decisions = await _reasoner.DecideAsync(
            candidateSets, request.MaxProjectionsPerSource, cancellationToken);
        var decisionByEntity = decisions.ToDictionary(d => d.EntityId, StringComparer.Ordinal);
        var candidateByEntity = candidateSets.ToDictionary(
            e => e.Entity.EntityId, StringComparer.Ordinal);

        var projections = new List<CompanionProjectionDefinition>();
        var ordinal = 0;
        foreach (var entity in entities.OrderBy(e => e.Root.BrowsePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityDecision = decisionByEntity[entity.EntityId];
            var set = candidateByEntity[entity.EntityId];
            if (entityDecision.IsUnresolved)
            {
                projections.Add(CreateUnresolvedProjection(
                    entity, entityDecision.Rationale, ordinal++));
                continue;
            }

            foreach (var decision in entityDecision.SelectedCandidates
                         .Take(request.MaxProjectionsPerSource))
            {
                projections.Add(await BuildProjectionAsync(
                    request, entity, set, decision, ordinal++, cancellationToken));
            }
        }

        foreach (var passThroughRoot in SelectPassThroughRoots(filtered.PassThroughNodes))
        {
            projections.Add(CreatePassThroughProjection(passThroughRoot, ordinal++));
        }

        var requiredModels = await ResolveRequiredModelsAsync(
            projections
                .Where(p => p.TargetType?.ModelUri is not null)
                .Select(p => new AddressSpaceRequiredModel
                {
                    ModelUri = p.TargetType!.ModelUri!,
                    Version = p.TargetType.ModelVersion,
                    PublicationDate = p.TargetType.ModelPublicationDate,
                }),
            cancellationToken);
        var metadata = new CompanionProjectionModelMetadata
        {
            OutputModelUri = request.OutputModelUri,
            OutputVersion = request.OutputVersion,
            SourceName = request.SourceName,
            SourceModelUris = source.Models.Select(m => m.ModelUri).Distinct(StringComparer.Ordinal).ToArray(),
            RequiredModels = requiredModels,
            RequiredModelUris = requiredModels.Select(model => model.ModelUri).ToArray(),
        };
        var mapping = new CompanionMappingDocument { Model = metadata, Projections = projections };
        var report = new CompanionProjectionReport
        {
            Model = metadata,
            SourceNodeCount = source.Nodes.Count,
            IncludedNodeCount = filtered.IncludedNodes.Count + filtered.PassThroughNodes.Count,
            AcceptedProjectionCount = projections.Count(p => p.Status == CompanionDecisionStatus.Accepted),
            ProposedProjectionCount = projections.Count(p => p.Status == CompanionDecisionStatus.Proposed),
            UnresolvedProjectionCount = projections.Count(p => p.Status == CompanionDecisionStatus.Unresolved),
            Projections = projections,
        };
        return new CompanionProjectionEngineResult
        {
            Mapping = mapping,
            Report = report,
            Filter = filtered,
        };
    }

    async Task<CompanionProjectionDefinition> BuildProjectionAsync(
        CompanionProjectionJobRequest request,
        CompanionEntitySignature entity,
        CompanionEntityCandidates candidateSet,
        CompanionCandidateDecision decision,
        int ordinal,
        CancellationToken cancellationToken)
    {
        var candidate = candidateSet.Candidates.Single(c => c.CandidateId == decision.CandidateId);
        var declarations = await _models.ExpandDeclarationsAsync(
            candidate.ModelUri, candidate.ExpandedNodeId, candidate.Version, cancellationToken);
        var mappings = DeriveMappings(request, entity, candidate, declarations, decision, ordinal);
        var blockingGaps = mappings
            .Where(m => m.Kind == CompanionMappingKind.UnboundRequired &&
                        m.Target is not null &&
                        declarations.Any(d => d.DeclarationPath == m.DeclarationPath &&
                                              (d.IsPlaceholder ||
                                               d.Node.NodeClass is "Method" or "Object")))
            .Select(m => $"Mandatory structural declaration '{m.DeclarationPath}' is unbound.")
            .ToArray();
        var status = blockingGaps.Length > 0 && decision.Status == CompanionDecisionStatus.Accepted
            ? CompanionDecisionStatus.Proposed
            : decision.Status;
        if (status != decision.Status)
        {
            mappings = mappings.Select(mapping => mapping.Kind == CompanionMappingKind.UnboundRequired
                ? mapping
                : mapping with { Status = status }).ToArray();
        }

        return new CompanionProjectionDefinition
        {
            Ordinal = ordinal,
            SourceRoot = Identifier(entity.Root),
            TargetType = new CompanionNodeIdentifier
            {
                ExpandedNodeId = candidate.ExpandedNodeId,
                BrowseName = candidate.BrowseName,
                ModelUri = candidate.ModelUri,
                ModelVersion = candidate.Version,
                ModelPublicationDate = candidate.PublicationDate,
            },
            Status = status,
            Confidence = decision.Confidence,
            Mappings = mappings,
            BlockingGaps = blockingGaps,
        };
    }

    static CompanionProjectionDefinition CreateUnresolvedProjection(
        CompanionEntitySignature entity,
        string? rationale,
        int ordinal) =>
        new()
        {
            Ordinal = ordinal,
            SourceRoot = Identifier(entity.Root),
            Status = CompanionDecisionStatus.Unresolved,
            Confidence = 0,
            BlockingGaps = [rationale ?? "No companion ObjectType was selected."],
        };

    static IReadOnlyList<CompanionMappingEntry> DeriveMappings(
        CompanionProjectionJobRequest request,
        CompanionEntitySignature entity,
        CompanionTypeCandidate candidate,
        IReadOnlyList<CompanionDeclaration> declarations,
        CompanionCandidateDecision decision,
        int ordinal)
    {
        var result = new List<CompanionMappingEntry>
        {
            new()
            {
                MappingId = StableMappingId(entity.Root.NodeId, candidate.ExpandedNodeId, "$"),
                Source = Identifier(entity.Root),
                Target = new CompanionNodeIdentifier
                {
                    ExpandedNodeId = CompanionTargetNodeId.Create(
                        request.OutputModelUri, entity.Root.NodeId, candidate.ExpandedNodeId, "$", ordinal),
                    BrowseName = candidate.BrowseName,
                    BrowsePath = "$",
                    ModelUri = request.OutputModelUri,
                },
                Kind = CompanionMappingKind.Object,
                Status = decision.Status,
                Confidence = decision.Confidence,
                Evidence = DecisionEvidence(decision),
                DeclarationPath = "$",
            },
        };

        var availableSources = entity.Nodes.Skip(1).ToList();
        var usedSources = new HashSet<string>(StringComparer.Ordinal);
        var omittedParentPaths = new List<string>();
        foreach (var declaration in declarations
                     .OrderBy(d => DeclarationDepth(d.DeclarationPath))
                     .ThenByDescending(d => d.IsMandatory)
                     .ThenBy(d => d.DeclarationPath, StringComparer.Ordinal))
        {
            if (omittedParentPaths.Any(path =>
                    declaration.DeclarationPath.StartsWith(
                        path + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            var match = availableSources
                .Where(source => !usedSources.Contains(source.NodeId) &&
                                 CompatibleNodeClasses(source.NodeClass, declaration.Node.NodeClass))
                .Select(source => (Source: source, Score: MemberScore(source, declaration)))
                .Where(pair => pair.Score >= 0.52)
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Source.BrowsePath, StringComparer.Ordinal)
                .FirstOrDefault();

            var targetId = CompanionTargetNodeId.Create(
                request.OutputModelUri,
                match.Source?.NodeId ?? entity.Root.NodeId,
                candidate.ExpandedNodeId,
                declaration.DeclarationPath,
                ordinal);
            var targetBrowseName = declaration.IsPlaceholder && match.Source is not null
                ? match.Source.BrowseName.Name
                : declaration.Node.BrowseName.Name;
            var target = new CompanionNodeIdentifier
            {
                ExpandedNodeId = targetId,
                BrowseName = targetBrowseName,
                BrowsePath = declaration.DeclarationPath,
                ModelUri = request.OutputModelUri,
            };

            if (match.Source is not null)
            {
                usedSources.Add(match.Source.NodeId);
                result.Add(new CompanionMappingEntry
                {
                    MappingId = StableMappingId(
                        match.Source.NodeId, candidate.ExpandedNodeId, declaration.DeclarationPath),
                    Source = Identifier(match.Source),
                    Target = target,
                    Kind = MappingKind(declaration.Node.NodeClass),
                    Direction = InferDirection(match.Source),
                    Status = decision.Status,
                    Confidence = Math.Clamp(0.65 * decision.Confidence + 0.35 * match.Score, 0, 1),
                    Evidence = new CompanionMappingEvidence
                    {
                        DeterministicScore = match.Score,
                        SemanticScore = decision.SemanticScore,
                        Reasons = [$"Matched declaration '{declaration.DeclarationPath}' by name, class and data type."],
                    },
                    IsMandatory = declaration.IsMandatory,
                    DeclarationPath = declaration.DeclarationPath,
                    TargetNodeClass = declaration.Node.NodeClass,
                    TargetTypeDefinition = declaration.Node.TypeDefinition,
                    TargetDataType = declaration.Node.DataType,
                    TargetReferenceType = declaration.ReferenceType,
                    TargetBrowseNameNamespaceUri = declaration.Node.BrowseName.NamespaceUri,
                });
            }
            else if (declaration.IsPlaceholder && !declaration.IsMandatory)
            {
                omittedParentPaths.Add(declaration.DeclarationPath);
            }
            else if (declaration.IsMandatory)
            {
                result.Add(new CompanionMappingEntry
                {
                    MappingId = StableMappingId(
                        entity.Root.NodeId, candidate.ExpandedNodeId, declaration.DeclarationPath),
                    Source = Identifier(entity.Root),
                    Target = target,
                    Kind = CompanionMappingKind.UnboundRequired,
                    Direction = declaration.Node.NodeClass == "Method"
                        ? CompanionMappingDirection.MethodForward
                        : CompanionMappingDirection.Read,
                    Status = CompanionDecisionStatus.Unresolved,
                    Confidence = 0,
                    Evidence = new CompanionMappingEvidence
                    {
                        DeterministicScore = 0,
                        Reasons = [$"Mandatory declaration '{declaration.DeclarationPath}' has no source match."],
                    },
                    IsMandatory = true,
                    DeclarationPath = declaration.DeclarationPath,
                    TargetNodeClass = declaration.Node.NodeClass,
                    TargetTypeDefinition = declaration.Node.TypeDefinition,
                    TargetDataType = declaration.Node.DataType,
                    TargetReferenceType = declaration.ReferenceType,
                    TargetBrowseNameNamespaceUri = declaration.Node.BrowseName.NamespaceUri,
                });
                if (declaration.IsPlaceholder)
                    omittedParentPaths.Add(declaration.DeclarationPath);
            }
            else
            {
                // The declaration is optional and has no source match. Its
                // nested declarations cannot be instantiated without their
                // parent, so suppress that entire subtree.
                omittedParentPaths.Add(declaration.DeclarationPath);
            }
        }
        return result.OrderBy(m => m.DeclarationPath ?? "", StringComparer.Ordinal).ToArray();
    }

    static int DeclarationDepth(string path) => path.Count(character => character == '/');

    static CompanionEntityCandidates CollapseInheritanceCandidates(CompanionEntityCandidates set)
    {
        var candidateIds = set.Candidates.Select(c => c.CandidateId).ToHashSet(StringComparer.Ordinal);
        var superseded = set.Candidates
            .SelectMany(candidate => candidate.SupertypeIds.Where(candidateIds.Contains))
            .ToHashSet(StringComparer.Ordinal);
        if (set.Candidates.Count > superseded.Count && superseded.Count > 0)
            return set with { Candidates = set.Candidates.Where(c => !superseded.Contains(c.CandidateId)).ToArray() };
        return set;
    }

    static IReadOnlyList<AddressSpaceNode> SelectPassThroughRoots(
        IReadOnlyList<AddressSpaceNode> passThrough)
    {
        var ids = passThrough.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
        return passThrough
            .Where(n => n.NodeClass == "Object" &&
                        (n.ParentNodeId is null || !ids.Contains(n.ParentNodeId)))
            .OrderBy(n => n.BrowsePath, StringComparer.Ordinal)
            .ToArray();
    }

    static CompanionProjectionDefinition CreatePassThroughProjection(AddressSpaceNode node, int ordinal)
    {
        var targetType = node.TypeDefinition is null
            ? null
            : new CompanionNodeIdentifier
            {
                ExpandedNodeId = node.TypeDefinition,
                BrowseName = node.TypeDefinition,
                ModelUri = ExpandedNodeIdNamespace(node.TypeDefinition),
            };
        return new CompanionProjectionDefinition
        {
            Ordinal = ordinal,
            SourceRoot = Identifier(node),
            TargetType = targetType,
            Status = CompanionDecisionStatus.Accepted,
            Confidence = 1,
            Mappings =
            [
                new CompanionMappingEntry
                {
                    MappingId = StableMappingId(node.NodeId, node.TypeDefinition ?? node.NodeId, "$"),
                    Source = Identifier(node),
                    Target = Identifier(node),
                    Kind = CompanionMappingKind.PassThrough,
                    Status = CompanionDecisionStatus.Accepted,
                    Confidence = 1,
                    Evidence = new CompanionMappingEvidence
                    {
                        DeterministicScore = 1,
                        Reasons = ["Source node already has an official companion TypeDefinition."],
                    },
                },
            ],
        };
    }

    async Task<IReadOnlyList<AddressSpaceRequiredModel>> ResolveRequiredModelsAsync(
        IEnumerable<AddressSpaceRequiredModel> rootModels,
        CancellationToken cancellationToken)
    {
        var catalog = await _models.GetCatalogAsync(cancellationToken);
        var result = new Dictionary<string, AddressSpaceRequiredModel>(StringComparer.Ordinal);
        var constraints = new Dictionary<string, AddressSpaceRequiredModel>(StringComparer.Ordinal);
        var expanded = new HashSet<(string ModelUri, string? Version)>();

        async Task VisitAsync(AddressSpaceRequiredModel requested)
        {
            if (constraints.TryGetValue(requested.ModelUri, out var existingConstraint))
            {
                var mergedVersion = MergeExactString(
                    requested.ModelUri, "version", existingConstraint.Version, requested.Version);
                var mergedPublicationDate = MergeExactValue(
                    requested.ModelUri, "publication date",
                    existingConstraint.PublicationDate, requested.PublicationDate);
                if (mergedVersion == existingConstraint.Version &&
                    mergedPublicationDate == existingConstraint.PublicationDate)
                {
                    return;
                }

                // Upgrade an earlier unversioned fallback with a later exact
                // transitive constraint, then resolve that exact model's own
                // dependency closure.
                requested = existingConstraint with
                {
                    Version = mergedVersion,
                    PublicationDate = mergedPublicationDate,
                };
            }
            constraints[requested.ModelUri] = requested;

            var matches = catalog.Where(entry =>
                string.Equals(entry.ModelUri, requested.ModelUri, StringComparison.Ordinal)
                || string.Equals(entry.NamespaceUri, requested.ModelUri, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(requested.Version))
                matches = matches.Where(entry =>
                    string.Equals(entry.Version, requested.Version, StringComparison.Ordinal));
            if (requested.PublicationDate is { } requestedDate)
                matches = matches.Where(entry =>
                    entry.PublicationDate?.UtcDateTime == requestedDate.UtcDateTime);
            var entry = matches
                .OrderByDescending(candidate => candidate.IsLatest)
                .ThenByDescending(candidate => candidate.PublicationDate)
                .FirstOrDefault();
            var exact = new AddressSpaceRequiredModel
            {
                ModelUri = requested.ModelUri,
                Version = requested.Version ?? entry?.Version,
                PublicationDate = requested.PublicationDate ?? entry?.PublicationDate,
            };
            result[exact.ModelUri] = exact;
            if (entry is null || !expanded.Add((exact.ModelUri, exact.Version)))
                return;
            foreach (var required in entry.RequiredModels)
                await VisitAsync(required);
        }

        // Resolve selected companion models first. Their exact dependency
        // constraints (including the UA model version) must win over the
        // unversioned Core fallback.
        foreach (var root in rootModels
                     .GroupBy(model => (model.ModelUri, model.Version))
                     .Select(group => group.First())
                     .OrderBy(model => model.ModelUri, StringComparer.Ordinal)
                     .ThenBy(model => model.Version, StringComparer.Ordinal))
        {
            await VisitAsync(root);
        }
        await VisitAsync(new AddressSpaceRequiredModel
        {
            ModelUri = AddressSpaceNodeSetReader.UaNamespace,
        });

        return result.Values
            .OrderBy(model => model.ModelUri, StringComparer.Ordinal)
            .ToArray();

        static T? MergeExactValue<T>(
            string modelUri,
            string field,
            T? existing,
            T? requested)
            where T : struct
        {
            if (existing is null) return requested;
            if (requested is null || EqualityComparer<T>.Default.Equals(existing.Value, requested.Value))
                return existing;
            throw new InvalidOperationException(
                $"Required model '{modelUri}' has conflicting {field} constraints " +
                $"'{existing}' and '{requested}'.");
        }

        static string? MergeExactString(
            string modelUri,
            string field,
            string? existing,
            string? requested)
        {
            if (existing is null) return requested;
            if (requested is null || string.Equals(existing, requested, StringComparison.Ordinal))
                return existing;
            throw new InvalidOperationException(
                $"Required model '{modelUri}' has conflicting {field} constraints " +
                $"'{existing}' and '{requested}'.");
        }
    }

    static double MemberScore(AddressSpaceNode source, CompanionDeclaration target)
    {
        var sourceName = CompanionDeterministicCandidateScorer.NormalizeName(source.BrowseName.Name);
        var targetName = CompanionDeterministicCandidateScorer.NormalizeName(target.Node.BrowseName.Name);
        var name = sourceName == targetName
            ? 1
            : TokenOverlap(source.BrowseName.Name, target.Node.BrowseName.Name);
        var dataType = source.DataType is null || target.Node.DataType is null
            ? 0.5
            : CompanionDeterministicCandidateScorer.NormalizeNodeIdentity(source.DataType) ==
              CompanionDeterministicCandidateScorer.NormalizeNodeIdentity(target.Node.DataType) ? 1 : 0;
        return 0.78 * name + 0.22 * dataType;
    }

    static double TokenOverlap(string left, string right)
    {
        var l = CompanionCandidateService.Tokenize(left);
        var r = CompanionCandidateService.Tokenize(right);
        if (l.Count == 0 || r.Count == 0) return 0;
        return (double)l.Count(r.Contains) / Math.Max(l.Count, r.Count);
    }

    static bool CompatibleNodeClasses(string source, string target) =>
        source == target || source == "Variable" && target == "Variable" ||
        source == "Object" && target == "Object" ||
        source == "Method" && target == "Method";

    static CompanionMappingKind MappingKind(string nodeClass) => nodeClass switch
    {
        "Variable" => CompanionMappingKind.Variable,
        "Method" => CompanionMappingKind.Method,
        "Object" => CompanionMappingKind.Object,
        _ when nodeClass.Contains("Event", StringComparison.OrdinalIgnoreCase) => CompanionMappingKind.Event,
        _ => CompanionMappingKind.Object,
    };

    static CompanionMappingDirection? InferDirection(AddressSpaceNode node)
    {
        if (node.NodeClass == "Method") return CompanionMappingDirection.MethodForward;
        if (node.NodeClass != "Variable") return null;
        var access = node.UserAccessLevel ?? node.AccessLevel ?? 1;
        return (access & 0x02) != 0
            ? CompanionMappingDirection.ReadWrite
            : CompanionMappingDirection.Read;
    }

    static CompanionMappingEvidence DecisionEvidence(CompanionCandidateDecision decision) =>
        new()
        {
            DeterministicScore = decision.DeterministicScore,
            SemanticScore = decision.SemanticScore,
            Reasons = decision.Rationale is null ? [] : [decision.Rationale],
        };

    static CompanionNodeIdentifier Identifier(AddressSpaceNode node) =>
        new()
        {
            ExpandedNodeId = node.NodeId,
            BrowseName = node.BrowseName.Name,
            BrowsePath = node.BrowsePath,
            ModelUri = node.BrowseName.NamespaceUri,
        };

    static string StableMappingId(string source, string target, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}\n{target}\n{path}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    static string? ExpandedNodeIdNamespace(string expandedNodeId)
    {
        var start = expandedNodeId.IndexOf("nsu=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 4;
        var end = expandedNodeId.IndexOf(';', start);
        return end < 0 ? null : Uri.UnescapeDataString(expandedNodeId[start..end]);
    }
}
