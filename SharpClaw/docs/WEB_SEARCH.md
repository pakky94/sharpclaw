# Web Search Implementation

## Overview

Web search capabilities have been added to SharpClaw using Brave Search API. The implementation is designed to be extensible for future search providers.

## Tools

Two new tools are available:

### `web_search`

Search the web using Brave Search.

**Parameters:**
- `query` (required): The search query
- `num_results` (optional, default: 10): Number of results to return
- `country` (optional, default: "US"): Country code for localized results
- `safe_search` (optional, default: true): Enable safe search filtering

**Example Response:**
```json
{
  "success": true,
  "query": "weather today",
  "total_results": 1500000,
  "count": 10,
  "results": [
    {
      "title": "Weather Forecast",
      "url": "https://example.com/weather",
      "description": "Current weather conditions...",
      "snippet": "Today's forecast shows..."
    }
  ]
}
```

### `web_fetch`

Fetch and extract readable text content from a web page URL.

**Parameters:**
- `url` (required): The URL to fetch

**Example Response:**
```json
{
  "success": true,
  "url": "https://example.com/article",
  "title": "Article Title",
  "content_length": 5432,
  "content": "Full extracted text content..."
}
```

## Configuration

Add your Brave Search API key to `appsettings.json` or `appsettings.Development.json`:

```json
{
  "WebSearch": {
    "Brave": {
      "ApiKey": "your-api-key-here",
      "BaseUrl": "https://api.search.brave.com/res/v1",
      "MaxResults": 10,
      "Country": "US"
    }
  }
}
```

## Getting a Brave Search API Key

1. Visit https://brave.com/search/api/
2. Sign up for an account
3. Create a new API key
4. Copy the key to your configuration

## Architecture

The implementation follows the existing tool patterns in SharpClaw:

```
SharpClaw.API/
├── Web/
│   ├── BraveSearchConfiguration.cs    # Configuration class
│   ├── SearchProviderConfiguration.cs # Provider selection (future)
│   ├── ISearchService.cs              # Search interface
│   ├── BraveSearchService.cs          # Brave implementation
│   └── WebFetchService.cs             # HTML fetching & parsing
└── Agents/Tools/Web/
    └── WebTools.cs                    # Tool definitions
```

## Future Extensions

To add a new search provider:

1. Create a configuration class (e.g., `GoogleSearchConfiguration`)
2. Implement `ISearchService` interface
3. Register in `Program.cs`
4. Update `SearchProviderConfiguration` to support provider selection

## Notes

- Search results are limited to the configured `MaxResults` (default: 10)
- Web fetch extracts main content, removing navigation, ads, scripts, and styles
- Content is truncated to 50,000 characters if longer
- HTTP timeout is 30 seconds for both search and fetch operations
