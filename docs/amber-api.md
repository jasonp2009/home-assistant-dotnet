# Querying the Amber API directly

The battery app prices every segment from [Amber Electric](https://app.amber.com.au)'s public
pricing API. When you want to **verify what the planner is reacting to** — an upcoming price spike, a
sell leg's earning, whether the machine-learned "advanced" estimate has firmed up yet — you can call
the same endpoint the app calls, with the same credentials, read-only. This is the price-side
companion to [`deployed-logs.md`](deployed-logs.md) (which reads the app's own log of its decisions).

The app's client is [`AmberClient`](../src/apps/HassModel/Battery/Clients/AmberClient/AmberClient.cs);
the field mapping is in
[`EnergySegmentExtensions.ApplyPrice`](../src/apps/HassModel/Battery/Extensions/EnergySegmentExtensions.cs)
and [`BaseIntervalExtensions`](../src/apps/HassModel/Battery/Clients/AmberClient/Extensions/BaseIntervalExtensions.cs).
This doc describes the wire format so you can read it without the app.

## Endpoint

```
GET https://api.amber.com.au/v1/sites/{SiteId}/prices/current?next={N}&resolution=5
```

- `next={N}` — number of forecast intervals to return after the current one (the app asks for 2048).
- `resolution=5` — request 5-minute intervals. **Note:** Amber only serves 5-minute granularity for
  the near term; intervals further out come back at 30-minute `duration` regardless (see *Gotchas*).

## Auth

A Bearer token — the **Amber API key**, not the Home Assistant token. It and the site id live in
[`../src/appsettings.json`](../src/appsettings.json) under `AmberClientSettings`. The file is UTF-8
**with BOM**, hence `utf-8-sig`. **Never paste real keys into committed files.**

```bash
KEY=$(python -c "import json;print(json.load(open('src/appsettings.json',encoding='utf-8-sig'))['AmberClientSettings']['ApiKey'])")
SITE=$(python -c "import json;print(json.load(open('src/appsettings.json',encoding='utf-8-sig'))['AmberClientSettings']['SiteId'])")
```

## Recipe

Pull the prices and inspect a window. **Do the curl and the parse in a single shell invocation** —
write the JSON to a file and parse it in the same call (see *Gotchas* for why):

```bash
curl -s -H "Authorization: Bearer $KEY" \
  "https://api.amber.com.au/v1/sites/$SITE/prices/current?next=2048&resolution=5" -o _amber_tmp.json
python -c "
import json
d=json.load(open('_amber_tmp.json'))
for x in d:
    if x['channelType']!='feedIn': continue
    nem=x['nemTime']                       # interval END, +10:00 (AEST)
    if nem[:10]=='2026-06-22' and '17:00'<=nem[11:16]<='19:00':
        a=x.get('advancedPrice')           # ML band, or None beyond the ~24h horizon
        band=f\"low {a['low']:.0f} pred {a['predicted']:.0f} high {a['high']:.0f}\" if a else 'no advanced band'
        print(nem, 'perKwh', round(x['perKwh']), '|', band, '|', x['spikeStatus'])
"
rm -f _amber_tmp.json
```

## Field reference

Each element is one price interval for one channel.

| Field | Meaning |
|---|---|
| `channelType` | `general` and `controlledLoad` are **import/buy** channels; `feedIn` is the **export/sell** channel. The app maps General/ControlledLoad → `BuyPricePerKw`, FeedIn → `SellPricePerKw`. |
| `perKwh` | Price for the interval, in **cents per kWh**. See sign convention below. |
| `spotPerKwh` | Underlying wholesale spot price (c/kWh) — useful context, not what you're billed. |
| `advancedPrice` | `{ low, predicted, high }` — Amber's machine-learned forecast band (c/kWh). Present only within the advanced horizon (see below); `null`/absent otherwise. |
| `spikeStatus` | `spike` when Amber flags an extreme price, else `none`. |
| `type` | `CurrentInterval` (now), `ForecastInterval` (future), `ActualInterval` (settled past). |
| `estimate` | On `CurrentInterval`: `true` until the interval's price locks in. The app waits briefly for the current price to lock (`MaxPriceLockInWaitSecs`). |
| `nemTime` | Interval **end** time, in NEM time (`+10:00`, AEST). `startTime`/`endTime` are the same span in UTC. |
| `duration` | Interval length in minutes (5 or 30). |
| `renewables` | % renewable generation for the interval. |

## Interpreting the data

### Sign convention (the important one)

For the **feedIn** channel, a **negative `perKwh` means you get _paid_ that much to export**
(e.g. `perKwh: -2049` ≈ you earn **$20.49/kWh**). The app stores
`SellPricePerKw = -GetPrice()`, so a **positive `SellPricePerKw` is the earning** — that's why the
deployed log shows a sell leg as `Sell …@2049c` (a good thing) while the raw API shows `-2049`.

Import channels (`general`, `controlledLoad`) use the natural sign: positive `perKwh` is what you pay.

### `nemTime` is the interval END — expect a label offset vs the logs

`nemTime` marks the **end** of each interval, but the app labels each segment by its **start**
(`StartUtc.ToLocalTime()`). So an app leg logged at `17:30` corresponds to the Amber interval whose
`nemTime` is `18:00` (the 30-minute interval `17:30 → 18:00`). When cross-checking the
`Arbitrage legs this plan` log against the API, shift by the interval length — it's a labelling
difference, not a discrepancy.

### The advanced (ML) price has a rolling ~24h horizon

`advancedPrice` is Amber's ML estimate and is only published for roughly the **next ~24 hours**.
Intervals further out return **no advanced band** — just the base `perKwh` forecast. The planner's
`GetPrice()` is `advancedPrice.Predicted ?? perKwh`, so a far-future spike is currently priced off
the base forecast and only switches to the ML predicted/low/high blend once it crosses into the
horizon. If you check a spike that's >24h away, expect `advancedPrice` to be `null` and to fill in
later — re-pull within the day to see the ML estimate (and any revision).

## Gotchas (this shell environment)

- **Temp files may not persist between separate tool calls.** Run the `curl` and the `python` parse
  in **one** invocation (write the JSON, parse it, `rm` it, all together). A two-step
  *fetch-then-parse* across calls can fail with "No such file or directory".
- **Don't pipe `curl` into a `python` heredoc.** `python << 'EOF'` binds the heredoc to **stdin**, so
  `sys.stdin` is the script, not the piped JSON. Write curl output to a file and `json.load` the
  file instead.
- The response can be large (2048 intervals × 2–3 channels). Filter by `channelType` and a time
  window as in the recipe rather than dumping the whole array.

## Notes

- **Read-only and safe to run anytime** — pricing reads never command devices (unlike launching the app).
- All prices in `current` are **forecasts** until their interval settles (`type: ForecastInterval`,
  or `CurrentInterval` with `estimate: true`); figures can move before then.
- **PowerShell** equivalent for the call:
  `Invoke-RestMethod -Uri $url -Headers @{ Authorization = "Bearer $key" }`.
- `401` → key wrong/expired. `404` → wrong `SiteId`. `422` → bad `next`/`resolution` query.
