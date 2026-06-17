# Reading the deployed instance logs

The automations run in production as a **Home Assistant OS add-on** (NetDaemon). Because you must
**not** run the app locally to observe it (its scheduler commands live devices — see
[`../CLAUDE.md`](../CLAUDE.md)), reading the deployed add-on's logs over Home Assistant's REST API is
the safe, **read-only** way to verify behaviour in production.

## Endpoint

The Supervisor exposes add-on logs through HA's REST proxy:

- **One-shot, bounded tail** (best for spot checks): `GET /api/hassio/addons/<slug>/logs` with a
  `Range` header.
- **Live follow** (stream): `GET /api/hassio/addons/<slug>/logs/follow?lines=<N>`.

Add-on slug: **`c6a2317c_netdaemon6`**. The `c6a2317c_` prefix is the local add-on repository hash and
can change if the add-on is reinstalled — confirm the current slug from the add-on page URL in HA
(`/config/app/<slug>/logs`).

## Auth

A Bearer token for an **admin** user. The long-lived token already in
[`../src/appsettings.json`](../src/appsettings.json) (`HomeAssistant:Token`) works — the same one used
for `/api/states` and `/api/history`. **Never paste real tokens into committed files.**

Extract it (the file is UTF-8 **with BOM**, hence `utf-8-sig`):

```bash
TOKEN=$(python -c "import json;print(json.load(open('src/appsettings.json',encoding='utf-8-sig'))['HomeAssistant']['Token'])")
```

## Recipes

Last N journal entries (recommended):

```bash
curl -s -H "Authorization: Bearer $TOKEN" -H "Range: entries=:-400:400" \
  "http://homeassistant.local:8123/api/hassio/addons/c6a2317c_netdaemon6/logs"
```

`Range: entries=:-N:N` asks the systemd journal gateway for the last `N` entries; without it the
endpoint can return a very large amount.

Live follow (cap the stream so `curl` doesn't hang):

```bash
curl -s --max-time 10 -H "Authorization: Bearer $TOKEN" \
  "http://homeassistant.local:8123/api/hassio/addons/c6a2317c_netdaemon6/logs/follow?lines=100"
```

The output carries ANSI colour codes. Strip them and filter to the battery app:

```bash
curl -s -H "Authorization: Bearer $TOKEN" -H "Range: entries=:-400:400" \
  "http://homeassistant.local:8123/api/hassio/addons/c6a2317c_netdaemon6/logs" \
  | sed -E 's/\x1b\[[0-9;]*m//g' \
  | grep -aE "HassModel\.Battery"
```

## What to look for (battery app)

| Log line | Meaning |
|---|---|
| `Usage backfill: … buckets populated` | startup backfill of the usage estimate from HA history (expect close to `288/288` buckets) |
| `Per-segment usage estimate profile …` | the learned per-time-of-day usage curve (kWh per 5 min) |
| `Segment usage estimate for HH:MM …` | the per-segment estimate used to drain the current segment (vs the flat fallback) |
| `Usage live update: closed N-segment window …` | a live 5-min-loop usage sample being recorded (or discarded) |
| `Initialised segments with 865 …` | the 72 h trajectory was built; `Hourly usage estimate` is the flat runway figure |
| `Succesfully set battery mode to …` | the work-mode command actually sent to the inverter |
| `[ERR]` / `Error …` | failures worth investigating |

## Notes

- **Read-only and safe to run anytime** — these calls never command devices (unlike launching the app).
- **PowerShell** equivalent: `Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $token"; Range = "entries=:-400:400" }`.
- `401` → token wrong/expired or user isn't an admin. `404` → the add-on slug changed (see above).
