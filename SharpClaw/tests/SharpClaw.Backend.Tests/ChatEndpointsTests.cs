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
        var runId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");

        using var run = await fixture.Api.WaitForRunTerminalStateAsync(
            sessionId,
            runId,
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal("completed", run.RootElement.GetProperty("status").GetString());
        Assert.True(run.RootElement.GetProperty("error").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);

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
}
