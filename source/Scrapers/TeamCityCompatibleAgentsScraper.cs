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

            // only look at builds that have been queued for 30 minutes
            var thirtyMinutesAgo = DateTime.UtcNow.AddMinutes(-1);
            Logger.Debug("DEBUG: Current time: {Now}, Threshold time (30 min ago): {Threshold}", DateTime.UtcNow, thirtyMinutesAgo);

            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate,queuedWaitReasons(property(name,value)),compatibleAgents(count,agent(id)))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                .Where(qb => qb.QueuedDate <= thirtyMinutesAgo)
                .ToArray();

            Logger.Debug("DEBUG: Found {Count} queued builds with wait reasons", queuedBuilds.Length);

            var noAgentsGauge = metricFactory.CreateGauge("queued_builds_no_compatible_agents", "Queued builds waiting with no compatible agents available", "buildTypeId", "buildId", "queuedDateTime");

            var buildsNoCompatibleAgents = new List<(string buildTypeId, string buildId, string queuedDateTime)>();

            foreach (var build in queuedBuilds)
            {
                Logger.Debug("DEBUG: Processing build {BuildId} (Type: {BuildTypeId}), Wait Reason: {WaitReason}, Queued: {QueuedDate}",
                    build.Id, build.BuildTypeId, build.WaitReason, build.QueuedDate);

                // Log all properties in queuedWaitReasons if available
                if (build.QueuedWaitReasons?.Property != null)
                {
                    Logger.Debug("DEBUG: Build {BuildId} has {Count} wait reason properties", build.Id, build.QueuedWaitReasons.Property.Count);
                    foreach (var prop in build.QueuedWaitReasons.Property)
                    {
                        Logger.Debug("DEBUG: Build {BuildId} wait reason property: Name='{Name}', Value='{Value}'",
                            build.Id, prop.Name, prop.Value);
                    }
                }
                else
                {
                    Logger.Debug("DEBUG: Build {BuildId} has no QueuedWaitReasons.Property", build.Id);
                }

                // Check if this build has the "no compatible agents" wait reason
                var noAgentsWaitReason = build.QueuedWaitReasons?.Property
                    ?.FirstOrDefault(p => p.Name == "There are no idle compatible agents which can run this build");

                if (noAgentsWaitReason != null && !string.IsNullOrEmpty(noAgentsWaitReason.Value))
                {
                    Logger.Debug("DEBUG: Build {BuildId} found matching wait reason, value: {Value}", build.Id, noAgentsWaitReason.Value);

                    // Parse the wait time in milliseconds and convert to minutes
                    if (long.TryParse(noAgentsWaitReason.Value, out var milliseconds))
                    {
                        var waitTimeMinutes = Math.Round(milliseconds / (60.0 * 1000.0));

                        Logger.Debug("DEBUG: Build Type {BuildTypeId}, build ID {BuildId} has no compatible agents wait reason, waiting for {WaitTimeMinutes} minutes (parsed from {Milliseconds}ms)",
                            build.BuildTypeId, build.Id, waitTimeMinutes, milliseconds);

                        // Only track builds that have been waiting for more than 30 minutes
                        if (waitTimeMinutes > 3)
                        {
                            buildsNoCompatibleAgents.Add((build.BuildTypeId, build.Id, build.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ")));

                            noAgentsGauge.WithLabels(build.BuildTypeId, build.Id, build.QueuedDate.ToString("yyyy-MM-ddTHH:mm:ssZ")).Set(1);
                            Logger.Information("ALERT: Build Type {BuildTypeId}, build ID {BuildId} has been waiting with no compatible agents for {WaitTimeMinutes} minutes (threshold exceeded)",
                                build.BuildTypeId, build.Id, waitTimeMinutes);
                        }
                        else
                        {
                            Logger.Debug("DEBUG: Build {BuildId} wait time {WaitTimeMinutes} minutes is under 30 minute threshold", build.Id, waitTimeMinutes);
                        }
                    }
                    else
                    {
                        Logger.Debug("DEBUG: Build {BuildId} failed to parse wait time value: {Value}", build.Id, noAgentsWaitReason.Value);
                    }
                }
                else
                {
                    Logger.Debug("DEBUG: Build {BuildId} does not have 'no compatible agents' wait reason", build.Id);
                }
            }

            Logger.Debug("DEBUG: Total builds exceeding 30 minute threshold: {Count}", buildsNoCompatibleAgents.Count);

            var currentBuildsNoAgents = buildsNoCompatibleAgents.ToArray();
            seenBuildsNoAgents.UnionWith(currentBuildsNoAgents);
            var absentBuildsNoAgents = seenBuildsNoAgents.Except(currentBuildsNoAgents);

            Logger.Debug("DEBUG: Builds no longer in alert state: {Count}", absentBuildsNoAgents.Count());

            foreach (var (buildTypeId, buildId, queuedDateTime) in absentBuildsNoAgents)
            {
                noAgentsGauge.RemoveLabelled(buildTypeId, buildId, queuedDateTime);
                Logger.Information("RESOLVED: Build Type {BuildTypeId}, build ID {BuildId} queued at {QueuedDateTime} no longer waiting with no compatible agents", buildTypeId, buildId, queuedDateTime);
            }
        }
    }
}
