using SharpClaw.Backend.Tests.Helpers;
using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class ToolInvocationTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task ListFilesToolCall_IsInvoked_AndPersistedInHistory()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("ws_list_files",
            """{"path":".","recursive":false}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_list_files");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Tool invocation finished.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommandToolCall_RequiresApproval_ThenCompletes()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_run_command",
            """{"command":"echo test-from-tool"}""",
            c => c.Messages.Last().Content == "TEST_TOOL_RUN_COMMAND");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId,  "TEST_TOOL_RUN_COMMAND");

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        await fixture.Api.ApproveAsync(sessionId, token);

        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_run_command");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");
    }

    [Fact]
    public async Task Stream_EmitsToolCall_BeforeApprovalRequired()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_write_file",
            """{"path":"tmp/sse-approval-check.txt","content":"from-test","mode":"overwrite"}""",
            c => c.Messages.Last().Content == "TEST_TOOL_WRITE_FILE");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var runId = await fixture.Api.EnqueueMessageAsync(sessionId,  "TEST_TOOL_WRITE_FILE");

        var events = await fixture.Api.WaitForStreamEventTypes(
            sessionId,
            runId,
            requiredTypes: ["tool_call", "approval_required"],
            timeout: TimeSpan.FromSeconds(20));

        var toolCallIndex = ResponseHelpers.IndexOf(events, "tool_call");
        var approvalIndex = ResponseHelpers.IndexOf(events, "approval_required");

        Assert.True(toolCallIndex >= 0, "tool_call event was not observed.");
        Assert.True(approvalIndex >= 0, "approval_required event was not observed.");
        Assert.True(toolCallIndex < approvalIndex, $"Expected tool_call before approval_required. Seen: {string.Join(", ", events)}");
        Assert.True(events.All(e => e.Type != "completed"));

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        await fixture.Api.ApproveAsync(sessionId, token);

        events = await fixture.Api.WaitForStreamCompleted(sessionId, runId);
        Assert.Equal("completed", events[^1].Type);
    }

    [Fact]
    public async Task MultipleCallInvocations()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallsSse(["ws_list_files", "ws_list_files"],
            ["""{"path":".","recursive":false}""", """{"path":".","recursive":false}"""],
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_list_files");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Tool invocation finished.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultipleCallInvocations_TaskTool()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallsSse(["task", "task"],
            [
                """
                {
                  "description": "task1",
                  "prompt": "task prompt 1"
                }
                """,
                """
                {
                  "description": "task2",
                  "prompt": "task prompt 2"
                }
                """,
            ],
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        fixture.LlmServer?.TextSse("task result 1",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "task prompt 1");

        fixture.LlmServer?.TextSse("task result 2",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "task prompt 2");

        fixture.LlmServer?.TextSse("result after both tasks",
            c => (c.Messages.Last().Content?.Contains("task result 1") ?? false)
                 && (c.Messages.Last().Content?.Contains("task result 2") ?? false));

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await Task.Delay(5_000_000);
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_list_files");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Tool invocation finished.", StringComparison.Ordinal));
    }
}