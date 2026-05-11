using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

// ═══════════════════════════════════════════════════════════════════════
// Agent Tool Dispatcher
//
// Reflects the OpcUaKb.Core assembly for [McpServerToolType] static
// classes containing [McpServerTool] methods and exposes them as
// OpenAI Chat Completions function-calling tool definitions. At runtime,
// InvokeAsync parses GPT's JSON arguments, resolves service-typed
// parameters from the IServiceProvider, and invokes the static method
// via reflection, returning the tool's string result.
//
// This is the bridge that lets a tool-using agent loop in OpcUaKb.Agent
// reuse the exact same tool implementations that the MCP server exposes,
// so the chatbot and external MCP clients share one source of truth.
// ═══════════════════════════════════════════════════════════════════════

public sealed class AgentToolDispatcher
{
    readonly IServiceProvider _services;
    readonly Dictionary<string, ToolEntry> _tools;
    readonly List<object> _toolDefinitions;

    public AgentToolDispatcher(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tools = new Dictionary<string, ToolEntry>(StringComparer.Ordinal);
        _toolDefinitions = new List<object>();

        // Scan the Core assembly for static tool classes. Use GetTypes() so
        // internal `static class` declarations (most tools) are included —
        // the [McpServerToolType] attribute is what marks them.
        var assembly = typeof(SearchService).Assembly;
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue; // static class
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() == null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr == null) continue;

                var name = !string.IsNullOrWhiteSpace(toolAttr.Name) ? toolAttr.Name : method.Name;
                var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                var parameters = method.GetParameters();

                _tools[name] = new ToolEntry(method, parameters);
                _toolDefinitions.Add(BuildToolDefinition(name, description, parameters));
            }
        }
    }

    /// <summary>
    /// Returns the OpenAI Chat Completions "tools" array — a list of
    /// { type: "function", function: { name, description, parameters } }
    /// objects ready to be embedded in a chat-completion request body.
    /// </summary>
    public IReadOnlyList<object> GetToolDefinitions() => _toolDefinitions;

    /// <summary>The set of tool names registered with the dispatcher.</summary>
    public IReadOnlyCollection<string> ToolNames => _tools.Keys;

    /// <summary>
    /// Invokes the tool whose Name matches <paramref name="toolName"/>,
    /// parsing arguments from <paramref name="jsonArguments"/>. Returns the
    /// string result of the tool method. Throws InvalidOperationException
    /// for unknown tool names; the agent loop should catch this and feed
    /// a synthetic error response back to the model so it can recover.
    /// </summary>
    public async Task<string> InvokeAsync(string toolName, string jsonArguments, CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(toolName, out var entry))
            throw new InvalidOperationException($"Unknown tool '{toolName}'.");

        JsonObject argsObj;
        try
        {
            var parsed = string.IsNullOrWhiteSpace(jsonArguments)
                ? new JsonObject()
                : JsonNode.Parse(jsonArguments) as JsonObject ?? new JsonObject();
            argsObj = parsed;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Tool '{toolName}' received invalid JSON arguments: {ex.Message}", ex);
        }

        var values = new object?[entry.Parameters.Length];
        for (int i = 0; i < entry.Parameters.Length; i++)
        {
            var p = entry.Parameters[i];

            if (IsServiceParameter(p.ParameterType))
            {
                values[i] = _services.GetService(p.ParameterType)
                    ?? throw new InvalidOperationException(
                        $"Tool '{toolName}' parameter '{p.Name}' of type {p.ParameterType.Name} is not registered in the service provider.");
                continue;
            }

            if (p.Name != null && argsObj.TryGetPropertyValue(p.Name, out var node) && node != null)
            {
                values[i] = ConvertArgument(node, p.ParameterType);
            }
            else if (p.HasDefaultValue)
            {
                values[i] = p.DefaultValue;
            }
            else if (IsNullableParameter(p))
            {
                values[i] = null;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Tool '{toolName}' missing required argument '{p.Name}'.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        object? result;
        try
        {
            result = entry.Method.Invoke(null, values);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw new InvalidOperationException(
                $"Tool '{toolName}' threw: {tie.InnerException.Message}", tie.InnerException);
        }

        // Tool methods return Task<string> (async) or string (sync — e.g., ValidateNodeSet).
        if (result is Task task)
        {
            await task.ConfigureAwait(false);
            var resultProp = task.GetType().GetProperty("Result");
            return resultProp?.GetValue(task)?.ToString() ?? "";
        }
        return result?.ToString() ?? "";
    }

    // ───────────────────────────────────────────────────────────────────
    // Tool-definition schema construction
    // ───────────────────────────────────────────────────────────────────

    static object BuildToolDefinition(string name, string description, ParameterInfo[] parameters)
    {
        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var p in parameters)
        {
            if (IsServiceParameter(p.ParameterType)) continue;
            if (p.Name == null) continue;

            var schema = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["type"] = JsonTypeFor(p.ParameterType),
            };

            var paramDesc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(paramDesc))
                schema["description"] = paramDesc;

            if (p.HasDefaultValue && p.DefaultValue != null)
                schema["default"] = p.DefaultValue;

            properties[p.Name] = schema;

            if (!p.HasDefaultValue && !IsNullableParameter(p))
                required.Add(p.Name);
        }

        return new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters = new
                {
                    type = "object",
                    properties,
                    required,
                },
            },
        };
    }

    static string JsonTypeFor(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u == typeof(string)) return "string";
        if (u == typeof(int) || u == typeof(long) || u == typeof(short)) return "integer";
        if (u == typeof(bool)) return "boolean";
        if (u == typeof(double) || u == typeof(float) || u == typeof(decimal)) return "number";
        return "string"; // fallback for complex types — tools are expected to use scalars
    }

    static bool IsServiceParameter(Type t)
    {
        return t == typeof(SearchService) || t == typeof(KbService);
    }

    static bool IsNullableParameter(ParameterInfo p)
    {
        if (p.ParameterType.IsValueType)
            return Nullable.GetUnderlyingType(p.ParameterType) != null;

        var ctx = new NullabilityInfoContext();
        var info = ctx.Create(p);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState == NullabilityState.Nullable;
    }

    static object? ConvertArgument(JsonNode node, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Null)
            return null;

        try
        {
            if (underlying == typeof(string))
            {
                if (node is JsonValue jv && jv.GetValueKind() == JsonValueKind.String)
                    return jv.GetValue<string>();
                return node.ToJsonString().Trim('"');
            }
            if (underlying == typeof(int)) return CoerceInt(node);
            if (underlying == typeof(long)) return (long)CoerceInt(node);
            if (underlying == typeof(short)) return (short)CoerceInt(node);
            if (underlying == typeof(bool)) return CoerceBool(node);
            if (underlying == typeof(double)) return CoerceDouble(node);
            if (underlying == typeof(float)) return (float)CoerceDouble(node);
            if (underlying == typeof(decimal)) return (decimal)CoerceDouble(node);
        }
        catch
        {
            // fall through to ToString fallback
        }

        return node.ToString();
    }

    static int CoerceInt(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return checked((int)l);
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return int.Parse(node.ToString());
    }

    static bool CoerceBool(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        }
        return bool.Parse(node.ToString());
    }

    static double CoerceDouble(JsonNode node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var p)) return p;
        }
        return double.Parse(node.ToString());
    }

    sealed record ToolEntry(MethodInfo Method, ParameterInfo[] Parameters);
}
