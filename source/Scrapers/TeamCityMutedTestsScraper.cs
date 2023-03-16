using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Prometheus.Client;
using Serilog;
using TeamCitySharp;
using TeamCitySharp.Connection;
using TeamCitySharp.DomainEntities;
using TeamCitySharp.Locators;

namespace TeamCityBuildStatsScraper.Scrapers;

class TeamCityMutedTestsScraper : BackgroundService
{
    readonly IMetricFactory metricFactory;
    readonly IConfiguration configuration;

    public TeamCityMutedTestsScraper(IMetricFactory metricFactory, IConfiguration configuration, ILogger logger)
        : base(logger.ForContext("Scraper", nameof(TeamCityMutedTestsScraper)))
    {
        this.metricFactory = metricFactory;
        this.configuration = configuration;
    }
    protected override TimeSpan DelayBetweenScrapes => TimeSpan.FromMinutes(15);

    protected override void Scrape()
    {
        var teamCityToken = configuration.GetValue<string>("TEAMCITY_TOKEN");
        var teamCityUrl = configuration.GetValue<string>("BUILD_SERVER_URL");
        var teamCityClient = new TeamCityClient(teamCityUrl, true);

        teamCityClient.ConnectWithAccessToken(teamCityToken);
        
        //Unfortunately, we dont seem to be able to use the client for this call,
        //so for now, I'm reaching deep into it's guts to get the field I need. Ick.
        //We should raise a PR (though there hasn't been a release for nearly 12 months)
        var callerField = typeof(TeamCityClient).GetField("m_caller", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var caller = (ITeamCityCaller)callerField.GetValue(teamCityClient) ?? throw new ApplicationException("Unable to get m_caller field");

        const string projectId = "OctopusDeploy_OctopusServer";
        var hungBuilds = caller.Get<TestOccurrences>($"/tests?locator=currentlyMuted:true,affectedProject:{projectId}&fields=count");
        
        metricFactory
            .CreateGauge("muted_tests", "Count of muted tests", "projectId")
            .WithLabels(projectId)
            .Set(hungBuilds.Count);
        
        Logger.Debug("Project {ProjectId} has {Count} muted tests", projectId, hungBuilds.Count);
    }
}
