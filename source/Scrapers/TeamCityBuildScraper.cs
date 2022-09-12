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
        readonly HashSet<(string, string)> seenBuilds = new();

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
                .GetFields("count,build(id,buildTypeId)")
                .ByBuildLocator(BuildLocator.WithDimensions(running: true), new List<string> { "hanging:true", "composite:false" })
                .ToArray();

            var gauge = metricFactory.CreateGauge("probably_hanging_builds", "Running builds that appear to be hung", labelNames: new []{"buildTypeId", "buildId"});

            foreach (var build in hungBuilds)
            {
                gauge.WithLabels(build.BuildTypeId, build.Id).Set(1);
                Logger.Debug("Build Type {BuildTypeId}, build ID {BuildId} has hung", build.BuildTypeId, build.Id);
            }

            var currentBuilds = hungBuilds.Select(x => (x.BuildTypeId, x.Id)).ToArray();
            seenBuilds.UnionWith(currentBuilds);
            var absentBuilds = seenBuilds.Except(currentBuilds);

            foreach (var (buildType, buildId) in absentBuilds)
            {
                // if not present, reset the gauge to zero
                gauge.WithLabels(buildType, buildId).Reset();
                Logger.Debug("Build Type {BuildTypeId}, build ID {Id} no longer hung", buildType, buildId);
            }
        }
    }
}
