using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;
using TeamCitySharp;
using TeamCitySharp.Locators;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityBuildScraper : BackgroundService
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly HashSet<string> seenBuildTypes = new();

        public TeamCityBuildScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityBuildScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        protected override void Scrape()
        {
            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var teamCityClient = new TeamCityClient(teamCityUrl, true);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var hungBuilds = teamCityClient.Builds
                .GetFields("count,build(id,probablyHanging,buildTypeId)")
                .ByBuildLocator(BuildLocator.WithDimensions(running: true), new List<string> { "hanging:true" })
                .ToArray();

            var gauge = metricFactory.CreateGauge("probably_hanging_builds", "Count of running builds that appear to be hung", "buildTypeId");

            foreach (var build in hungBuilds.GroupBy(x => x.BuildTypeId))
            {
                gauge.WithLabels(build.Key).Set(build.Count());
                Logger.Debug("Build Type {BuildTypeId}, Count {Count}", build.Key, build.Count());
            }

            var currentBuildTypes = hungBuilds.Select(x => x.BuildTypeId).Distinct().ToArray();
            seenBuildTypes.UnionWith(currentBuildTypes);
            var absentBuildTypes = seenBuildTypes.Except(currentBuildTypes);

            foreach (var item in absentBuildTypes)
            {
                // if not present, reset the gauge to zero
                gauge.WithLabels(item).Reset();
                Logger.Debug("Build Type {BuildTypeId}, Count {Count}", item, 0);
            }
        }
    }
}
