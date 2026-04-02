namespace SharpClaw.API.Database;

public sealed class FragmentEmbeddingBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<FragmentEmbeddingBackgroundService> logger) : BackgroundService
{
    private const int BatchSize = 24;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var fragmentsRepository = scope.ServiceProvider.GetRequiredService<FragmentsRepository>();
                var embeddingService = scope.ServiceProvider.GetRequiredService<FragmentEmbeddingService>();

                var pending = await fragmentsRepository.GetPendingEmbeddings(BatchSize, stoppingToken);
                if (pending.Count == 0)
                {
                    await Task.Delay(IdleInterval, stoppingToken);
                    continue;
                }

                foreach (var fragment in pending)
                {
                    if (stoppingToken.IsCancellationRequested)
                        break;

                    var input = FragmentEmbeddingText.BuildInput(fragment.Name, fragment.Type, fragment.Content);
                    var embedding = await embeddingService.TryGenerateEmbedding(input, stoppingToken);
                    if (embedding is null || embedding.Length == 0)
                        continue;

                    await fragmentsRepository.TrySetFragmentEmbedding(
                        fragment.Id,
                        fragment.UpdatedAt,
                        embedding,
                        stoppingToken);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Fragment embedding background worker iteration failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }
}
