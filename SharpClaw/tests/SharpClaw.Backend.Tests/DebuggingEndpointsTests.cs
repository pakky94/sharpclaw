using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class DebuggingEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task DebugToolCall_ExecutesWorkspaceTool_WithoutCreatingSession()
    {
        await fixture.ResetStateAsync();

        using var toolResponse = await fixture.Api.DebugToolCallAsync(
            "ws_list_files",
            arguments: new { path = ".", recursive = false });

        Assert.Equal("ws_list_files", toolResponse.RootElement.GetProperty("toolName").GetString());
        Assert.True(toolResponse.RootElement.TryGetProperty("callId", out _));

        var result = toolResponse.RootElement.GetProperty("result");
        Assert.Equal(".", result.GetProperty("path").GetString());
        Assert.True(result.GetProperty("entries").ValueKind == System.Text.Json.JsonValueKind.Array);

        using var sessions = await fixture.Api.GetSessionsAsync(agentId: 1);
        var sessionRows = sessions.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Empty(sessionRows);
    }

    [Fact]
    public async Task DebugToolCall_UnknownTool_ReturnsNotFound()
    {
        await fixture.ResetStateAsync();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            _ = await fixture.Api.DebugToolCallAsync("tool_does_not_exist"));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }
}
