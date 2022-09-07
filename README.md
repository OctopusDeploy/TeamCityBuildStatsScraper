# TeamCity Build Stats Scraper

This app scrapes a TeamCity instance for some aggregated statistics and exposes them in Prometheus format on port 9090 at `<hostname>:9090/metrics`.

## Configuration

Two environment variables need to be set:

- `BUILD_SERVER_URL` - The URL (or IP address) of your TeamCity instance, without `http(s)://` or trailing `/` - e.g. `buildserver.example.com` and not `https://buildserver.example.com/`
- `TEAMCITY_TOKEN` - An [authentication token](https://www.jetbrains.com/help/teamcity/managing-your-user-account.html#Managing+Access+Tokens) for your TeamCity instance, with enough access to read build information.

Optionally, you can also set

- `SEQ_URL` - The URL (with http/https prefix) to a seq server to log messages to
- `SEQ_API_KEY` - If your seq server requires an api key, you can specify it here. This also allows you to modify the logging level from seq. 

## How to run

### Console app

- Install the .NET 5 SDK
- Clone this repo
- `dotnet publish -c Release` from the root directory
- `cd` to `/bin/net5.0/Release/publish` and run `dotnet TeamCityBuildStatsScraper.dll`

### Docker container

- `docker build` the Dockerfile
- `docker run -p 9090:9090` and provide your environment variables via `-e`

## Metrics collected

### Build Artifact Movements

In a rolling three-hour window, the app collects the below [gauges](https://prometheus.io/docs/concepts/metric_types/#gauge) every five minutes for each build type that ran at least once in the window.

- `build_artifact_push_size` - mean size (in bytes) of the build artifacts pushed out from the agent
- `build_artifact_pull_size` - mean size (in bytes) of the build artifacts pulled into the agent
- `build_artifact_push_time` - mean time (in milliseconds) spent pushing build artifacts from the agent
- `build_artifact_pull_time` - mean time (in milliseconds) spent pulling build artifacts into the agent

Every minute, the app collects this:

- `probably_hanging_builds` - number of builds that are probably hanging, per build type
- `queued_builds_with_reason` - total number of builds queued per wait reason

Every 15 seconds, the app collects:
- `queued_builds_wait_times_by_type` - a [Summary](https://prometheus.io/docs/concepts/metric_types/#summary) with P50, P90, and P99 values for the time spent in the queue, per build type, over a ten-minute rolling window

Each gauge has a label called `buildTypeId` which matches the Build Configuration ID in TeamCity (not the numeric internal ID, but the human-readable one).
