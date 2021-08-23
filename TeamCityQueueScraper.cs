using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus.Client;
using TeamCitySharp;

namespace TeamCityBuildStatsScraper
{
    internal class TeamCityQueueScraper: IHostedService, IDisposable
    {
        private readonly IHost _host;
        private readonly IConfiguration _configuration;
        private Timer _timer;

        public TeamCityQueueScraper(IHost host, IConfiguration configuration)
        {
            _host = host;
            _configuration = configuration;
        }
        
        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Fire off the Scraper starting *right now* and do it again every minute
            _timer = new Timer(ScrapeQueueStats, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));

            return Task.CompletedTask;
        }

        private void ScrapeQueueStats(object state)
        {
            var metricFactory = _host.Services.GetRequiredService<IMetricFactory>();
            var teamCityToken = _configuration.GetValue<string>("TEAMCITY_TOKEN");
            var teamCityUrl = _configuration.GetValue<string>("BUILD_SERVER_URL");
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
                .GroupBy(qb => qb.WaitReason)
                .Select(qb => new
                {
                    waitReason = qb.Key,
                    queuedBuildCount = qb.Count()
                })
                .ToArray();

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

            Console.WriteLine(consoleString.ToString());
        }
        
        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}