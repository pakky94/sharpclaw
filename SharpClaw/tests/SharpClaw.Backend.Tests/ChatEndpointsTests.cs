using SharpClaw.Backend.Tests.Infrastructure;
using System.Text.Json;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class ChatEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task EnqueueMessage_CompletesRun_AndPersistsAssistantReply()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.TextSse("Mock response from integration test.", _ => true);

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");

        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messages = history.RootElement.GetProperty("messages").EnumerateArray().ToArray();

        Assert.Contains(messages, message =>
            message.GetProperty("role").GetString() == "user" &&
            message.GetProperty("text").GetString() == "Reply with one sentence.");

        Assert.Contains(messages, message =>
            message.GetProperty("role").GetString() == "assistant" &&
            (message.GetProperty("text").GetString() ?? string.Empty)
            .Contains("Mock response from integration test.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HistoryResponse_IncludesEstimatedTokenCount()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.TextSse("Mock response from integration test.", _ => true);

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");

        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);

        Assert.True(history.RootElement.TryGetProperty("estimatedTokenCount", out var tokenCount));
        Assert.True(tokenCount.GetInt64() > 0, "estimatedTokenCount should be greater than zero after a completed run.");
    }

    [Fact]
    public async Task Stream_EmitsTokenUsage_AfterAssistantResponse()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.TextSse("Mock response from integration test.", _ => true);

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");

        var events = await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        Assert.Contains(events, e => e.Type == "token_usage");

        var tokenEvent = events.First(e => e.Type == "token_usage");
        var data = ParseStreamEventData(tokenEvent);
        Assert.True(data.TryGetProperty("estimatedTokenCount", out var count));
        Assert.True(count.GetInt64() > 0, "token_usage event should report a positive estimatedTokenCount.");
    }

    [Fact]
    public async Task Stream_EmitsTokenUsage_AfterEachLlmTurn()
    {
        await fixture.ResetStateAsync();

        // First turn: tool call, then text response
        fixture.LlmServer!.ToolCallSse("ws_list_files",
            """{"path":".","recursive":false}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_MULTI_TURN_TOKEN");

        fixture.LlmServer?.TextSse("First response done.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_MULTI_TURN_TOKEN");

        var events = await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        var tokenEvents = events.Where(e => e.Type == "token_usage").ToList();
        Assert.True(tokenEvents.Count >= 2,
            $"Expected at least 2 token_usage events (one per LLM turn), got {tokenEvents.Count}.");

        // Each token_usage event should have a valid estimatedTokenCount
        foreach (var tokenEvent in tokenEvents)
        {
            var data = ParseStreamEventData(tokenEvent);
            Assert.True(data.TryGetProperty("estimatedTokenCount", out var count));
            Assert.True(count.GetInt64() > 0);
        }
    }

    private static JsonElement ParseStreamEventData(StreamEvent ev)
    {
        var json = ev.Payload.StartsWith("data: ", StringComparison.Ordinal)
            ? ev.Payload["data: ".Length..]
            : ev.Payload;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").Clone();
    }
}
