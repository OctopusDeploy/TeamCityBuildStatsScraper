using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;
using TeamCitySharp;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityCompatibleAgentsScraper : BackgroundService
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly HashSet<(string buildTypeId, string buildId, string queuedDateTime)> seenBuildsNoAgents = new();

        public TeamCityCompatibleAgentsScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityCompatibleAgentsScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromSeconds(60);

        protected override async Task Scrape(CancellationToken stoppingToken)
        {
            await Task.CompletedTask;

            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var useSSL = configuration.GetValue<bool>("USE_SSL");
            var teamCityClient = new TeamCityClient(teamCityUrl, useSSL);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate,compatibleAgents(count,agent(id)))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                .ToArray();

            // Track builds with no compatible agents
            var buildsNoCompatibleAgents = queuedBuilds
                .Where(qb => qb.WaitReason == "There are no idle compatible agents which can run this build")
                .Where(qb => qb.CompatibleAgents?.Agent == null || qb.CompatibleAgents.Agent.Count == 0)
                .ToArray();

            var noAgentsGauge = metricFactory.CreateGauge("queued_builds_no_compatible_agents", "Queued builds waiting with no compatible agents available", "buildTypeId", "buildId", "queuedDateTime");

            foreach (var build in buildsNoCompatibleAgents)
            {
                noAgentsGauge.WithLabels(build.BuildTypeId, build.Id, build.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ")).Set(1);
                Logger.Debug("Build Type {BuildTypeId}, build ID {BuildId} has no compatible agents, queued at {QueuedDateTime}", build.BuildTypeId, build.Id, build.QueuedDate);
            }

            var currentBuildsNoAgents = buildsNoCompatibleAgents.Select(b => (b.BuildTypeId, b.Id, b.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ"))).ToArray();
            seenBuildsNoAgents.UnionWith(currentBuildsNoAgents);
            var absentBuildsNoAgents = seenBuildsNoAgents.Except(currentBuildsNoAgents);

            foreach (var (buildTypeId, buildId, queuedDateTime) in absentBuildsNoAgents)
            {
                noAgentsGauge.RemoveLabelled(buildTypeId, buildId, queuedDateTime);
                Logger.Debug("Build Type {BuildTypeId}, build ID {BuildId} queued at {QueuedDateTime} no longer waiting with no compatible agents", buildTypeId, buildId, queuedDateTime);
            }
        }
    }
}
