using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

// ═══════════════════════════════════════════════════════════════════════
// Azure OpenAI chat-completion HTTP client
// Thin wrapper around the AOAI chat/completions endpoint that handles
// endpoint/deployment/api-version URL composition and dual auth
// (api-key header OR DefaultAzureCredential Bearer token). A single
// shared HttpClient is reused across calls to avoid socket exhaustion;
// a fresh HttpRequestMessage is built per call so the Bearer token is
// re-fetched each time (DefaultAzureCredential caches/refreshes).
// ═══════════════════════════════════════════════════════════════════════

public sealed class AoaiChatClient
{
    const string DefaultGptDeployment = "gpt-4o";
    const string AoaiApiVersion = "2024-10-21";

    readonly HttpClient _http;
    readonly string _endpoint;
    readonly string _deployment;
    readonly TokenCredential? _credential;
    readonly bool _available;

    public AoaiChatClient()
    {
        _endpoint = Environment.GetEnvironmentVariable("AOAI_ENDPOINT") ?? "";
        var apiKey = Environment.GetEnvironmentVariable("AOAI_API_KEY");
        _deployment = Environment.GetEnvironmentVariable("GPT_DEPLOYMENT") ?? DefaultGptDeployment;

        _http = new HttpClient();

        if (!string.IsNullOrEmpty(_endpoint))
        {
            if (!string.IsNullOrEmpty(apiKey))
            {
                _http.DefaultRequestHeaders.Add("api-key", apiKey);
                _credential = null;
            }
            else
            {
                _credential = new DefaultAzureCredential();
            }
            _available = true;
        }
        else
        {
            _available = false;
        }
    }

    /// <summary>True when AOAI_ENDPOINT is set and the client can issue requests.</summary>
    public bool Available => _available;

    /// <summary>Resolved GPT deployment name (from GPT_DEPLOYMENT env var, defaults to gpt-4o).</summary>
    public string Deployment => _deployment;

    /// <summary>
    /// Sends a chat-completion request and returns the parsed JSON response.
    /// Caller supplies the body (messages + tools + temperature etc.) so the
    /// same client can be used for plain RAG completions and tool-using loops.
    /// </summary>
    public async Task<JsonNode?> ChatCompletionAsync(
        object body,
        CancellationToken cancellationToken = default)
    {
        if (!_available)
            throw new InvalidOperationException("AOAI not configured — set AOAI_ENDPOINT environment variable.");

        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_endpoint}/openai/deployments/{_deployment}/chat/completions?api-version={AoaiApiVersion}")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        if (_credential != null)
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                cancellationToken);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        var response = await _http.SendAsync(req, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(responseText);
    }
}
