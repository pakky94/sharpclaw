namespace SharpClaw.API.Agents;

public class LmStudioConfiguration
{
    public const string SectionName = "LmStudio";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";
}
