# TeamCity Build Stats Scraper

This app scrapes a TeamCity instance for some aggregated statistics and exposes them in Prometheus format on port 9090 at `<hostname>:9090/metrics`.

## Configuration

Two environment variables need to be set:

- `BUILD_SERVER_URL` - The URL (or IP address) of your TeamCity instance, without `http(s)://` or trailing `/` - e.g. `buildserver.example.com` and not `https://buildserver.example.com/`
- `TEAMCITY_TOKEN` - An [authentication token](https://www.jetbrains.com/help/teamcity/managing-your-user-account.html#Managing+Access+Tokens) for your TeamCity instance, with enough access to read build information.

Optionally:

- `DISABLE_SSL_FOR_TEAMCITY_SCRAPER` - if present (with any value), this will use HTTP and not HTTPS to access your instance.

##Metrics collected

### Build Artifact Movements

In a rolling three-hour window, the app collects the below [gauges](https://prometheus.io/docs/concepts/metric_types/#gauge) every five minutes for each build type that ran at least once in the window.

- `build_artifact_push_size` - mean size (in bytes) of the build artifacts pushed out from the agent
- `build_artifact_pull_size` - mean size (in bytes) of the build artifacts pulled into the agent
- `build_artifact_push_time` - mean time (in milliseconds) spent pushing build artifacts from the agent
- `build_artifact_pull_time` - mean time (in milliseconds) spent pulling build artifacts into the agent

Each gauge has a label called `buildTypeId` which matches the Build Configuration ID in TeamCity (not the numeric internal ID, but the human-readable one).