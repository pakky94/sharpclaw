using System.ClientModel;
using Microsoft.Extensions.Options;
using OpenAI;

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
                NetworkTimeout = TimeSpan.FromMinutes(10),
            });

        // var chatClient = client.GetChatClient("openai/gpt-oss-20b");
        // var chatClient = client.GetChatClient("qwen/qwen3.5-35b-a3b");
        // var chatClient = client.GetChatClient("qwen/qwen3.5-9b");
        // var chatClient = client.GetChatClient("zai-org/glm-4.7-flash");
        var chatClient = client.GetChatClient("qwen3.5-13b-glm-4.7-flash-grande-deep-thinking-i1");

        return new AgentClient(chatClient, context);
    }
}