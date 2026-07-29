using LocalLm.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalLm.Mcp;

public sealed class RecommendedModelSyncService(
    ModelManagementTasks tasks,
    ILogger<RecommendedModelSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await tasks.SyncRecommendedAsync(stoppingToken);
            if (result.InstalledModels.Count > 0)
            {
                logger.LogInformation(
                    "Queued and completed recommended model installation: {Models}",
                    string.Join(", ", result.InstalledModels));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Recommended model synchronization failed; MCP startup continues.");
        }
    }
}
