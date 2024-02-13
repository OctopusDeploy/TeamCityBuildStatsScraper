using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;
using TeamCitySharp;
using TeamCitySharp.Locators;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityBuildArtifactScraper : BackgroundService
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;

        public TeamCityBuildArtifactScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityBuildArtifactScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromMinutes(5);

        protected override async Task Scrape(CancellationToken stoppingToken)
        {
            await Task.CompletedTask;
            
            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var teamCityClient = new TeamCityClient(teamCityUrl, true);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            // We need to use UTC because the TeamCitySharp library has a problem where timezones with + are converted to -
            // However, our TeamCity instance is able to deal with filtering dates provided in UTC appropriately.
            var locator = BuildLocator.WithDimensions(
                running: false,
                sinceDate: DateTime.UtcNow.AddMinutes(-180),
                maxResults: 1000);


            var recentBuilds = teamCityClient.Builds
                .GetFields("count,build(id,finishDate,startDate,buildTypeId,queuedDate,statistics(property,value,name))")
                .ByBuildLocator(locator)
                // Only give me the builds that actually contain these metrics, otherwise we skew the data with lots of zeroes
                .Where(b => b.Statistics.Property.Exists(p => p.Name.Contains("artifactsPublishing")))
                .Where(b => b.Statistics.Property.Exists(p => p.Name.Contains("ArtifactsSize")))
                .Where(b => b.Statistics.Property.Exists(p => p.Name.Contains("dependenciesResolving")))
                .Where(b => b.Statistics.Property.Exists(p => p.Name.Contains("artifactResolving:totalDownloaded")))
                .ToArray();

            var recentBuildStats = recentBuilds.Select(rb => new
                {
                    rb.BuildTypeId,
                    artifactPublishTime = long.Parse(rb.Statistics.Property.Single(p => p.Name.Contains("artifactsPublishing")).Value),
                    artifactPublishSize = long.Parse(rb.Statistics.Property.Single(p => p.Name == "ArtifactsSize").Value),
                    artifactPullTime = long.Parse(rb.Statistics.Property.Single(p => p.Name.Contains("dependenciesResolving")).Value),
                    artifactPullSize = long.Parse(rb.Statistics.Property.Single(p => p.Name.Contains("artifactResolving:totalDownloaded")).Value)
                })
                .GroupBy(b => b.BuildTypeId)
                .Select(b => new
                {
                    buildTypeId = b.Key,
                    meanArtifactPublishTime = b.Average(build => build.artifactPublishTime),
                    meanArtifactPublishSize = b.Average(build => build.artifactPublishSize),
                    meanArtifactPullTime = b.Average(build => build.artifactPullTime),
                    meanArtifactPullSize = b.Average(build => build.artifactPullSize)
                })
                .ToArray();

            var publishSizeGauge = metricFactory.CreateGauge("build_artifact_push_size", "Size of artifacts pushed by a build", "buildTypeId");
            var publishTimeGauge = metricFactory.CreateGauge("build_artifact_push_time", "Time in ms for artifacts to be pushed by a build", "buildTypeId");
            var pullSizeGauge = metricFactory.CreateGauge("build_artifact_pull_size", "Size of artifacts pulled into a build", "buildTypeId");
            var pullTimeGauge = metricFactory.CreateGauge("build_artifact_pull_time", "Time in ms for artifacts to be pulled into a build", "buildTypeId");

            foreach (var item in recentBuildStats)
            {
                pullSizeGauge.WithLabels(item.buildTypeId).Set(item.meanArtifactPullSize);
                pullTimeGauge.WithLabels(item.buildTypeId).Set(item.meanArtifactPullTime);
                publishSizeGauge.WithLabels(item.buildTypeId).Set(item.meanArtifactPublishSize);
                publishTimeGauge.WithLabels(item.buildTypeId).Set(item.meanArtifactPublishTime);
                Logger.Debug("Build Type {BuildTypeId}, Push Size {MeanArtifactPublishSize}, Push Time {MeanArtifactPublishTime}, Pull Size {MeanArtifactPullSize}, Pull Time {MeanArtifactPullTime}",
                    item.buildTypeId,
                    item.meanArtifactPublishSize,
                    item.meanArtifactPublishTime,
                    item.meanArtifactPullSize,
                    item.meanArtifactPullTime);
            }
        }

    }
}
