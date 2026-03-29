using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SharpClaw.API.Agents;

public class AgentClient(ChatClient chatClient, AgentExecutionContext context)
{
    public async Task<List<ChatMessage>> GetResponse(List<ChatMessage> messages, List<AIFunction> tools,
        Func<ChatResponseUpdate, Task>? onUpdate = null)
    {
        var agent = chatClient.AsAIAgent(
            // instructions: "you are a helpful assistant, follow the .md files instructions and try to help the user.",
            tools: [..tools],
            services: new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider()
        );

        var response = new List<ChatMessage>();
        var messageUpdates = new List<ChatResponseUpdate>();
        string? currentMessageId = null;

        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           options: new ChatClientAgentRunOptions
                           {
                               ChatOptions = new ChatOptions
                               {
                                   Temperature = 0.1f,
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

            if (chatUpdate.FinishReason is not null)
                FlushMessageUpdates();
        }

        FlushMessageUpdates();

        return response;

        void FlushMessageUpdates()
        {
            var m = messageUpdates.ToChatResponse();

            if (messageUpdates.Count == 0)
                return;

            if (!messageUpdates.Any(u => u.Contents.Count > 0))
                return;

            response.AddMessages(messageUpdates);
            messageUpdates.Clear();
            currentMessageId = null;
        }
    }

    private static JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string> GetStreamingResponse(List<AIFunction> tools, Func<ChatResponseUpdate, Task>? onUpdate = null)
    {
        var agent = chatClient.AsAIAgent(
            instructions: "you are a helpful assistant, follow the .md files instructions and try to help the user.",
            tools: [..tools],
            services: new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider()
        );

        var sb = new StringBuilder();
        var messageUpdates = new List<ChatResponseUpdate>();
        string? currentMessageId = null;

        await foreach (var update in agent.RunStreamingAsync(
                           context.Messages,
                           options: new ChatClientAgentRunOptions
                           {
                               ChatOptions = new ChatOptions
                               {
                                   Temperature = 0.1f,
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

            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);

            if (onUpdate is not null)
                await onUpdate(chatUpdate);

            Console.WriteLine(JsonSerializer.Serialize(update, _jsonOptions));

            if (chatUpdate.FinishReason is not null)
                FlushMessageUpdates();
        }

        FlushMessageUpdates();

        return sb.ToString();

        void FlushMessageUpdates()
        {
            if (messageUpdates.Count == 0)
                return;

            context.Messages.AddMessages(messageUpdates);
            messageUpdates.Clear();
            currentMessageId = null;
        }
    }
}
