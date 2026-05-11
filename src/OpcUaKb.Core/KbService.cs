using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// ═══════════════════════════════════════════════════════════════════════
// Knowledge Base Retrieve + GPT-4o Completion Service
// Calls the Azure AI Search KB retrieve API for grounding, then sends
// the grounded context to GPT-4o for answer synthesis.
// ═══════════════════════════════════════════════════════════════════════

public sealed class KbService
{
    const string DefaultKbName = "opcua-kb";
    const string SearchApiVersion = "2025-11-01-preview";

    readonly HttpClient _searchHttp;
    readonly AoaiChatClient _aoai;
    readonly string _searchEndpoint;
    readonly string _kbName;

    static readonly string SystemPrompt = """
        You are an OPC UA expert assistant. You answer questions about OPC UA technology
        using the OPC UA reference specifications as your knowledge base.
        
        When answering:
        - Cite specification part numbers and sections (e.g., "Part 4, Section 5.6.2")
        - Be technically precise — use correct OPC UA terminology
        - If the grounding data doesn't cover the question, say so
        
        You have access to grounding data from reference.opcfoundation.org covering
        all OPC UA specification parts, companion specs, and NodeSet definitions.
        """;

    public KbService()
    {
        _searchEndpoint = Environment.GetEnvironmentVariable("SEARCH_ENDPOINT")
            ?? throw new InvalidOperationException("Set SEARCH_ENDPOINT environment variable");
        var searchApiKey = Environment.GetEnvironmentVariable("SEARCH_API_KEY")
            ?? throw new InvalidOperationException("Set SEARCH_API_KEY environment variable");

        _kbName = Environment.GetEnvironmentVariable("KB_NAME") ?? DefaultKbName;

        _searchHttp = new HttpClient();
        _searchHttp.DefaultRequestHeaders.Add("api-key", searchApiKey);

        _aoai = new AoaiChatClient();
    }

    /// <summary>Whether RAG is available (AOAI_ENDPOINT configured).</summary>
    public bool Available => _aoai.Available;

    /// <summary>
    /// Retrieve grounding data from the KB, then generate a GPT-4o answer.
    /// </summary>
    public async Task<string> AskAsync(string query, string? context = null)
    {
        if (!_aoai.Available)
            return "RAG not available — set AOAI_ENDPOINT environment variable to enable.";

        // Step 1: Retrieve grounding from KB
        var grounding = await RetrieveGroundingAsync(query, context);

        // Step 2: Generate answer with GPT-4o
        return await ChatCompletionAsync(query, grounding, context);
    }

    async Task<string?> RetrieveGroundingAsync(string query, string? context)
    {
        var messages = new List<object>();

        // Add conversation context if provided
        if (!string.IsNullOrWhiteSpace(context))
        {
            var lines = context.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var role = i % 2 == 0 ? "user" : "assistant";
                messages.Add(new
                {
                    role,
                    content = new[] { new { type = "text", text = lines[i].Trim() } }
                });
            }
        }

        messages.Add(new
            {
                role = "user",
                content = new[] { new { type = "text", text = query } }
            }
        );

        var body = new
        {
            messages,
            retrievalReasoningEffort = new { kind = "medium" }
        };

        var response = await _searchHttp.PostAsync(
            $"{_searchEndpoint}/knowledgebases/{_kbName}/retrieve?api-version={SearchApiVersion}",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode) return null;

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        return json?["response"]?[0]?["content"]?[0]?["text"]?.GetValue<string>();
    }

    async Task<string> ChatCompletionAsync(string query, string? grounding, string? context)
    {
        var messages = new List<object>
        {
            new { role = "system", content = SystemPrompt }
        };

        if (!string.IsNullOrWhiteSpace(grounding))
        {
            messages.Add(new
            {
                role = "system",
                content = $"Use the following OPC UA specification data to answer the user's question. " +
                          $"Cite [ref_id:N] references where applicable.\n\n{grounding}"
            });
        }

        // Add conversation context for follow-up questions
        if (!string.IsNullOrWhiteSpace(context))
        {
            var lines = context.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var role = i % 2 == 0 ? "user" : "assistant";
                messages.Add(new { role, content = lines[i].Trim() });
            }
        }

        messages.Add(new { role = "user", content = query });

        var body = new
        {
            messages,
            model = _aoai.Deployment,
            temperature = 0.3,
            max_tokens = 2000
        };

        var json = await _aoai.ChatCompletionAsync(body);
        return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
            ?? "(no response from GPT-4o)";
    }
}
