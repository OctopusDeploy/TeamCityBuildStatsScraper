using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;
using Serilog;

namespace TeamCityBuildStatsScraper.Scrapers;

//from https://docs.microsoft.com/en-us/dotnet/architecture/microservices/multi-container-microservice-net-applications/background-tasks-with-ihostedservice
public abstract class BackgroundService : IHostedService, IDisposable
{
    protected readonly ILogger Logger;
    Task executingTask;
    readonly CancellationTokenSource stoppingCts = new();
    readonly AsyncRetryPolicy retryPolicy;

    protected BackgroundService(ILogger logger)
    {
        Logger = logger;
        retryPolicy = GetRetryPolicy();
    }
    
    protected abstract Task Scrape(CancellationToken stoppingToken);

    async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() => Logger.Information("{TaskName} background task is starting", GetType().Name));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await retryPolicy.ExecuteAsync(async _ =>
                {
                    Logger.Debug("Beginning scrape for {TaskName}", GetType().Name);
                    var stopwatch = new Stopwatch();
                    stopwatch.Start();
                    await Scrape(stoppingToken);
                    stopwatch.Stop();
                    Logger.Information("Scrape complete at {CompletionTime}, taking {Duration} ms", DateTime.UtcNow.ToString(CultureInfo.InvariantCulture),  stopwatch.ElapsedMilliseconds);
                }, stoppingToken);
                await Task.Delay(DelayBetweenScrapes, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                Logger.Information("Task cancellation detected in {TaskName} background task - shutting down", GetType().Name);
            }
        }

        Logger.Information("{TaskName} background task is stopping", GetType().Name);
    }

    protected abstract TimeSpan DelayBetweenScrapes { get; }

    public virtual Task StartAsync(CancellationToken cancellationToken)
    {
        // Store the task we're executing
        executingTask = ExecuteAsync(stoppingCts.Token);

        // If the task is completed then return it,
        // this will bubble cancellation and failure to the caller
        if (executingTask.IsCompleted)
            return executingTask;

        // Otherwise it's running
        return Task.CompletedTask;
    }

    public virtual async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop called without start
        if (executingTask == null)
            return;

        try
        {
            // Signal cancellation to the executing method
            stoppingCts.Cancel();
        }
        finally
        {
            // Wait until the task completes or the stop token triggers
            await Task.WhenAny(executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
        }
    }

    public virtual void Dispose()
    {
        stoppingCts.Cancel();
        GC.SuppressFinalize(this);
    }
    
    static AsyncRetryPolicy GetRetryPolicy()
    {
        var policy = Policy
            .Handle<Exception>()
            .WaitAndRetryForeverAsync(_ => TimeSpan.FromSeconds(30),
                (exception, attempt, waitTime) =>
                    Log.Error(exception,
                        "Exception {Exception} while trying to scrape TeamCity stats. Waiting {WaitTime} before next retry. Retry attempt {Attempt}",
                        exception.Message,
                        waitTime,
                        attempt)
            );
        return policy;
    }
}
