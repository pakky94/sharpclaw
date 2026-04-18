namespace SharpClaw.API.Web;

public class SearchProviderConfiguration
{
    public string ActiveProvider { get; set; } = "Brave";
    public BraveSearchConfiguration Brave { get; set; } = new();
}
