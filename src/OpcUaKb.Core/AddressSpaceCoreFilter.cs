using System.Text.RegularExpressions;

public sealed record AddressSpaceFilterOptions
{
    public IReadOnlyList<string> IncludePaths { get; init; } = [];
    public IReadOnlyList<string> ExcludePaths { get; init; } = [];
}

public sealed record AddressSpaceFilterResult
{
    public required IReadOnlyList<AddressSpaceNode> IncludedNodes { get; init; }
    public required IReadOnlyList<AddressSpaceNode> PassThroughNodes { get; init; }
    public required IReadOnlyList<AddressSpaceNode> ExcludedNodes { get; init; }
}

public interface ICompanionModelCatalog
{
    Task<IReadOnlyList<CompanionModelCatalogEntry>> GetCatalogAsync(
        CancellationToken cancellationToken = default);
}

public sealed class AddressSpaceCoreFilter
{
    static readonly string[] CoreSubtreePaths = ["/Types", "/Views", "/Objects/Server"];

    readonly ICompanionModelCatalog? _catalog;

    public AddressSpaceCoreFilter(ICompanionModelCatalog? catalog = null) => _catalog = catalog;

    public async Task<AddressSpaceFilterResult> FilterAsync(
        AddressSpaceGraph graph,
        AddressSpaceFilterOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        options ??= new AddressSpaceFilterOptions();

        var officialNamespaces = _catalog is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _catalog.GetCatalogAsync(cancellationToken))
                .Where(m => m.IsOfficial)
                .SelectMany(m => new[] { m.ModelUri, m.NamespaceUri }.Concat(m.NamespaceUris))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToHashSet(StringComparer.Ordinal);

        var included = new List<AddressSpaceNode>();
        var passThrough = new List<AddressSpaceNode>();
        var excluded = new List<AddressSpaceNode>();
        foreach (var node in graph.Nodes.Values.OrderBy(n => n.BrowsePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsCoreNode(node) ||
                CoreSubtreePaths.Any(p => IsPathWithin(node.BrowsePath, p)) ||
                !MatchesIncludes(node.BrowsePath, options.IncludePaths) ||
                MatchesAny(node.BrowsePath, options.ExcludePaths))
            {
                excluded.Add(node);
                continue;
            }

            if (IsOfficiallyTyped(node, officialNamespaces))
                passThrough.Add(node);
            else
                included.Add(node);
        }

        return new AddressSpaceFilterResult
        {
            IncludedNodes = included,
            PassThroughNodes = passThrough,
            ExcludedNodes = excluded,
        };
    }

    static bool IsCoreNode(AddressSpaceNode node) =>
        string.Equals(node.BrowseName.NamespaceUri, AddressSpaceNodeSetReader.UaNamespace, StringComparison.Ordinal);

    static bool IsOfficiallyTyped(AddressSpaceNode node, HashSet<string> officialNamespaces)
    {
        if (node.TypeDefinition is null) return false;
        var namespaceUri = ExpandedNodeIdNamespace(node.TypeDefinition);
        return namespaceUri is not null &&
               namespaceUri != AddressSpaceNodeSetReader.UaNamespace &&
               officialNamespaces.Contains(namespaceUri);
    }

    static string? ExpandedNodeIdNamespace(string expandedNodeId)
    {
        var start = expandedNodeId.IndexOf("nsu=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += 4;
        var end = expandedNodeId.IndexOf(';', start);
        return end < 0 ? null : Uri.UnescapeDataString(expandedNodeId[start..end]);
    }

    static bool MatchesIncludes(string path, IReadOnlyList<string> includes)
    {
        if (includes.Count == 0) return true;
        return includes.Any(include =>
        {
            var normalized = NormalizePath(include);
            return IsPathWithin(path, normalized) ||
                   IsPathWithin(normalized, path) ||
                   GlobMatches(path, normalized);
        });
    }

    static bool MatchesAny(string path, IReadOnlyList<string> patterns) =>
        patterns.Any(pattern =>
        {
            var normalized = NormalizePath(pattern);
            return IsPathWithin(path, normalized) || GlobMatches(path, normalized);
        });

    static bool IsPathWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    static bool GlobMatches(string path, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?')) return false;
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return normalized.TrimEnd('/');
    }
}
