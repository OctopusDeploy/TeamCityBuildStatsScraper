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
using TeamCitySharp.Locators;

namespace TeamCityBuildStatsScraper
{
    internal class TeamCityBuildScraper: IHostedService, IDisposable
    {
        private readonly IMetricFactory _metricFactory;
        private readonly IConfiguration _configuration;
        private Timer _timer;
        private readonly HashSet<string> seenBuildTypes = new();

        public TeamCityBuildScraper(IMetricFactory metricFactory, IConfiguration configuration)
        {
            _metricFactory = metricFactory;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire off the Scraper starting *right now* and do it again every minute
            _timer = new Timer(ScrapeBuildStats, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

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

            var hungBuilds = teamCityClient.Builds
                .GetFields("count,build(id,probablyHanging,buildTypeId)")
                .ByBuildLocator(BuildLocator.WithDimensions(running: true), new List<string> { "hanging:true" })
                .ToArray();

            stopwatch.Stop();

            var gauge = _metricFactory.CreateGauge("probably_hanging_builds", "Count of running builds that appear to be hung", "buildTypeId");

            var consoleString = new StringBuilder();

            consoleString.AppendLine($"Scrape complete at {DateTime.UtcNow.ToString(CultureInfo.InvariantCulture)}");
            consoleString.AppendLine($"Completed scrape of hanging builds in {stopwatch.ElapsedMilliseconds} ms. Gauges generated were:");
            consoleString.AppendLine("----------------------------------------------------------");
            consoleString.AppendLine("Build Type | Count");

            foreach (var build in hungBuilds.GroupBy(x => x.BuildTypeId))
            {
                gauge.WithLabels(build.Key).Set(build.Count());
                consoleString.AppendLine($"{build.Key} | {(build.Count())}");
            }

            var currentBuildTypes = hungBuilds.Select(x => x.BuildTypeId).Distinct();
            seenBuildTypes.UnionWith(currentBuildTypes);
            var absentBuildTypes = seenBuildTypes.Except(currentBuildTypes);

            foreach (var item in absentBuildTypes)
            {
                // if not present, reset the gauge to zero
                gauge.WithLabels(item).Reset();
                consoleString.AppendLine($"{item} | 0");
            }

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
