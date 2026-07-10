using System.Globalization;
using System.Text.RegularExpressions;
using Azure.Search.Documents.Models;

public sealed record CompanionEntitySignature
{
    public required string EntityId { get; init; }
    public required AddressSpaceNode Root { get; init; }
    public required IReadOnlyList<AddressSpaceNode> Nodes { get; init; }
    public required IReadOnlySet<string> NameTokens { get; init; }
    public IReadOnlySet<string> ParentContextTokens { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public required IReadOnlySet<string> DescriptionTokens { get; init; }
    public IReadOnlySet<string> ValueTokens { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);
    public required IReadOnlySet<string> MemberNames { get; init; }
    public required IReadOnlySet<string> DataTypes { get; init; }
    public required IReadOnlySet<string> MethodNames { get; init; }
    public int MaximumDepth { get; init; }
}

public sealed record CompanionTypeCandidate
{
    public required string CandidateId { get; init; }
    public required string ExpandedNodeId { get; init; }
    public required string ModelUri { get; init; }
    public string? Version { get; init; }
    public DateTimeOffset? PublicationDate { get; init; }
    public required string BrowseName { get; init; }
    public string? Description { get; init; }
    public string? SupertypeExpandedNodeId { get; init; }
    public IReadOnlyList<string> SupertypeIds { get; init; } = [];
    public IReadOnlyList<string> MemberNames { get; init; } = [];
    public IReadOnlyList<string> DataTypes { get; init; } = [];
    public IReadOnlyList<string> MethodNames { get; init; } = [];
    public double SearchScore { get; init; }
    public double DeterministicScore { get; init; }
    public IReadOnlyDictionary<string, double> Features { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}

public sealed record CompanionEntityCandidates
{
    public required CompanionEntitySignature Entity { get; init; }
    public required IReadOnlyList<CompanionTypeCandidate> Candidates { get; init; }
}

public sealed record CompanionSearchDocument(
    IReadOnlyDictionary<string, object?> Fields,
    double? Score);

public interface ICompanionTypeSearch
{
    Task<IReadOnlyList<CompanionSearchDocument>> SearchAsync(
        string query,
        string filter,
        IReadOnlyList<string> select,
        int top,
        CancellationToken cancellationToken = default);
}

public interface ICompanionCandidateScorer
{
    CompanionTypeCandidate Score(
        CompanionEntitySignature source,
        CompanionTypeCandidate candidate);
}

public sealed class SearchServiceCompanionTypeSearch(SearchService search) : ICompanionTypeSearch
{
    public async Task<IReadOnlyList<CompanionSearchDocument>> SearchAsync(
        string query,
        string filter,
        IReadOnlyList<string> select,
        int top,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = await search.SearchAsync(query, filter, select, top);
        return results.Select(result => new CompanionSearchDocument(
            result.Document.ToDictionary(
                pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
            result.Score)).ToArray();
    }
}

public sealed class CompanionDeterministicCandidateScorer : ICompanionCandidateScorer
{
    public CompanionTypeCandidate Score(
        CompanionEntitySignature source,
        CompanionTypeCandidate candidate)
    {
        var candidateName = CompanionCandidateService.Tokenize(candidate.BrowseName);
        var candidateDescription = CompanionCandidateService.Tokenize(candidate.Description);
        var candidateMembers = candidate.MemberNames
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var candidateDataTypes = candidate.DataTypes
            .Select(NormalizeNodeIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var candidateMethods = candidate.MethodNames
            .Select(NormalizeName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var name = Jaccard(source.NameTokens, candidateName);
        var description = Jaccard(source.DescriptionTokens, candidateDescription);
        var members = Jaccard(source.MemberNames, candidateMembers);
        var dataTypes = Jaccard(source.DataTypes, candidateDataTypes);
        var methods = Jaccard(source.MethodNames, candidateMethods);
        var hierarchy = candidate.SupertypeIds.Count > 0 || candidate.SupertypeExpandedNodeId is not null
            ? Math.Min(1, source.MaximumDepth / 4d)
            : source.MaximumDepth == 0 ? 1 : 0;
        var search = Math.Clamp(candidate.SearchScore / 10d, 0, 1);

        var features = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = description,
            ["members"] = members,
            ["data_types"] = dataTypes,
            ["methods"] = methods,
            ["hierarchy"] = hierarchy,
            ["search"] = search,
        };
        var score =
            0.28 * name +
            0.12 * description +
            0.30 * members +
            0.12 * dataTypes +
            0.10 * methods +
            0.04 * hierarchy +
            0.04 * search;
        return candidate with
        {
            DeterministicScore = Math.Clamp(score, 0, 1),
            Features = features,
        };
    }

    static double Jaccard(IReadOnlySet<string> left, IReadOnlySet<string> right)
    {
        if (left.Count == 0 && right.Count == 0) return 1;
        if (left.Count == 0 || right.Count == 0) return 0;
        var intersection = left.Count(right.Contains);
        return (double)intersection / (left.Count + right.Count - intersection);
    }

    internal static string NormalizeName(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    internal static string NormalizeNodeIdentity(string value)
    {
        var separator = value.LastIndexOf(';');
        return (separator >= 0 ? value[(separator + 1)..] : value).ToLowerInvariant();
    }
}

public sealed class CompanionCandidateService
{
    static readonly IReadOnlyList<string> SelectFields =
    [
        "node_id", "model_uri", "model_version", "spec_version", "publication_date",
        "browse_name", "description", "source_blob", "parent_node_id", "page_chunk",
    ];

    readonly ICompanionTypeSearch _search;
    readonly ICompanionModelRepository? _models;
    readonly ICompanionCandidateScorer _scorer;

    public static IReadOnlyList<string> CandidateSelectFields => SelectFields;

    public CompanionCandidateService(
        ICompanionTypeSearch search,
        ICompanionModelRepository models,
        ICompanionCandidateScorer? scorer = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _scorer = scorer ?? new CompanionDeterministicCandidateScorer();
    }

    public CompanionCandidateService(
        ICompanionTypeSearch search,
        ICompanionCandidateScorer? scorer = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _scorer = scorer ?? new CompanionDeterministicCandidateScorer();
    }

    public CompanionCandidateService(
        SearchService search,
        ICompanionModelRepository models,
        ICompanionCandidateScorer? scorer = null)
        : this(new SearchServiceCompanionTypeSearch(search), models, scorer)
    {
    }

    public CompanionCandidateService(
        SearchService search,
        ICompanionCandidateScorer? scorer = null)
        : this(new SearchServiceCompanionTypeSearch(search), scorer)
    {
    }

    public IReadOnlyList<CompanionEntitySignature> Segment(
        AddressSpaceGraph graph,
        IEnumerable<AddressSpaceNode> includedNodes)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(includedNodes);
        var included = includedNodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var subtreeCache = new Dictionary<string, SubtreeInfo>(StringComparer.Ordinal);
        var roots = included.Values
            .Where(n => n.NodeClass == "Object")
            .OrderBy(n => n.BrowsePath, StringComparer.Ordinal)
            .ToArray();

        return roots.Select(root =>
        {
            var subtree = BuildSubtree(
                root.NodeId,
                included,
                subtreeCache,
                new HashSet<string>(StringComparer.Ordinal));
            return CreateSignature(root, subtree, graph);
        }).ToArray();
    }

    public async Task<IReadOnlyList<CompanionEntityCandidates>> FindCandidatesAsync(
        IReadOnlyList<CompanionEntitySignature> entities,
        int top = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var result = new List<CompanionEntityCandidates>(entities.Count);
        var modelCache = new Dictionary<(string ModelUri, string? Version), CompanionModelGraph>();
        var candidateCache = new Dictionary<
            (string ModelUri, string? Version, string CandidateId),
            CompanionTypeCandidate>();
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = BuildQuery(entity);
            const string filter =
                "content_type eq 'nodeset' and source eq 'opcfoundation' and is_latest eq true " +
                "and node_class eq 'ObjectType' " +
                "and model_uri ne null and model_uri ne '' and node_id ne null and node_id ne ''";
            var documents = await _search.SearchAsync(
                query, filter, SelectFields, Math.Max(top * 3, top), cancellationToken);
            var parsedCandidates = documents
                .Select(ParseCandidate)
                .OfType<CompanionTypeCandidate>()
                .GroupBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(c => c.SearchScore).First())
                .ToArray();
            var enrichedCandidates = new List<CompanionTypeCandidate>(parsedCandidates.Length);
            foreach (var candidate in parsedCandidates)
            {
                enrichedCandidates.Add(await EnrichCandidateAsync(
                    candidate, modelCache, candidateCache, cancellationToken));
            }
            var candidates = enrichedCandidates
                .Select(candidate => _scorer.Score(entity, candidate))
                .OrderByDescending(candidate => candidate.DeterministicScore)
                .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
                .Take(top)
                .ToArray();
            result.Add(new CompanionEntityCandidates { Entity = entity, Candidates = candidates });
        }
        return result;
    }

    async Task<CompanionTypeCandidate> EnrichCandidateAsync(
        CompanionTypeCandidate candidate,
        Dictionary<(string ModelUri, string? Version), CompanionModelGraph> modelCache,
        Dictionary<(string ModelUri, string? Version, string CandidateId), CompanionTypeCandidate> candidateCache,
        CancellationToken cancellationToken)
    {
        if (_models is null) return candidate;
        var candidateKey = (candidate.ModelUri, candidate.Version, candidate.CandidateId);
        if (candidateCache.TryGetValue(candidateKey, out var cached))
            return candidate with
            {
                SupertypeExpandedNodeId = cached.SupertypeExpandedNodeId,
                SupertypeIds = cached.SupertypeIds,
                MemberNames = cached.MemberNames,
                DataTypes = cached.DataTypes,
                MethodNames = cached.MethodNames,
            };

        var modelKey = (candidate.ModelUri, candidate.Version);
        if (!modelCache.TryGetValue(modelKey, out var model))
        {
            model = await _models.LoadModelAsync(
                candidate.ModelUri, candidate.Version, cancellationToken);
            modelCache.Add(modelKey, model);
        }
        if (!model.AddressSpace.Nodes.ContainsKey(candidate.ExpandedNodeId))
        {
            throw new KeyNotFoundException(
                $"Candidate type '{candidate.ExpandedNodeId}' was not found in exact model " +
                $"'{candidate.ModelUri}' version '{candidate.Version ?? "latest"}'.");
        }

        var declarations = await _models.ExpandDeclarationsAsync(
            candidate.ModelUri,
            candidate.ExpandedNodeId,
            candidate.Version,
            cancellationToken);
        var supertypes = ResolveSupertypes(
            candidate.ExpandedNodeId, model.Supertypes);
        var enriched = candidate with
        {
            SupertypeExpandedNodeId = supertypes.FirstOrDefault() ??
                candidate.SupertypeExpandedNodeId,
            SupertypeIds = supertypes,
            MemberNames = declarations
                .Select(declaration => declaration.Node.BrowseName.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
            DataTypes = declarations
                .Where(declaration => declaration.Node.DataType is not null)
                .Select(declaration => declaration.Node.DataType!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(dataType => dataType, StringComparer.Ordinal)
                .ToArray(),
            MethodNames = declarations
                .Where(declaration => declaration.Node.NodeClass == "Method")
                .Select(declaration => declaration.Node.BrowseName.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray(),
        };
        candidateCache.Add(candidateKey, enriched);
        return enriched;
    }

    static IReadOnlyList<string> ResolveSupertypes(
        string candidateId,
        IReadOnlyDictionary<string, string> supertypes)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { candidateId };
        var current = candidateId;
        while (supertypes.TryGetValue(current, out var supertype))
        {
            if (!visited.Add(supertype))
                throw new InvalidOperationException(
                    $"Cycle detected in companion type hierarchy at '{supertype}'.");
            result.Add(supertype);
            current = supertype;
        }
        return result;
    }

    static CompanionEntitySignature CreateSignature(
        AddressSpaceNode root,
        SubtreeInfo subtree,
        AddressSpaceGraph graph)
    {
        var nodes = subtree.Nodes;

        return new CompanionEntitySignature
        {
            EntityId = root.NodeId,
            Root = root,
            Nodes = nodes,
            NameTokens = Tokenize(root.BrowseName.Name),
            ParentContextTokens = BuildParentContextTokens(root, graph),
            DescriptionTokens = Tokenize(string.Join(
                " ", nodes.Select(n => $"{n.DisplayName} {n.Description} {n.ValueText}"))),
            ValueTokens = Tokenize(string.Join(" ", nodes.Select(n => n.ValueText))),
            MemberNames = nodes.Skip(1)
                .Select(n => CompanionDeterministicCandidateScorer.NormalizeName(n.BrowseName.Name))
                .Where(n => n.Length > 0)
                .ToHashSet(StringComparer.Ordinal),
            DataTypes = nodes
                .Where(n => n.DataType is not null)
                .Select(n => CompanionDeterministicCandidateScorer.NormalizeNodeIdentity(n.DataType!))
                .ToHashSet(StringComparer.Ordinal),
            MethodNames = nodes
                .Where(n => n.NodeClass == "Method")
                .Select(n => CompanionDeterministicCandidateScorer.NormalizeName(n.BrowseName.Name))
                .ToHashSet(StringComparer.Ordinal),
            MaximumDepth = subtree.MaximumDepth,
        };
    }

    static SubtreeInfo BuildSubtree(
        string nodeId,
        IReadOnlyDictionary<string, AddressSpaceNode> included,
        Dictionary<string, SubtreeInfo> cache,
        HashSet<string> active)
    {
        if (cache.TryGetValue(nodeId, out var cached)) return cached;
        if (!active.Add(nodeId))
            throw new InvalidOperationException(
                $"Cycle detected in source hierarchical references at '{nodeId}'.");

        var root = included[nodeId];
        var nodes = new Dictionary<string, AddressSpaceNode>(StringComparer.Ordinal)
        {
            [root.NodeId] = root,
        };
        var maximumDepth = 0;
        foreach (var childId in root.Children)
        {
            if (!included.ContainsKey(childId)) continue;
            var child = BuildSubtree(childId, included, cache, active);
            maximumDepth = Math.Max(maximumDepth, child.MaximumDepth + 1);
            foreach (var childNode in child.Nodes)
                nodes.TryAdd(childNode.NodeId, childNode);
        }
        active.Remove(nodeId);

        var result = new SubtreeInfo(
            nodes.Values.OrderBy(node => node.BrowsePath, StringComparer.Ordinal).ToArray(),
            maximumDepth);
        cache[nodeId] = result;
        return result;
    }

    static IReadOnlySet<string> BuildParentContextTokens(
        AddressSpaceNode root,
        AddressSpaceGraph graph)
    {
        var parentNames = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.NodeId };
        var parentId = root.ParentNodeId;
        for (var depth = 0; depth < 4 && parentId is not null; depth++)
        {
            if (!visited.Add(parentId) || !graph.Nodes.TryGetValue(parentId, out var parent))
                break;
            parentNames.Add(parent.BrowseName.Name);
            parentId = parent.ParentNodeId;
        }
        return Tokenize(string.Join(" ", parentNames));
    }

    internal static IReadOnlySet<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.Ordinal);
        return Regex.Matches(text, @"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.Ordinal);
    }

    static string BuildQuery(CompanionEntitySignature entity)
    {
        var terms = entity.NameTokens
            .Concat(entity.ParentContextTokens.Take(4))
            .Concat(entity.MemberNames.Take(12))
            .Concat(entity.MethodNames.Take(6))
            .Distinct(StringComparer.Ordinal)
            .Take(24);
        return string.Join(" ", terms.Select(EscapeSearchTerm));
    }

    static string EscapeSearchTerm(string term) =>
        Regex.Replace(term, @"([+\-!(){}\[\]^""~*?:\\/])", "\\$1");

    static CompanionTypeCandidate? ParseCandidate(CompanionSearchDocument document)
    {
        var id = GetString(document.Fields, "node_id");
        var modelUri = GetString(document.Fields, "model_uri");
        var browseName = GetString(document.Fields, "browse_name");
        if (string.IsNullOrWhiteSpace(id) ||
            string.IsNullOrWhiteSpace(modelUri) ||
            string.IsNullOrWhiteSpace(browseName))
            return null;

        return new CompanionTypeCandidate
        {
            CandidateId = id,
            ExpandedNodeId = id,
            ModelUri = modelUri,
            Version = GetString(document.Fields, "model_version", "spec_version"),
            PublicationDate = GetDate(document.Fields, "publication_date"),
            BrowseName = browseName,
            Description = GetString(document.Fields, "description"),
            SupertypeExpandedNodeId = GetString(document.Fields, "parent_node_id"),
            SearchScore = document.Score ?? 0,
        };
    }

    static string? GetString(IReadOnlyDictionary<string, object?> fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetValue(name, out var value) || value is null) continue;
            if (value is string text) return text;
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    static IReadOnlyList<string> GetStrings(
        IReadOnlyDictionary<string, object?> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || value is null) return [];
        if (value is IEnumerable<string> strings) return strings.ToArray();
        if (value is IEnumerable<object> objects)
            return objects.Select(o => Convert.ToString(o, CultureInfo.InvariantCulture))
                .Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().ToArray();
        if (value is string text)
            return text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [];
    }

    static DateTimeOffset? GetDate(
        IReadOnlyDictionary<string, object?> fields,
        string name)
    {
        if (!fields.TryGetValue(name, out var value) || value is null) return null;
        if (value is DateTimeOffset date) return date;
        return DateTimeOffset.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed) ? parsed : null;
    }

    sealed record SubtreeInfo(IReadOnlyList<AddressSpaceNode> Nodes, int MaximumDepth);
}
