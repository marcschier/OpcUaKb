using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
static class SearchDocsRagTool
{
    [McpServerTool(Name = "search_docs_rag"),
     Description("Search OPC UA specification documentation and get an AI-synthesized answer grounded by " +
        "the knowledge base. Uses Azure AI Search retrieval + GPT-4o for natural language answers " +
        "with specification references. Best for general questions about OPC UA concepts, protocol " +
        "details, services, security models, or any specification content. " +
        "For structured NodeSet queries (specific ObjectTypes, Variables, Methods), use search_nodes instead.")]
    public static async Task<string> SearchDocsRag(
        KbService kb,
        [Description("Natural language question about OPC UA specifications " +
            "(e.g., 'How does OPC UA session authentication work?', " +
            "'What are the security modes defined in Part 4?', " +
            "'Explain the Publish/Subscribe communication model')")] string query,
        [Description("Optional prior conversation context for follow-up questions. " +
            "Format as alternating user/assistant messages separated by newlines.")] string? context = null)
    {
        if (!kb.Available)
            return "RAG search is not available — AOAI_ENDPOINT is not configured on this server. " +
                   "Use search_docs for text search without AI synthesis.";

        return await kb.AskAsync(query, context);
    }
}
