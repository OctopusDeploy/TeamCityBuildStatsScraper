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
using TeamCitySharp.DomainEntities;

namespace TeamCityBuildStatsScraper.Scrapers
{
    class TeamCityQueueWaitScraper : IHostedService, IDisposable
    {
        readonly IMetricFactory metricFactory;
        readonly IConfiguration configuration;
        readonly HashSet<string> seenBuildTypes = new();
        Timer timer;

        public TeamCityQueueWaitScraper(IMetricFactory metricFactory, IConfiguration configuration)
        {
            this.metricFactory = metricFactory;
            this.configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire off the Scraper starting *right now* and do it again every fifteen seconds
            timer = new Timer(ScrapeBuildStats, null, TimeSpan.Zero, TimeSpan.FromSeconds(15));

            return Task.CompletedTask;
        }

        void ScrapeBuildStats(object state)
        {
            var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
            var teamCityClient = new TeamCityClient(teamCityUrl, true);

            teamCityClient.ConnectWithAccessToken(teamCityToken);

            var scrapeDuration = new Stopwatch();
            scrapeDuration.Start();

            var queuedBuilds = GetFilteredQueuedBuilds(teamCityClient);

            var queueStats = GenerateQueueWaitStats(queuedBuilds);

            scrapeDuration.Stop();

            CreateOrUpdateMetrics(queueStats);
            LogActivity(queueStats, scrapeDuration.Elapsed);
        }

        void CreateOrUpdateMetrics(QueuedBuildStats[] queueStats)
        {
            /*
             * A Summary gives us a sliding-time-window view of our wait times. The library we use holds a small buffer and calculates a
             * sliding P50 / P90 / P99 value for all the observations. We're unlikely to need the 'sum' value, but it could be used in our
             * monitoring visualisations along with 'count' to work out a mean (as opposed to P50 which is median). However, as we observe
             * every ten seconds a value that is only growing, the sum values are a bit misleading. It could be the same build continuing to wait
             * ever-increasing amounts, which means that the _sum will increase exponentially as the build continues to wait.
             *
             * The P-values should be relatively meaningful, though. Given the 'spikiness' of our build queue waits, we should be able to alert on a
             * P-value from this and not get too many false positives. A brand new build that waits for 5 seconds won't outweigh another build of the
             * same type that's been waiting for 5 minutes, for example. In reverse, if 50 builds of the same type all wait for 5 seconds only, then
             * repeated observation of a build that is now waiting a long time will shift the P90 and P99 values relatively quickly. If we find that
             * this dynamic is happening too fast, the first thing to try should be to *increase* the scrape interval above in StartAsync() so that
             * frequent scraping of long-waiting builds doesn't skew the data higher as quickly.
             * 
             * The output on the server looks a bit like this:
             *
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.5"} 2874.183
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.9"} 2874.183
             * queued_build_wait_times_by_type{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal",quantile="0.99"} 2874.183
             * queued_build_wait_times_by_type_sum{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal"} 2874.183
             * queued_build_wait_times_by_type_count{buildType="OctopusDeploy_OctopusServer_Build_BuildPortal"} 1
             */

            var metrics = metricFactory
                .CreateSummary("queued_builds_wait_times_by_type", "How long each build type has been waiting to start", "buildTypeId");

            foreach (var queuedBuild in queueStats)
            {
                metrics.WithLabels(queuedBuild.BuildType).Observe(queuedBuild.TimeInQueue.TotalMilliseconds);
            }

            // In other scrapers we track previously-observed build types in order to reset their gauges. With a Summary, the Prometheus library
            // needs to retain a small history of previous observations in order to calculate the P-values. There is a library default of 10 minutes 
            // that will age out data so that memory usage doesn't grow without bound. This means that unlike the other scrapers, we don't need a
            // metrics.WithLabels(queuedBuild.BuildType).Reset() here.
        }

        void LogActivity(QueuedBuildStats[] queueStats, TimeSpan scrapeDuration)
        {
            var consoleString = new StringBuilder();

            consoleString.AppendLine($"Scrape complete at {DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)}");
            consoleString.AppendLine($"Completed scrape of queued build waiting time in {scrapeDuration.TotalMilliseconds} ms. Builds captured were:");
            consoleString.AppendLine("-------------------------------------------------------------------------------------------");
            consoleString.AppendLine("Build Type | Id | Wait Duration | Now | Queued Date-Time | Latest Dependency Finish Time");

            foreach (var queuedBuild in queueStats)
            {
                consoleString.AppendLine(
                    $"{queuedBuild.BuildType} | {queuedBuild.Id} | {queuedBuild.TimeInQueue.TotalMilliseconds} | {DateTime.UtcNow} | {queuedBuild.QueuedTime} | {queuedBuild.LastDependencyFinishTime}");
            }

            var currentBuildTypes = queueStats.Select(x => x.BuildType).Distinct().ToArray();
            seenBuildTypes.UnionWith(currentBuildTypes);
            var absentBuildTypes = seenBuildTypes.Except(currentBuildTypes);

            foreach (var item in absentBuildTypes)
            {
                consoleString.AppendLine($"{item} | not observed | <n/a>");
            }

            Console.WriteLine(consoleString.ToString());
        }

        static QueuedBuildStats[] GenerateQueueWaitStats(Build[] queuedBuilds)
        {
            var now = DateTime.UtcNow;

            return queuedBuilds
                .Select(qb =>
                {
                    // For builds where they have been waiting on another build, we only want to 'start the clock' from the final dependency finish time, if that's more recent.
                    var latestQueueDate = qb.SnapshotDependencies.Build.Select(b => b.FinishDate).Concat(new[] { qb.QueuedDate }).Max();

                    return new QueuedBuildStats
                    {
                        BuildType = qb.BuildTypeId,
                        Id = qb.Id,
                        QueuedTime = (qb.QueuedDate), // local development you may need to offset these values; TC seems to do some magic converting TZs
                        LastDependencyFinishTime = latestQueueDate,
                        TimeInQueue = now - latestQueueDate
                    };
                })
                .ToArray();
        }

        static Build[] GetFilteredQueuedBuilds(TeamCityClient teamCityClient)
        {
            return teamCityClient.BuildQueue
                .GetFields(
                    "count,build(id,waitReason,buildTypeId,queuedDate,snapshot-dependencies(count,build(state,status,finishDate)),artifact-dependencies(count,build(state,status,finishDate)))")
                .All()
                // exclude builds with no wait reason - these are the ones that are 'starting shortly'
                .Where(qb => qb.WaitReason != null)
                // exclude builds just waiting on other builds
                .Where(qb => !qb.WaitReason.Contains("Build dependencies have not been built yet"))
                // exclude builds where any artifact dependency is still building, unless there are none
                .Where(AllDependenciesComplete)
                .ToArray();
        }

        static bool AllDependenciesComplete(Build qb)
        {
            if (qb.SnapshotDependencies == null) return true;

            if (qb.SnapshotDependencies.Build == null)
                throw new InvalidOperationException(
                    $"Looks like we received a build with no list of dependent builds at all, despite it apparently having snapshot dependencies. BuildTypeId: {qb.BuildTypeId}, BuildId: {qb.Id}");

            return qb.SnapshotDependencies.Build.TrueForAll(b => b.State == "finished");
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

    class QueuedBuildStats
    {
        public string BuildType { get; set; }
        public string Id { get; set; }
        public DateTime QueuedTime { get; set; }
        public DateTime LastDependencyFinishTime { get; set; }
        public TimeSpan TimeInQueue { get; set; }
    }
}
