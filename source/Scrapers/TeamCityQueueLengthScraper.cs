using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;
using TeamCitySharp;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityQueueLengthScraper : BackgroundService
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly HashSet<(string buildTypeId, string waitReason)> waitReasonList = new();

        public TeamCityQueueLengthScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
            : base(logger.ForContext("Scraper", nameof(TeamCityQueueLengthScraper)))
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }
        protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromSeconds(15);

        protected override async Task Scrape(CancellationToken stoppingToken)
        {
            await Task.CompletedTask;

            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var useSSL = configuration.GetValue<bool>("USE_SSL");
            var teamCityClient = new TeamCityClient(teamCityUrl, useSSL);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate,statistics(property,value,name))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                .ToArray();

            var queueStats = queuedBuilds
                .GroupBy(qb => new { buildTypeId = qb.BuildTypeId, waitReason = Sanitize(qb.WaitReason) })
                .Select(qb => new
                {
                    waitReason = qb.Key.waitReason,
                    buildTypeId = qb.Key.buildTypeId,
                    queuedBuildCount = qb.Count()
                })
                .ToArray();

            var currentWaitReasons = queueStats
                .Select(qs => (qs.buildTypeId, qs.waitReason ))
                .ToHashSet();

            // update wait reason list with any new reasons
            waitReasonList.UnionWith(currentWaitReasons);

            var waitReasonsGauge = metricFactory.CreateGauge("queued_builds_with_reason", "Count of builds in the queue for each queue reason", "buildTypeId", "waitReason");

            foreach (var item in queueStats)
            {
                waitReasonsGauge.WithLabels(item.buildTypeId, item.waitReason).Set(item.queuedBuildCount);
                Logger.Debug("Build Type {BuildTypeId}, Wait Reason {WaitReason}, Count {Count}", item.buildTypeId, item.waitReason, item.queuedBuildCount);
            }

            var absentWaitReasons = waitReasonList.Except(currentWaitReasons);

            foreach (var item in absentWaitReasons)
            {
                // if not present, reset the gauge to zero
                waitReasonsGauge.WithLabels(item.buildTypeId, item.waitReason).Reset();
                Logger.Debug("Build Type {BuildTypeId}, Wait Reason {WaitReason}, Count {Count}", item.buildTypeId, item.waitReason, 0);
            }
        }

        string Sanitize(string waitReason)
        {
            return waitReason.StartsWith("Build is waiting for the following resource to become available")
                ? "Build is waiting for a shared resource"
                : waitReason;
        }
    }
}
