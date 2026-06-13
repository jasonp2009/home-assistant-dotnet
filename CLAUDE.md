# CLAUDE.md

Guidance for AI agents working in this repository.

## What this is

A [NetDaemon 4](https://netdaemon.xyz) console app (.NET 10, `net10.0`) that runs Home
Assistant automations written in C#. Each automation is a self-contained "app" under
`src/apps/HassModel/<App>/`, discovered automatically at startup. The most developed app is the
**battery price-arbitrage controller**.

Start here: [`docs/README.md`](docs/README.md) (project overview + app index) and
[`docs/apps/battery-control.md`](docs/apps/battery-control.md).

## Build / test / run

- **Build** any configuration: `dotnet build -c Release` (or Debug). A **Debug** build runs the
  `nd-codegen` MSBuild step, which connects to the live Home Assistant to regenerate
  `src/HomeAssistantGenerated.cs`. That is expected and fine. `HomeAssistantGenerated.cs` is
  committed, so a Release build compiles without codegen.
- **Test**: `dotnet test` — xUnit project in `test/`, whose folders mirror the `src/` layout.
- **Do NOT run the app** (`dotnet run`): its schedulers issue real commands to live devices
  (e.g. the battery inverter work mode) and will conflict with the deployed instance. Verify with
  builds + unit tests instead.
- **Secrets** (Amber/Forecast.Solar API keys, HA long-lived token) live in `src/appsettings.json`.
  Home Assistant state/history can be read for analysis via its REST API using that token.

## Layout

- `src/` — the app (`OutputType=Exe`). Entry point `program.cs`, DI in `DependencyInjection.cs`.
- `src/apps/HassModel/<App>/` — one folder per automation, usually `<App>.cs` (decorated with
  `[NetDaemonApp]`), an `<App>Config.cs`, and an `<App>.yaml` for configuration/entity wiring.
- `src/HomeAssistantGenerated.cs` — generated strongly-typed HA entity classes (via `nd-codegen`).
- `test/` — xUnit tests, folders mirror `src/`.
