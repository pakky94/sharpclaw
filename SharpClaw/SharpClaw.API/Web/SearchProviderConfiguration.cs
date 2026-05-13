namespace SharpClaw.API.Web;

public class SearchProviderConfiguration
{
    public string ActiveProvider { get; set; } = "Searxng";
    public BraveSearchConfiguration Brave { get; set; } = new();
    public SearxngSearchConfiguration Searxng { get; set; } = new();
}
