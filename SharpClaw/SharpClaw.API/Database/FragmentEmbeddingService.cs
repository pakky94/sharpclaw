using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SharpClaw.API.Agents;

namespace SharpClaw.API.Database;

public sealed class FragmentEmbeddingService(IOptions<LmStudioConfiguration> options)
{
    private readonly LmStudioConfiguration _configuration = options.Value;

    public async Task<float[]?> TryGenerateEmbedding(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var model = _configuration.EmbeddingModel?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var endpoint = _configuration.EmbeddingEndpoint?.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_configuration.EmbeddingTimeout),
        };

        if (!string.IsNullOrWhiteSpace(_configuration.EmbeddingApiKey))
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _configuration.EmbeddingApiKey);

        var baseUri = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
        var uri = new Uri(new Uri(baseUri, UriKind.Absolute),
            _configuration.EmbeddingProvider == EmbeddingProvider.LocalOllama
                ? "v1/embeddings"
                : "embeddings");

        var payload = JsonSerializer.Serialize(new
        {
            model,
            input,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var parsed = await JsonSerializer.DeserializeAsync<EmbeddingsResponse>(stream, cancellationToken: cancellationToken);
        var embedding = parsed?.Data?.FirstOrDefault()?.Embedding;
        return embedding is { Count: > 0 } ? embedding.ToArray() : null;
    }

    public sealed class EmbeddingsResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData>? Data { get; init; }
    }

    public sealed class EmbeddingData
    {
        [JsonPropertyName("embedding")]
        public List<float>? Embedding { get; init; }
    }

    public async Task LoadEmbeddingsModel(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_configuration.EmbeddingModel))
            return;

        if (_configuration.EmbeddingProvider == EmbeddingProvider.LocalOllama)
        {
            var model = _configuration.EmbeddingModel?.Trim();
            if (string.IsNullOrWhiteSpace(model))
                return;

            var endpoint = _configuration.EmbeddingEndpoint?.Trim();
            if (string.IsNullOrWhiteSpace(endpoint))
                return;

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(_configuration.EmbeddingTimeout),
            };

            if (!string.IsNullOrWhiteSpace(_configuration.EmbeddingApiKey))
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", _configuration.EmbeddingApiKey);

            var baseUri = endpoint.EndsWith("/", StringComparison.Ordinal) ? endpoint : endpoint + "/";
            var uri = new Uri(new Uri(baseUri, UriKind.Absolute), "api/tags");

            using var tagsResponse = await httpClient.GetAsync(uri, cancellationToken);
            tagsResponse.EnsureSuccessStatusCode();

            var tags = await tagsResponse.Content.ReadFromJsonAsync<OllamaTagsResponse>(cancellationToken: cancellationToken);

            if (tags?.Models.Any(m => m.Model == _configuration.EmbeddingModel) ?? false)
                return;

            uri = new Uri(new Uri(baseUri, UriKind.Absolute), "api/pull");
            using var response = await httpClient.PostAsync(uri, JsonContent.Create(new
            {
                model = _configuration.EmbeddingModel,
                stream = false,
            }), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}

internal class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<ModelResponse> Models { get; init; }

    internal class ModelResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; init; }
    }
}