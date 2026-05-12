using Azure.Search.Documents.Models;

// ═══════════════════════════════════════════════════════════════════════
// Backward-compatible spec identifier filter helpers.
//
// During the rollout of the new `spec_section` document schema, tools must
// work cleanly against BOTH the legacy index (uses `spec_part` field) and
// the new `opcua-content-index-v2` index (introduces `spec_id`). Always
// produce an OR-form filter so docs from either schema match.
//
// Example:  SpecFilter.Match("Part4")
//   →  (spec_part eq 'Part4' or spec_id eq 'Part4')
// ═══════════════════════════════════════════════════════════════════════

static class SpecFilter
{
    /// <summary>Escape a literal value for use in an OData v4 string literal.</summary>
    public static string Escape(string value) => value?.Replace("'", "''") ?? "";

    /// <summary>
    /// Returns an OData filter clause that matches documents whose spec identifier
    /// equals <paramref name="spec"/> in either the legacy <c>spec_part</c> field
    /// (raw HTML chunk schema: text/table/diagram/nodeset/nodeset_summary/...) or
    /// the new <c>spec_id</c> field (spec_section schema).
    /// </summary>
    public static string Match(string spec)
    {
        var v = Escape(spec);
        return $"(spec_part eq '{v}' or spec_id eq '{v}')";
    }

    /// <summary>
    /// Reads a <c>Collection(Edm.String)</c> field (e.g. <c>figures</c>, <c>breadcrumb</c>)
    /// from a <see cref="SearchDocument"/>. Returns an empty list when missing/null.
    /// </summary>
    public static List<string> ReadStringCollection(SearchDocument d, string field)
    {
        if (!d.TryGetValue(field, out var v) || v == null) return [];
        if (v is IEnumerable<string> strs) return [.. strs];
        if (v is IEnumerable<object> objs)
            return [.. objs.Select(x => x?.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s))];
        return [];
    }
}
