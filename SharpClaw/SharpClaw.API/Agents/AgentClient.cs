using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SharpClaw.API.Agents;

public class AgentClient(ChatClient chatClient, AgentExecutionContext context, IConfiguration configuration)
{
    public async Task<List<ChatResponse>> GetResponse(List<ChatMessage> messages, List<AIFunction> tools,
        AgentRunState? runState = null,
        Func<ChatResponseUpdate, Task>? onUpdate = null)
    {
        var services = new ServiceCollection()
            .Configure<LmStudioConfiguration>(configuration)
            .AddSingleton(context)
            .AddSingleton(configuration)
            .AddSingleton<Repository>()
            .AddSingleton<FragmentsRepository>()
            .AddSingleton<FragmentEmbeddingService>()
            .AddSingleton<WorkspaceRepository>()
            .AddSingleton<ApprovalService>();

        if (runState is not null)
            services.AddSingleton(runState);

        var agent = chatClient.AsAIAgent(
            tools: [..tools],
            services: services.BuildServiceProvider()
        );

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
                               }
                           }))
        {
            var chatUpdate = update.AsChatResponseUpdate();

            var hasNewMessageBoundary = messageUpdates.Count > 0 &&
                                        !string.IsNullOrEmpty(chatUpdate.MessageId) &&
                                        !string.IsNullOrEmpty(currentMessageId) &&
                                        !string.Equals(chatUpdate.MessageId, currentMessageId, StringComparison.Ordinal);

            if (hasNewMessageBoundary)
                FlushMessageUpdates();

            if (!string.IsNullOrEmpty(chatUpdate.MessageId))
                currentMessageId ??= chatUpdate.MessageId;

            messageUpdates.Add(chatUpdate);

            if (onUpdate is not null)
                await onUpdate(chatUpdate);

            // Console.WriteLine(JsonSerializer.Serialize(update, _jsonOptions));

            // TODO: maybe split only when MessageId changes?
            // if (chatUpdate.FinishReason is not null)
                // FlushMessageUpdates();
        }

        FlushMessageUpdates();

        return response;

        void FlushMessageUpdates()
        {
            if (messageUpdates.Count == 0)
                return;

            if (!messageUpdates.Any(u => u.Contents.Count > 0))
                return;

            var m = messageUpdates.ToChatResponse();
            response.Add(m);
            messageUpdates.Clear();
            currentMessageId = null;
        }
    }
}
