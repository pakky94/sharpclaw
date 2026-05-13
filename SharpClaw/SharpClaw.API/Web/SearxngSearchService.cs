using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SharpClaw.API.Web;

public class SearxngSearchService : ISearchService
{
    private readonly HttpClient _httpClient;
    private readonly SearxngSearchConfiguration _config;
    private readonly ILogger<SearxngSearchService> _logger;

    public SearxngSearchService(
        HttpClient httpClient,
        IOptions<SearxngSearchConfiguration> config,
        ILogger<SearxngSearchService> logger)
    {
        _httpClient = httpClient;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<SearchResults> SearchAsync(string query, int numResults = 10, string country = "US", bool safeSearch = true)
    {
        var effectiveNumResults = Math.Min(numResults, _config.MaxResults);
        var language = ResolveLanguage(country);
        var safeSearchValue = safeSearch ? "1" : "0";

        var queryParameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query)}",
            "format=json",
            $"safesearch={safeSearchValue}",
            $"language={Uri.EscapeDataString(language)}",
        };

        if (!string.IsNullOrWhiteSpace(_config.Engines))
        {
            queryParameters.Add($"engines={Uri.EscapeDataString(_config.Engines)}");
        }

        var requestUri = $"/search?{string.Join("&", queryParameters)}";

        try
        {
            var response = await _httpClient.GetAsync(requestUri);
            response.EnsureSuccessStatusCode();

            var searchResponse = await response.Content.ReadFromJsonAsync<SearxngSearchResponse>();

            if (searchResponse is null)
            {
                _logger.LogWarning("SearXNG returned null response for query: {Query}", query);
                return new SearchResults { Query = query, Results = [], TotalResults = 0 };
            }

            return new SearchResults
            {
                Query = query,
                TotalResults = searchResponse.NumberOfResults ?? searchResponse.Results?.Count ?? 0,
                Results = searchResponse.Results?
                    .Take(effectiveNumResults)
                    .Select(r => new SearchResult
                    {
                        Title = r.Title ?? string.Empty,
                        Url = r.Url ?? string.Empty,
                        Description = r.Content ?? string.Empty,
                        Snippet = r.Content,
                    })
                    .ToList() ?? [],
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "SearXNG API request failed for query: {Query}", query);
            return new SearchResults
            {
                Query = query,
                Results = [],
                TotalResults = 0
            };
        }
    }

    private string ResolveLanguage(string country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return _config.DefaultLanguage;
        }

        var trimmed = country.Trim();
        if (trimmed.Contains('-') || trimmed.Contains('_'))
        {
            return trimmed.Replace('_', '-');
        }

        if (trimmed.Length == 2)
        {
            return $"en-{trimmed.ToUpperInvariant()}";
        }

        return _config.DefaultLanguage;
    }
}

public class SearxngSearchResponse
{
    [JsonPropertyName("number_of_results")]
    public int? NumberOfResults { get; set; }

    [JsonPropertyName("results")]
    public List<SearxngSearchResult>? Results { get; set; }
}

public class SearxngSearchResult
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}
