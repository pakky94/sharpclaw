using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace SharpClaw.API.Web;

public class BraveSearchService : ISearchService
{
    private readonly HttpClient _httpClient;
    private readonly BraveSearchConfiguration _config;
    private readonly ILogger<BraveSearchService> _logger;

    public BraveSearchService(
        HttpClient httpClient,
        IOptions<BraveSearchConfiguration> config,
        ILogger<BraveSearchService> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<SearchResults> SearchAsync(string query, int numResults = 10, string country = "US", bool safeSearch = true)
    {
        var effectiveNumResults = Math.Min(numResults, _config.MaxResults);
        
        var requestUrl = $"{_config.BaseUrl}/web/search?q={Uri.EscapeDataString(query)}&count={effectiveNumResults}&country={country}&safe_search={GetSafeSearchValue(safeSearch)}";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Subscription-Token", _config.ApiKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var searchResponse = await response.Content.ReadFromJsonAsync<BraveSearchResponse>();

            if (searchResponse is null)
            {
                _logger.LogWarning("Brave Search returned null response for query: {Query}", query);
                return new SearchResults { Query = query, Results = [], TotalResults = 0 };
            }

            var results = new SearchResults
            {
                Query = query,
                TotalResults = searchResponse.Web?.Total ?? 0,
                Results = searchResponse.Web?.Results?
                    .Take(effectiveNumResults)
                    .Select(r => new SearchResult
                    {
                        Title = r.Title ?? string.Empty,
                        Url = r.Url ?? string.Empty,
                        Description = r.Description ?? string.Empty,
                        Snippet = r.Snippet,
                    })
                    .ToList() ?? [],
            };

            return results;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Brave Search API request failed for query: {Query}", query);
            return new SearchResults 
            { 
                Query = query, 
                Results = [], 
                TotalResults = 0 
            };
        }
    }

    private static string GetSafeSearchValue(bool safeSearch) => safeSearch ? "1" : "0";
}

public class BraveSearchResponse
{
    public BraveWebResponse? Web { get; set; }
}

public class BraveWebResponse
{
    public List<BraveSearchResult>? Results { get; set; }
    public int? Total { get; set; }
}

public class BraveSearchResult
{
    public string? Title { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public string? Snippet { get; set; }
}
