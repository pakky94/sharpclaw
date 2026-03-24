using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace SharpClaw.API.Agents;

public class AgentClient(ChatClient chatClient, AgentExecutionContext context)
{
    public async Task<string> GetResponse(List<AIFunction> tools)
    {
        var agent = chatClient.AsAIAgent(
            instructions: "",
            tools: [..tools],
            services: new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider()
        );

        var response = await agent.RunAsync(context.Messages,
            options: new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    Temperature = 0.1f,
                }
            });

        context.Messages.AddRange(response.Messages);

        return response.Text;
    }

    private static JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string> GetStreamingResponse(List<AIFunction> tools)
    {
        var agent = chatClient.AsAIAgent(
            instructions: "you are a helpful assistant, follow the .md files instructions and try to help the user.",
            tools: [..tools],
            services: new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider()
        );

        var sb = new StringBuilder();

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
            if (!string.IsNullOrEmpty(update.Text))
                sb.Append(update.Text);

            Console.WriteLine(JsonSerializer.Serialize(update, _jsonOptions));
        }

        return sb.ToString();
    }
}