namespace SharpClaw.API.Web;

public interface ISearchService
{
    Task<SearchResults> SearchAsync(string query, int numResults = 10, string country = "US", bool safeSearch = true);
}

public class SearchResults
{
    public string Query { get; set; } = string.Empty;
    public List<SearchResult> Results { get; set; } = [];
    public int TotalResults { get; set; }
}

public class SearchResult
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Snippet { get; set; }
}
