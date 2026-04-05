using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace SharpClaw.Backend.Tests.Infrastructure;

public sealed class SharpClawAppFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string?> _previousEnvironmentValues = new(StringComparer.Ordinal);
    public TestLlmServer? LlmServer;
    private DistributedApplication? _app;

    public BackendApiClient Api { get; private set; } = null!;
    public string ConnectionString { get; private set; } = string.Empty;

    public const string CollectionName = "sharpclaw-backend";

    public async Task InitializeAsync()
    {
        LlmServer = TestLlmServer.Start();
        ConfigureEnvironment(LlmServer.Endpoint);

        var appBuilder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SharpClaw_AppHost>();
        _app = await appBuilder.BuildAsync();
        await _app.StartAsync();

        ConnectionString = await _app.GetConnectionStringAsync("sharpclaw")
                           ?? throw new InvalidOperationException("Connection string 'sharpclaw' was not resolved.");
        var endpoint = _app.GetEndpoint("sharpclaw-api", "https");
        var httpClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = endpoint,
        };
        Api = new BackendApiClient(httpClient);

        await Api.ResetConversationStateAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();

        if (LlmServer is not null)
            await LlmServer.DisposeAsync();

        RestoreEnvironment();
    }

    public async Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        await Api.ResetConversationStateAsync(ConnectionString, cancellationToken);
        LlmServer?.ResetMocks();
    }

    private void ConfigureEnvironment(Uri endpoint)
    {
        SetEnvironment("DOTNET_ENVIRONMENT", "Development");
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
        SetEnvironment("SHARPCLAW_VOLATILE_DATABASE", "true");
        SetEnvironment("LmStudio__Endpoint", endpoint.ToString().TrimEnd('/'));
        SetEnvironment("LmStudio__ApiKey", "test-api-key");
        SetEnvironment("LmStudio__EmbeddingEndpoint", endpoint.ToString().TrimEnd('/'));
        SetEnvironment("LmStudio__EmbeddingApiKey", "test-api-key");
        SetEnvironment("LmStudio__EmbeddingModel", "test-embedding-model");
    }

    private void SetEnvironment(string key, string value)
    {
        if (!_previousEnvironmentValues.ContainsKey(key))
            _previousEnvironmentValues[key] = Environment.GetEnvironmentVariable(key);

        Environment.SetEnvironmentVariable(key, value);
    }

    private void RestoreEnvironment()
    {
        foreach (var (key, value) in _previousEnvironmentValues)
            Environment.SetEnvironmentVariable(key, value);
    }
}
