using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SharpClaw.API.Agents;

public class AgentClient(ChatClient chatClient, AgentExecutionContext context, IServiceProvider serviceProvider)
{
    public delegate Task<AgentClientResponse> GetResponseDelegate(List<ChatMessage> messages, List<AIFunction> tools,
        AgentRunState? runState = null,
        Func<ChatResponseUpdate, ValueTask>? onUpdate = null,
        Func<ValueTask>? onMessageFlushed = null);

    private bool _shouldContinue;

    public async Task<AgentClientResponse> GetResponse(List<ChatMessage> messages, List<AIFunction> tools,
        AgentRunState? runState = null,
        Func<ChatResponseUpdate, ValueTask>? onUpdate = null,
        Func<ValueTask>? onMessageFlushed = null)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();

        var services = new ServiceCollection()
            .Configure<LmStudioConfiguration>(configuration)
            .AddSingleton(context)
            .AddSingleton(configuration)
            .AddSingleton<ChatRepository>()
            .AddSingleton<AgentsRepository>()
            .AddSingleton<FragmentsRepository>()
            .AddSingleton<FragmentEmbeddingService>()
            .AddSingleton<WorkspaceRepository>()
            .AddSingleton<ApprovalService>();

        if (runState is not null)
            services.AddSingleton(runState);

        var agent = chatClient.AsAIAgent(
                tools: [..tools],
                services: services.BuildServiceProvider()
            )
            .AsBuilder()
            .Use(Callback)
            .Build();

        _shouldContinue = false;
        var response = new List<ChatResponse>();
        var messageUpdates = new List<ChatResponseUpdate>();
        string? currentMessageId = null;

        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           options: new ChatClientAgentRunOptions
                           {
                               ChatOptions = new ChatOptions
                               {
                                   Temperature = context.Temperature,
                               },
                           }))
        {
            var chatUpdate = update.AsChatResponseUpdate();

            var hasNewMessageBoundary = messageUpdates.Count > 0 &&
                                        !string.IsNullOrEmpty(chatUpdate.MessageId) &&
                                        !string.IsNullOrEmpty(currentMessageId) &&
                                        !string.Equals(chatUpdate.MessageId, currentMessageId, StringComparison.Ordinal);

            if (hasNewMessageBoundary)
                await FlushMessageUpdates();

            if (!string.IsNullOrEmpty(chatUpdate.MessageId))
                currentMessageId ??= chatUpdate.MessageId;

            messageUpdates.Add(chatUpdate);

            runState?.AppendUpdate(chatUpdate);
            if (onUpdate is not null)
                await onUpdate(chatUpdate);

            // Console.WriteLine(JsonSerializer.Serialize(update, _jsonOptions));

            // TODO: maybe split only when MessageId changes?
            // if (chatUpdate.FinishReason is not null)
            // FlushMessageUpdates();
        }

        await FlushMessageUpdates();

        var queuedTasks = context.QueuedTasks;
        context.QueuedTasks = [];

        return new AgentClientResponse
        {
            Responses = response,
            ShouldContinue = _shouldContinue,
            QueuedTasks = queuedTasks,
        };

        async Task FlushMessageUpdates()
        {
            if (messageUpdates.Count == 0)
                return;

            if (!messageUpdates.Any(u => u.Contents.Count > 0))
                return;

            var m = messageUpdates.ToChatResponse();
            response.Add(m);
            messageUpdates.Clear();
            currentMessageId = null;
            runState?.NextMessage();
            if (onMessageFlushed is not null)
                await onMessageFlushed();
        }
    }

    private async ValueTask<object?> Callback(AIAgent agent,
        FunctionInvocationContext functionContext,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        functionContext.Arguments.Context ??= new Dictionary<object, object?>();
        functionContext.Arguments.Context["CallId"] = functionContext.CallContent.CallId;

        var t = await next(functionContext, ct);
        if (functionContext.FunctionCallIndex + 1 >= functionContext.FunctionCount)
        {
            functionContext.Terminate = true;
            _shouldContinue = true;
        }
        return t;
    }
}

public class AgentClientResponse
{
    public required List<ChatResponse> Responses { get; init; }
    public required bool ShouldContinue { get; init; }
    public List<AgentClientTask> QueuedTasks { get; init; } = [];
}

public class AgentClientTask
{
    public required string CallId { get; init; }
    public required TaskType Type { get; init; }

    // TODO: how to handle these parameters? a dictionary?
    public string? ChildDescription { get; init; }
    public string? ChildPrompt { get; init; }
    public long? AgentId { get; set; }

    public enum TaskType
    {
        ChildSession,
    }
}