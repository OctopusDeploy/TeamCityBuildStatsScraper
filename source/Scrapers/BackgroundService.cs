using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Retry;

namespace TeamCityBuildStatsScraper.Scrapers;

//from https://docs.microsoft.com/en-us/dotnet/architecture/microservices/multi-container-microservice-net-applications/background-tasks-with-ihostedservice
public abstract class BackgroundService : IHostedService, IDisposable
{
    Task executingTask;
    readonly CancellationTokenSource stoppingCts = new();
    readonly RetryPolicy retryPolicy;

    protected BackgroundService()
    {
        retryPolicy = GetRetryPolicy();
    }
    
    protected abstract void Scrape();

    async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stoppingToken.Register(() => Console.WriteLine($"{GetType().Name} background task is starting."));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                retryPolicy.Execute(_ => Scrape(), stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Task cancellation detected in {nameof(TeamCityQueueWaitScraper)} background task - shutting down.");
            }
        }

        Console.WriteLine($"{nameof(TeamCityQueueWaitScraper)} background task is stopping.");
    }
    
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
    
    static RetryPolicy GetRetryPolicy()
    {
        const int retryCount = 10;
        var policy = Policy
            .Handle<Exception>()
            .WaitAndRetryForever(retryNumber => TimeSpan.FromSeconds(30),
                (exception, attempt, waitTime) =>
                    Console.Write($"Exception {exception.Message} while trying to scrape TeamCity stats. Waiting {0} before next retry. Retry attempt {1} of {2} attempts",
                        waitTime,
                        attempt,
                        retryCount)
            );
        return policy;
    }
}
