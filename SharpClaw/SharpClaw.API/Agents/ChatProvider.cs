using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace SharpClaw.API.Agents;

public class ChatProvider(IOptions<LmStudioConfiguration> configuration)
{
    public AgentClient GetClient(AgentExecutionContext context)
    {
        var config = configuration.Value;

        var client = new OpenAIClient(new ApiKeyCredential(config.ApiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(config.Endpoint),
            });

        // var chatClient = client.GetChatClient("openai/gpt-oss-20b");
        // var chatClient = client.GetChatClient("qwen/qwen3.5-35b-a3b");
        var chatClient = client.GetChatClient("zai-org/glm-4.7-flash");

        return new AgentClient(chatClient, context);
    }
}

public class AgentClient(ChatClient chatClient, AgentExecutionContext context)
{
    public async Task<string> GetResponse(List<Message> messages, List<AIFunction> tools)
    {
        var agent = chatClient.AsAIAgent(
            instructions: "",
            tools: [..tools],
            services: new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider()
        );

        var session = await agent.CreateSessionAsync();

        var response = await agent.RunAsync(messages
            .Select(m => new ChatMessage(m.Role switch
            {
                MessageRole.System => ChatRole.System,
                MessageRole.User => ChatRole.User,
                MessageRole.Assistant => ChatRole.Assistant,
                MessageRole.Tool => ChatRole.Tool,
                _ => throw new ArgumentOutOfRangeException(nameof(m.Role), m.Role, null)
            }, m.Content))
            .ToList(), session);

        return response.Text;
    }
}

public class AgentExecutionContext
{
    public required string DbConnectionString { get; set; }
    public required long AgentId { get; set; }
    // public List<ChatMessage> Messages { get; init; } = [];
}

public class Message
{
    public required MessageRole Role { get; set; }
    public required string Content { get; set; }
    public List<ToolCall> ToolCalls { get; init; } = [];
}

public class ToolCall
{
}

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public class LmStudioConfiguration
{
    public const string SectionName = "LmStudio";

    public string Endpoint { get; set; }
    public string ApiKey { get; set; }
}