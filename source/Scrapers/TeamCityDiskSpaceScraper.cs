#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;

namespace TeamCityBuildStatsScraper.Scrapers
{
    record StorageStatistics(long TotalCapacity, long UsedCapacity)
    {
        public long AvailableCapacity => TotalCapacity - UsedCapacity;
    }

    class TeamCityDiskSpaceScraper : BackgroundService
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly ILogger logger;

        public TeamCityDiskSpaceScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityDiskSpaceScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
            this.logger = logger;
        }

        protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromMinutes(5);

        protected override async Task Scrape(CancellationToken cancellationToken)
        {
            var stats = await RetrieveFileStorageMetrics(cancellationToken);

            var totalCapacityGauge = metricFactory.CreateGauge("build_storage_total_capacity", "Total capacity of the file share");
            var usedCapacityGauge = metricFactory.CreateGauge("build_storage_used_capacity", "Used capacity of the file share");
            var availableCapacityGauge = metricFactory.CreateGauge("build_storage_available_capacity", "Available capacity on the file share");

            totalCapacityGauge.Set(stats?.TotalCapacity ?? 0);
            usedCapacityGauge.Set(stats?.UsedCapacity ?? 0);
            availableCapacityGauge.Set(stats?.AvailableCapacity ?? 0);
            
            Logger.Debug("TeamCity Disk Space - Total Capacity {TotalCapacity}, Used Capacity {UsedCapacity}, Available Capacity {AvailableCapacity}",
                stats?.TotalCapacity,
                stats?.UsedCapacity,
                stats?.AvailableCapacity);
        }

        async Task<StorageStatistics?> RetrieveFileStorageMetrics(CancellationToken cancellationToken)
        {
            logger.Verbose("Retrieving Azure file storage metrics");

            try
            {
                var resourceGroupName = configuration.GetValue<string>("AZURE_FILE_SHARE_RESOURCE_GROUP_NAME");
                var storageAccountName = configuration.GetValue<string>("AZURE_FILE_SHARE_STORAGE_ACCOUNT_NAME");
                var subscriptionId = configuration.GetValue<string>("AZURE_FILE_SHARE_SUBSCRIPTION_ID");
                var shareName = configuration.GetValue<string>("AZURE_FILE_SHARE_STORAGE_SHARE_NAME");

                var client = new ArmClient(new DefaultAzureCredential());
                var subscription = await client.GetSubscriptions().GetAsync(subscriptionId, cancellationToken);
                var resourceGroups = subscription.Value.GetResourceGroups();
                ResourceGroupResource resourceGroup = await resourceGroups.GetAsync(resourceGroupName, cancellationToken);
                StorageAccountResource storageAccount = await resourceGroup.GetStorageAccountAsync(storageAccountName, cancellationToken: cancellationToken);

                var fileService = storageAccount.GetFileService();
                FileShareResource fileShare = await fileService.GetFileShareAsync(shareName, expand:"stats", cancellationToken: cancellationToken);
                var shareProperties = fileShare.Data;
                var totalCapacityInBytes = shareProperties.ShareQuota.GetValueOrDefault() * 1024L * 1024L * 1024L;
                var shareUsageInBytes = shareProperties.ShareUsageBytes.GetValueOrDefault();

                return new StorageStatistics(totalCapacityInBytes, shareUsageInBytes);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Failed to get Azure file storage metrics");
            }
            return null;
        }
    }

}
