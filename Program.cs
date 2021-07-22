using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus.Client.Collectors;
using Prometheus.Client.DependencyInjection;
using Prometheus.Client.MetricServer;

namespace TeamCityBuildStatsScraper
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.AddEnvironmentVariables();
                })
                .ConfigureServices((_, services) =>
                {
                    services.AddMetricFactory();
                    services.AddSingleton<IMetricServer>(sp => new MetricServer(
                        new MetricServerOptions
                        {
                            CollectorRegistryInstance = sp.GetRequiredService<ICollectorRegistry>(),
                            Host = "0.0.0.0",
                            Port = 9090,
                            UseDefaultCollectors = false
                        }));
                    services.AddHostedService<TeamCityBuildArtifactScraper>();
                })
                .UseConsoleLifetime()
                .Build();

            var metricServer = host.Services.GetRequiredService<IMetricServer>();

            try
            {
                metricServer.Start();
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Host Terminated Unexpectedly");
            }
            finally
            {
                metricServer.Stop();
            }
        }
    }
}