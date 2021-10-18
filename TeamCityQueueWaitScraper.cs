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

namespace TeamCityBuildStatsScraper
{
    internal class TeamCityQueueWaitScraper : IHostedService, IDisposable
    {
        private readonly IMetricFactory _metricFactory;
        private readonly IConfiguration _configuration;
        private Timer _timer;
        private readonly HashSet<string> _seenBuildTypes = new();

        public TeamCityQueueWaitScraper(IMetricFactory metricFactory, IConfiguration configuration)
        {
            _metricFactory = metricFactory;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire off the Scraper starting *right now* and do it again every ten seconds
            _timer = new Timer(ScrapeBuildStats, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

            return Task.CompletedTask;
        }

        private void ScrapeBuildStats(object state)
        {
            var teamCityToken = _configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = _configuration.GetValue<string>("BUILD_SERVER_URL");
            var teamCityClient = new TeamCityClient(teamCityUrl, true);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var now = DateTime.UtcNow;
            
            var queuedBuilds = teamCityClient.BuildQueue
                .GetFields("count,build(id,waitReason,buildTypeId,queuedDate)")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                // exclude builds just waiting on other builds
                .Where(qb => !qb.WaitReason.Contains("Build dependencies have not been built yet"))
                .ToArray();

            stopwatch.Stop();

            var queueStats = queuedBuilds
                .Select(qb => new
                {
                    buildType = qb.BuildTypeId,
                    id = qb.Id,
                    now = now,
                    queueTime = (qb.QueuedDate), // local development you may need to offset these values; TC seems to do some magic converting TZs
                    timeInQueue = now - (qb.QueuedDate)
                })
                .ToArray();

            stopwatch.Stop();

            /*
             * A Summary gives us a sliding-time-window view of our wait times. The library we use holds a small buffer and calculates a
             * sliding P50 / P90 / P99 value for all the observations. We're unlikely to need the 'sum' value, but it could be used in our
             * monitoring visualisations along with 'count' to work out a mean (as opposed to P50 which is median). However, as we observe
             * every ten seconds a value that is only growing, the sum values are a bit misleading. It could be the same build continuing to wait
             * ever-increasing amounts, which means that the _sum will increase exponentially as the build continues to wait.
             *
             * The P-values should be relatively meaningful, though. Given the 'spikiness' of our build queue waits, we should be able to alert on a
             * P-value from this and not get too many false positives. A brand new build that waits for 5000ms won't outweigh another build of the
             * same type that's been waiting for 5 minutes, for example. In reverse, if 50 builds of the same type all wait for 5000ms only, then
             * repeated observation of a build that is now waiting a long time will shift the P90 and P99 values relatively quickly. If we find that
             * this dynamic is happening too fast, the first thing to try should be to *reduce* the scrape interval above in StartAsync() so that
             * frequent scraping of long-waiting builds doesn't skew the data higher as quickly.
             * 
             * The output on the server looks a bit like this:
             *
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.5"} 2874.183
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.9"} 2874.183
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.99"} 2874.183
             * queued_build_wait_times_by_type_sum{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal"} 2874.183
             * queued_build_wait_times_by_type_count{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal"} 1             * 
             */
            
            var metrics = _metricFactory
                .CreateSummary("queued_builds_wait_times_by_type", "How long each build type has been waiting to start", "buildTypeId");

            var consoleString = new StringBuilder();

            consoleString.AppendLine($"Scrape complete at {DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)}");
            consoleString.AppendLine($"Completed scrape of queued build waiting time in {stopwatch.ElapsedMilliseconds} ms. Builds captured were:");
            consoleString.AppendLine("-------------------------------------------------------------------------------------------");
            consoleString.AppendLine("Build Type | Id | Wait Duration | Now | Queued Date-Time");

            foreach (var queuedBuild in queueStats)
            {
                metrics.WithLabels(queuedBuild.buildType).Observe(queuedBuild.timeInQueue.TotalMilliseconds);
                consoleString.AppendLine($"{queuedBuild.buildType} | {queuedBuild.id} | {queuedBuild.timeInQueue.TotalMilliseconds} | {now} | {queuedBuild.queueTime}");
            }
            
            var currentBuildTypes = queueStats.Select(x => x.buildType).Distinct();
            _seenBuildTypes.UnionWith(currentBuildTypes);
            var absentBuildTypes = _seenBuildTypes.Except(currentBuildTypes);

            foreach (var item in absentBuildTypes)
            {
                // Summaries require historic data; Prometheus.net retains ~10 mins of data so we don't need to clear these out like we would in a gauge.
                consoleString.AppendLine($"{item} | not observed | <n/a>");
            }

            // consoleString.AppendLine($"Current summary buffer size: {metrics}");
            
            Console.WriteLine(consoleString.ToString());
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("Shutting down...");

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}