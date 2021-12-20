using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Prometheus.Client;
using TeamCitySharp;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityQueueLengthScraper : IHostedService, IDisposable
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly HashSet<string> waitReasonList = new();
        Timer timer;

        public TeamCityQueueLengthScraper(IMetricFactory metricFactory, IConfiguration configuration)
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire off the Scraper starting *right now* and do it again every minute
            timer = new Timer(ScrapeQueueStats, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            return Task.CompletedTask;
        }

        void ScrapeQueueStats(object state)
        {
            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var teamCityClient = new TeamCityClient(teamCityUrl, true);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate,statistics(property,value,name))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                .ToArray();

            stopwatch.Stop();

            var queueStats = queuedBuilds
                .GroupBy(qb => Sanitize(qb.WaitReason))
                .Select(qb => new
                {
                    waitReason = qb.Key,
                    queuedBuildCount = qb.Count()
                })
                .ToArray();

            var currentWaitReasons = queueStats
                .Select(qs => qs.waitReason)
                .ToHashSet();

            // update wait reason list with any new reasons
            waitReasonList.UnionWith(currentWaitReasons);

            var waitReasonsGauge = metricFactory.CreateGauge("queued_builds_with_reason", "Count of builds in the queue for each queue reason", "waitReason");

            var consoleString = new StringBuilder();

            consoleString.AppendLine($"Scrape complete at {DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)}");
            consoleString.AppendLine($"Completed scrape of TeamCity in {stopwatch.ElapsedMilliseconds} ms. Gauges generated were:");
            consoleString.AppendLine("-------------------");
            consoleString.AppendLine("Wait Reason | Count");

            foreach (var item in queueStats)
            {
                waitReasonsGauge.WithLabels(item.waitReason).Set(item.queuedBuildCount);
                consoleString.AppendLine($"{item.waitReason} | {item.queuedBuildCount}");
            }

            var absentWaitReasons = waitReasonList.Except(currentWaitReasons);

            foreach (var item in absentWaitReasons)
            {
                // if not present, reset the gauge to zero
                waitReasonsGauge.WithLabels(item).Reset();
                consoleString.AppendLine($"{item} | 0");
            }

            Console.WriteLine(consoleString.ToString());
        }

        string Sanitize(string waitReason)
        {
            return waitReason.StartsWith("Build is waiting for the following resource to become available")
                ? "Build is waiting for a shared resource"
                : waitReason;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Shutting down...");

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            timer?.Dispose();
        }
    }
}
