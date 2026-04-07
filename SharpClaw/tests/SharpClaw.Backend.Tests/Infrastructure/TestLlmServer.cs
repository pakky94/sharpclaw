using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Backend.Tests.Infrastructure;

public sealed class TestLlmServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _serveTask;
    private readonly List<(Func<RequestContext, bool> Condition, Func<HttpListenerContext, Task> Action)> _mocks = [];

    private TestLlmServer(HttpListener listener, Uri endpoint)
    {
        _listener = listener;
        Endpoint = endpoint;
        _serveTask = Task.Run(ServeAsync);
    }

    public Uri Endpoint { get; }

    public static TestLlmServer Start()
    {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        return new TestLlmServer(listener, new Uri($"{prefix}v1/"));
    }

    public void ResetMocks()
    {
        _mocks.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();

        try
        {
            await _serveTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context), _cts.Token);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (context.Request.HttpMethod == HttpMethod.Post.Method &&
                path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                await HandleChatCompletionAsync(context);
                return;
            }

            if (context.Request.HttpMethod == HttpMethod.Post.Method &&
                path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEmbeddingsAsync(context);
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task HandleChatCompletionAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        using var json = JsonDocument.Parse(body);
        var request = json.RootElement;

        var messages = request.GetProperty("messages").EnumerateArray().ToArray();
        var latestUserText = GetLatestUserText(messages);

        var ctx = new RequestContext
        {
            LatestUserText = latestUserText,
            Messages = request.GetProperty("messages").Deserialize<Message[]>() ?? [],
        };
        var match = _mocks.FirstOrDefault(x => x.Condition(ctx));

        if (match.Action is null)
            throw new InvalidOperationException("Mock response from integration test.");

        await match.Action(context);
    }

    public void ToolCallSse(
        string toolName,
        string argumentsJson,
        Func<RequestContext, bool> condition,
        string? callId = null)
    {
        callId ??= $"call_{Guid.NewGuid():N}";
        _mocks.Add((condition, c => WriteToolCallSseAsync(c, callId, toolName, argumentsJson)));
    }

    public void ToolCallsSse(
        string[] toolNames,
        string[] argumentsJsons,
        Func<RequestContext, bool> condition,
        string[]? callIds = null)
    {
        callIds ??= Enumerable.Range(0, toolNames.Length).Select(_ => $"call_{Guid.NewGuid():N}").ToArray();
        _mocks.Add((condition, c => WriteToolCallSseAsync(c, callIds, toolNames, argumentsJsons)));
    }

    public void TextSse(string text, Func<RequestContext, bool> condition)
    {
        _mocks.Add((condition, c => WriteTextSseAsync(c, text)));
    }

    private static async Task WriteToolCallSseAsync(
        HttpListenerContext context,
        string callId,
        string toolName,
        string argumentsJson)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chunkId = $"chatcmpl-mock-{Guid.NewGuid():N}";

        var firstChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = "assistant",
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = callId,
                                type = "function",
                                function = new { name = toolName, arguments = argumentsJson },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        });
        var finalChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "tool_calls",
                },
            },
        });

        await WriteSseDataAsync(writer, firstChunk);
        await WriteSseDataAsync(writer, finalChunk);
        await WriteSseDataAsync(writer, "[DONE]");
        context.Response.Close();
    }

    private static async Task WriteToolCallSseAsync(
        HttpListenerContext context,
        string[] callIds,
        string[] toolNames,
        string[] argumentsJsons)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        await using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chunkId = $"chatcmpl-mock-{Guid.NewGuid():N}";

        var firstChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        role = "assistant",
                        tool_calls = toolNames.Zip(argumentsJsons, callIds)
                            .Select((x, idx) =>
                                new
                                {
                                    index = idx,
                                    id = x.Third,
                                    type = "function",
                                    function = new { name = x.First, arguments = x.Second },
                                }),
                    },
                    finish_reason = (string?)null,
                },
            },
        });
        var finalChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "tool_calls",
                },
            },
        });

        await WriteSseDataAsync(writer, firstChunk);
        await WriteSseDataAsync(writer, finalChunk);
        await WriteSseDataAsync(writer, "[DONE]");
        context.Response.Close();
    }

    private static async Task WriteTextSseAsync(HttpListenerContext context, string text)
    {
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.SendChunked = true;

        using var writer = new StreamWriter(context.Response.OutputStream, new UTF8Encoding(false));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var chunkId = $"chatcmpl-mock-{Guid.NewGuid():N}";

        var firstChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { role = "assistant", content = text },
                    finish_reason = (string?)null,
                },
            },
        });
        var finalChunk = JsonSerializer.Serialize(new
        {
            id = chunkId,
            @object = "chat.completion.chunk",
            created = now,
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop",
                },
            },
        });

        await WriteSseDataAsync(writer, firstChunk);
        await WriteSseDataAsync(writer, finalChunk);
        await WriteSseDataAsync(writer, "[DONE]");
        context.Response.Close();
    }

    private static async Task WriteSseDataAsync(StreamWriter writer, string data)
    {
        await writer.WriteLineAsync($"data: {data}");
        await writer.WriteLineAsync();
        await writer.FlushAsync();
    }

    private static string? GetLatestUserText(JsonElement[] messages)
    {
        for (var i = messages.Length - 1; i >= 0; i--)
        {
            if (GetRole(messages[i]) != "user")
                continue;

            if (messages[i].TryGetProperty("content", out var content))
                return GetTextFromContent(content);
        }

        return null;
    }

    private static string? GetRole(JsonElement message) =>
        message.TryGetProperty("role", out var role) ? role.GetString() : null;

    private static string? GetTextFromContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type) || type.GetString() != "text")
                continue;

            if (part.TryGetProperty("text", out var text))
                return text.GetString();
        }

        return null;
    }

    private static object BuildNonStreamingResponse(string text) =>
        new
        {
            id = $"chatcmpl-mock-{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = "mock-model",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = "stop",
                },
            },
        };

    private static Task HandleEmbeddingsAsync(HttpListenerContext context) =>
        WriteJsonAsync(context, new
        {
            data = new[]
            {
                new
                {
                    embedding = new[] { 0.1f, 0.2f, 0.3f },
                },
            },
        });

    private static async Task WriteJsonAsync(HttpListenerContext context, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public class RequestContext
    {
        public string? LatestUserText { get; set; } = string.Empty;
        public Message[] Messages { get; set; } = [];
    }
}

public class Message
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }
    [JsonPropertyName("content")]
    public string? Content { get; set; }
    [JsonPropertyName("tool_calls")]
    public ToolCall[] ToolCalls { get; set; } = [];

    public class ToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
        [JsonPropertyName("type")]
        public string? Type { get; set; }
        [JsonPropertyName("function")]
        public ToolFunction? Function { get; set; }

        public class ToolFunction
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }
        }
    }
}