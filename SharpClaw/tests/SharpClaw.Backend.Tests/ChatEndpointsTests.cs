using SharpClaw.Backend.Tests.Infrastructure;

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
}
