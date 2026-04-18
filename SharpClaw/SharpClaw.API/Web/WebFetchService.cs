using System.Net.Http;
using System.Text;
using AngleSharp.Html.Parser;

namespace SharpClaw.API.Web;

public interface IWebFetchService
{
    Task<FetchResult> FetchAsync(string url);
}

public class WebFetchService : IWebFetchService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebFetchService> _logger;
    private readonly HtmlParser _htmlParser;

    public WebFetchService(
        HttpClient httpClient,
        ILogger<WebFetchService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _htmlParser = new HtmlParser();
    }

    public async Task<FetchResult> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || 
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new FetchResult 
            { 
                Success = false, 
                Error = "Invalid URL. Must be an absolute HTTP or HTTPS URL." 
            };
        }

        try
        {
            var response = await _httpClient.GetAsync(uri);
            
            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {response.ReasonPhrase}"
                };
            }

            var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (contentType is not null && contentType != "text/html" && !contentType.Contains("html"))
            {
                return new FetchResult
                {
                    Success = false,
                    Error = $"Unsupported content type: {contentType}. Only HTML is supported."
                };
            }

            var html = await response.Content.ReadAsStringAsync();
            var textContent = ExtractTextContent(html);

            return new FetchResult
            {
                Success = true,
                Url = url,
                Title = ExtractTitle(html),
                Content = textContent,
                ContentLength = textContent?.Length ?? 0,
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch URL: {Url}", url);
            return new FetchResult
            {
                Success = false,
                Error = $"Request failed: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching URL: {Url}", url);
            return new FetchResult
            {
                Success = false,
                Error = $"Unexpected error: {ex.Message}"
            };
        }
    }

    private string? ExtractTitle(string html)
    {
        try
        {
            var document = _htmlParser.ParseDocument(html);
            return document.QuerySelector("title")?.TextContent?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private string? ExtractTextContent(string html)
    {
        try
        {
            var document = _htmlParser.ParseDocument(html);

            // Remove script and style elements
            foreach (var element in document.QuerySelectorAll("script, style, noscript, iframe, svg"))
            {
                element.Remove();
            }

            // Remove common non-content elements
            foreach (var element in document.QuerySelectorAll("header, footer, nav, aside, [class*='nav'], [class*='menu'], [class*='sidebar']"))
            {
                element.Remove();
            }

            // Try to find main content area
            var mainContent = document.QuerySelector("main") 
                ?? document.QuerySelector("article") 
                ?? document.QuerySelector("[role='main']")
                ?? document.Body;

            if (mainContent is null)
                return null;

            var text = mainContent.TextContent;

            // Normalize whitespace
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            // Remove very short lines (likely navigation or noise)
            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 20)
                .ToList();

            text = string.Join("\n", lines);

            // Limit size
            const int maxLength = 50000;
            if (text.Length > maxLength)
            {
                text = text[..maxLength] + "\n\n[Content truncated]";
            }

            return text.Trim();
        }
        catch
        {
            return null;
        }
    }
}

public class FetchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Content { get; set; }
    public int ContentLength { get; set; }
}
