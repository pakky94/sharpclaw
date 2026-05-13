namespace SharpClaw.API.Web;

public class SearxngSearchConfiguration
{
    public string BaseUrl { get; set; } = "http://localhost:8080";
    public int MaxResults { get; set; } = 10;
    public string DefaultLanguage { get; set; } = "en-US";
    public string? Engines { get; set; }
}
