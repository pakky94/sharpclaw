namespace SharpClaw.API.Agents;

public class LmStudioConfiguration
{
    public const string SectionName = "LmStudio";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    // public string EmbeddingModel { get; set; } = "text-embedding-nomic-embed-text-v1.5";

    public string EmbeddingEndpoint { get; set; } = string.Empty;
    public string EmbeddingApiKey { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = "text-embedding-qwen3-embedding-0.6b";
    public int EmbeddingTimeout { get; set; } = 60;
    public EmbeddingProvider? EmbeddingProvider { get; set; } = null;
}

public enum EmbeddingProvider
{
    LocalOllama,
}
