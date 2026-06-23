# Project documentation

This repository is a [NetDaemon 4](https://netdaemon.xyz) console application (.NET 10) that runs
a collection of Home Assistant automations written in C#. It connects to a Home Assistant instance,
reads entity state, and drives devices (battery inverter, air conditioner, lights, …).

> These docs currently cover the **battery** and **AC** apps. The other apps exist and may be
> documented later — add a file under `docs/apps/` and link it from the index below.

## How it runs

- A single executable project (`src/src.csproj`, `OutputType=Exe`, `net10.0`) hosts the NetDaemon
  runtime. `program.cs` builds the host; `DependencyInjection.cs` registers the API clients.
- Every class decorated with `[NetDaemonApp]` is discovered and started automatically. Apps
  typically schedule recurring work via `INetDaemonScheduler` and/or subscribe to entity changes.
- Strongly-typed access to Home Assistant entities comes from the generated
  `src/HomeAssistantGenerated.cs` (produced by the `nd-codegen` tool, which runs as a Debug-build
  MSBuild step and talks to the live HA instance).
- Per-app configuration (entity ids, thresholds) lives in a YAML file next to the app and is bound
  to a typed `*Config` class via `IAppConfig<T>`.

## Repository layout

| Path | Purpose |
|---|---|
| `src/program.cs`, `src/DependencyInjection.cs` | Host bootstrap + service registration |
| `src/appsettings.json` | HA connection + external API credentials |
| `src/apps/HassModel/<App>/` | One folder per automation app |
| `src/HomeAssistantGenerated.cs` | Generated typed HA entities |
| `test/` | xUnit tests (folders mirror `src/`) |

## Build, test, run

See [`../CLAUDE.md`](../CLAUDE.md). In short: build any config (`dotnet build`), test with
`dotnet test`, and **never run the app locally** — it commands live devices and conflicts with the
deployed instance. A Debug build's `nd-codegen` step connecting to live HA is expected and fine.

## Operations

- [deployed-logs.md](deployed-logs.md) — read the **deployed** add-on's logs (and raw entity state
  history) over the HA REST API (read-only; the safe way to verify production behaviour without
  running the app locally).
- [amber-api.md](amber-api.md) — query the **Amber pricing API** directly (read-only) to verify the
  prices the planner is reacting to: buy/sell channels, the feed-in sign convention, and the
  machine-learned "advanced" price band.

## Apps

| App | Folder | Docs | Summary |
|---|---|---|---|
| Battery control | `src/apps/HassModel/Battery/` | [battery-control.md](apps/battery-control.md) | Price-arbitrage + solar-aware battery charge/discharge using Amber Electric prices |
| AC control | `src/apps/HassModel/AC/` | [ac-control.md](apps/ac-control.md) | Per-zone Mitsubishi air-conditioner control (felt-temperature aware) |
| Alarm light | `src/apps/HassModel/AlarmLight/` | _not yet documented_ | Scheduled wake-up lighting |
| Light adjust | `src/apps/HassModel/LightAdjust/` | _not yet documented_ | Adaptive light brightness/temperature |
| Light on movement | `src/apps/HassModel/LightOnMovement/` | _not yet documented_ | Motion-triggered lighting |
| Light sync | `src/apps/HassModel/LightSync/` | _not yet documented_ | Sync light groups |
| Hello world | `src/apps/HassModel/HelloWorld/` | _not yet documented_ | Sample/reference app |
