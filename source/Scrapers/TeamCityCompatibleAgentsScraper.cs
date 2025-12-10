using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
        readonly HttpClient httpClient = new();

        public TeamCityCompatibleAgentsScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityCompatibleAgentsScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromSeconds(60);

        async Task<QueuedWaitReasonsResponse> GetQueuedWaitReasons(string teamCityUrl, string teamCityToken, string buildId, bool useSSL)
        {
            var protocol = useSSL ? "https" : "http";
            var url = $"{protocol}://{teamCityUrl}/app/rest/buildQueue/id:{buildId}?fields=queuedWaitReasons(property(name,value))";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Authorization", $"Bearer {teamCityToken}");
            request.Headers.Add("Accept", "application/json");

            var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<QueuedWaitReasonsResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        protected override async Task Scrape(CancellationToken stoppingToken)
        {
            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var useSSL = configuration.GetValue<bool>("USE_SSL");
            var teamCityClient = new TeamCityClient(teamCityUrl, useSSL);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            // only look at builds that have been queued for 30 minutes
            var thirtyMinutesAgo = DateTime.UtcNow.AddMinutes(-30);
            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate,compatibleAgents(count,agent(id)))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                .Where(qb => qb.QueuedDate <= thirtyMinutesAgo)
                .ToArray();

            var noAgentsGauge = metricFactory.CreateGauge("queued_builds_no_compatible_agents", "Queued builds waiting with no compatible agents available", "buildTypeId", "buildId", "queuedDateTime");

            var buildsNoCompatibleAgents = new List<(string buildTypeId, string buildId, string queuedDateTime)>();

            foreach (var build in queuedBuilds)
            {
                // Fetch queuedWaitReasons from TeamCity API
                QueuedWaitReasonsResponse waitReasonsResponse = null;
                try
                {
                    waitReasonsResponse = await GetQueuedWaitReasons(teamCityUrl, teamCityToken, build.Id, useSSL);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to fetch queuedWaitReasons for build {BuildId}", build.Id);
                }

                // Check if this build has the "no compatible agents" wait reason
                var noAgentsWaitReason = waitReasonsResponse?.QueuedWaitReasons?.Property
                    ?.FirstOrDefault(p => p.Name == "There are no idle compatible agents which can run this build");

                if (noAgentsWaitReason != null && !string.IsNullOrEmpty(noAgentsWaitReason.Value))
                {
                    // Parse the wait time in milliseconds and convert to minutes
                    if (long.TryParse(noAgentsWaitReason.Value, out var milliseconds))
                    {
                        var waitTimeMinutes = Math.Round(milliseconds / (60.0 * 1000.0));

                        // Only track builds that have been waiting for more than 30 minutes
                        if (waitTimeMinutes > 30)
                        {
                            buildsNoCompatibleAgents.Add((build.BuildTypeId, build.Id, build.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ")));

                            noAgentsGauge.WithLabels(build.BuildTypeId, build.Id, build.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ")).Set(1);
                            Logger.Information("ALERT: Build Type {BuildTypeId}, build ID {BuildId} has been waiting with no compatible agents for {WaitTimeMinutes} minutes (threshold exceeded)",
                                build.BuildTypeId, build.Id, waitTimeMinutes);
                        }
                    }
                }
            }

            var currentBuildsNoAgents = buildsNoCompatibleAgents.ToArray();
            var absentBuildsNoAgents = seenBuildsNoAgents.Except(currentBuildsNoAgents).ToArray();

            Logger.Debug("DEBUG: currentBuildsNoAgents count: {Count}, values: {@Builds}",
                currentBuildsNoAgents.Length, currentBuildsNoAgents);
            Logger.Debug("DEBUG: absentBuildsNoAgents count: {Count}, values: {@Builds}",
                absentBuildsNoAgents.Length, absentBuildsNoAgents);
            Logger.Debug("DEBUG: seenBuildsNoAgents count before update: {Count}, values: {@Builds}",
                seenBuildsNoAgents.Count, seenBuildsNoAgents);

            foreach (var (buildTypeId, buildId, queuedDateTime) in absentBuildsNoAgents)
            {
                noAgentsGauge.RemoveLabelled(buildTypeId, buildId, queuedDateTime);
                Logger.Information("RESOLVED: Build Type {BuildTypeId}, build ID {BuildId} queued at {QueuedDateTime} no longer waiting with no compatible agents", buildTypeId, buildId, queuedDateTime);
                seenBuildsNoAgents.Remove((buildTypeId, buildId, queuedDateTime));
            }

            seenBuildsNoAgents.UnionWith(currentBuildsNoAgents);

            Logger.Debug("DEBUG: seenBuildsNoAgents count after update: {Count}, values: {@Builds}",
                seenBuildsNoAgents.Count, seenBuildsNoAgents);
        }
    }

    class QueuedWaitReasonsResponse
    {
        public QueuedWaitReasons QueuedWaitReasons { get; set; }
    }

    class QueuedWaitReasons
    {
        public List<WaitReasonProperty> Property { get; set; }
    }

    class WaitReasonProperty
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }
}
