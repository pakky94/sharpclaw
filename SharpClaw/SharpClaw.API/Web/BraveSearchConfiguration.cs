namespace SharpClaw.API.Web;

public class BraveSearchConfiguration
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.search.brave.com/res/v1";
    public int MaxResults { get; set; } = 10;
    public string Country { get; set; } = "US";
}
