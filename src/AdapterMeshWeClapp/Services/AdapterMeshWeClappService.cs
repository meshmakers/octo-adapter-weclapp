using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;

/// <summary>
/// Startup/shutdown lifecycle of the WeClapp mesh adapter: registers the configured
/// pipelines for the tenant and controls the event hub (per octo-adapter-demos template).
/// </summary>
internal class AdapterMeshWeClappService(
    ILogger<AdapterMeshWeClappService> logger,
    IPipelineRegistryService pipelineRegistryService,
    IEventHubControl eventHubControl) : IAdapterService
{
    public async Task<bool> StartupAsync(AdapterStartup adapterStartup, List<DeploymentUpdateErrorMessageDto> errorMessages,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Startup of WeClapp mesh adapter");
        try
        {
            var success = await pipelineRegistryService.RegisterPipelinesAsync(adapterStartup.TenantId,
                adapterStartup.Configuration.Pipelines, errorMessages);

            await eventHubControl.StartAsync(stoppingToken);

            return success;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while startup");
            throw;
        }
    }

    public async Task ShutdownAsync(AdapterShutdown adapterShutdown, CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Shutdown of WeClapp mesh adapter");
            await eventHubControl.StopAsync(stoppingToken);

            await pipelineRegistryService.UnregisterAllPipelinesAsync(adapterShutdown.TenantId);
            logger.LogInformation("WeClapp mesh adapter service stopped");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while shutdown");
            throw;
        }
    }
}
