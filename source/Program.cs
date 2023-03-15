using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus.Client.Collectors;
using Prometheus.Client.DependencyInjection;
using Prometheus.Client.MetricServer;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TeamCityBuildStatsScraper.Scrapers;

namespace TeamCityBuildStatsScraper
{
    class Program
    {
        public static async Task Main(string[] args)
        {
            LogUnhandledExceptions();
            LogUnobservedTaskExceptions();
            
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddEnvironmentVariables();

            var config = configBuilder.Build();
            var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug); //allows us to switch log levels by modifying the seq

            var appVersion = typeof(Program).Assembly?.GetName()?.Version?.ToString() ?? "<unknown>";
            var loggerConfiguration = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("ASPNET_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production")
                .Enrich.WithProperty("ApplicationSet", "TeamCityBuildStatsScraper")
                .Enrich.WithProperty("Application", "TeamCityBuildStatsScraper")
                .Enrich.WithProperty("Version", appVersion)
                .WriteTo.Console(LogEventLevel.Debug);
            
            if (!string.IsNullOrEmpty(config["SEQ_URL"]))
                loggerConfiguration.WriteTo.Seq(config["SEQ_URL"], apiKey: config["SEQ_API_KEY"], controlLevelSwitch: levelSwitch);
            
            Log.Logger = loggerConfiguration.CreateLogger();
            
            IMetricServer metricServer = null;
            try
            {
                var host = Host.CreateDefaultBuilder(args)
                    .UseSerilog(Log.Logger)
                    .ConfigureAppConfiguration((_, builder) =>
                    {
                        builder.AddEnvironmentVariables();
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
                        services.AddHostedService<TeamCityQueueLengthScraper>();
                        services.AddHostedService<TeamCityBuildScraper>();
                        services.AddHostedService<TeamCityBuildArtifactScraper>();
                        services.AddHostedService<TeamCityQueueWaitScraper>();
                        services.AddHostedService<TeamCityMutedTestsScraper>();
                    })
                    .UseConsoleLifetime()
                    .Build();

                Log.Information("TeamCityBuildStatsScraper v{Version} starting up", appVersion);
                metricServer = host.Services.GetRequiredService<IMetricServer>();
                metricServer.Start();
                await host.RunAsync();
                Log.Information("TeamCityBuildStatsScraper v{Version} stopping", appVersion);
            }
            catch (Exception ex)
            {
                Log.Logger?.Error(ex, "Unhandled exception");
            }
            finally
            {
                Log.CloseAndFlush();
                metricServer?.Stop();
            }
        }
        
        static void LogUnhandledExceptions()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                var exception = args.ExceptionObject as Exception;
                Log.Logger?.Fatal(exception, "Unhandled Exception: {Message}", exception?.Message);
            };
        }

        static void LogUnobservedTaskExceptions()
        {
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                if (Debugger.IsAttached) Debugger.Break();
                Log.Logger?.Fatal(args.Exception, "Unobserved Task Exception: {Message}", args.Exception.Message);
            };
        }
    }
}
