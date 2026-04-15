using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;

// ═══════════════════════════════════════════════════════════════════════
// Shared Azure AI Search client for all MCP tools.
// ═══════════════════════════════════════════════════════════════════════

sealed class SearchService
{
    const string DefaultIndexName = "opcua-content-index";

    public SearchClient Client { get; }

    public SearchService()
    {
        var endpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("Set SEARCH_ENDPOINT environment variable");
        var apiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
            ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");
        var indexName = Environment.GetEnvironmentVariable("SEARCH_INDEX_NAME") ?? DefaultIndexName;

        Client = new SearchClient(new Uri(endpoint), indexName, new AzureKeyCredential(apiKey));
    }

    /// <summary>Searches with optional OData filter and returns formatted results.</summary>
    public async Task<List<SearchResult>> SearchAsync(string? query, string? filter,
        IEnumerable<string>? select = null, int top = 20, IEnumerable<string>? facets = null)
    {
        var options = new SearchOptions
        {
            Filter = filter,
            Size = top,
            IncludeTotalCount = true,
        };

        if (select != null)
            foreach (var f in select) options.Select.Add(f);

        if (facets != null)
            foreach (var f in facets) options.Facets.Add(f);

        var response = await Client.SearchAsync<SearchDocument>(query ?? "*", options);
        var results = new List<SearchResult>();

        await foreach (var result in response.Value.GetResultsAsync())
        {
            results.Add(new SearchResult
            {
                Document = result.Document,
                Score = result.Score,
            });
        }

        return results;
    }

    /// <summary>Searches with facets and returns facet counts.</summary>
    public async Task<Dictionary<string, IList<FacetResult>>> FacetSearchAsync(
        string? filter, IEnumerable<string> facets)
    {
        var options = new SearchOptions
        {
            Filter = filter,
            Size = 0,
            IncludeTotalCount = true,
        };

        foreach (var f in facets) options.Facets.Add(f);

        var response = await Client.SearchAsync<SearchDocument>("*", options);
        return response.Value.Facets?.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value) ?? [];
    }

    public sealed class SearchResult
    {
        public required SearchDocument Document { get; init; }
        public double? Score { get; init; }
    }
}
