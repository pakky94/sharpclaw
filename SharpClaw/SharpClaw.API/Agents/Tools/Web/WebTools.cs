using Microsoft.Extensions.AI;
using SharpClaw.API.Web;

namespace SharpClaw.API.Agents.Tools.Web;

public static class WebTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(Search, "web_search", "Search the web using Brave Search. Returns search results with titles, URLs, and descriptions."),
        AIFunctionFactory.Create(Fetch, "web_fetch", "Fetch and extract readable text content from a web page URL."),
    ];

    private static async Task<object> Search(
        IServiceProvider serviceProvider,
        string query,
        int num_results = 10,
        string country = "US",
        bool safe_search = true)
    {
        var searchService = serviceProvider.GetRequiredService<ISearchService>();

        try
        {
            var results = await searchService.SearchAsync(query, num_results, country, safe_search);

            if (results.Results.Count == 0)
            {
                return new
                {
                    success = false,
                    query = results.Query,
                    total_results = 0,
                    results = new List<object>(),
                    message = "No results found."
                };
            }

            return new
            {
                success = true,
                query = results.Query,
                total_results = results.TotalResults,
                count = results.Results.Count,
                results = results.Results.Select(r => new
                {
                    title = r.Title,
                    url = r.Url,
                    description = r.Description,
                    snippet = r.Snippet,
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                query = query,
                error = $"Search failed: {ex.Message}"
            };
        }
    }

    private static async Task<object> Fetch(
        IServiceProvider serviceProvider,
        string url)
    {
        var fetchService = serviceProvider.GetRequiredService<IWebFetchService>();

        try
        {
            var result = await fetchService.FetchAsync(url);

            if (!result.Success)
            {
                return new
                {
                    success = false,
                    url = url,
                    error = result.Error
                };
            }

            return new
            {
                success = true,
                url = result.Url,
                title = result.Title,
                content_length = result.ContentLength,
                content = result.Content,
            };
        }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                url = url,
                error = $"Fetch failed: {ex.Message}"
            };
        }
    }
}
