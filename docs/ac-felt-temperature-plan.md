# Felt-temperature tuning plan

**Goal (owner's words):** *the temperature in the home always feels the same and does not fluctuate
with the weather or humidity.* Symptom reported 2026-08-12: during the recent cold spell the felt
temperature "is not low enough" — the AC is not heating enough.

Investigation against live Home Assistant data, 2026-08-02 → 2026-08-12. App background:
[`apps/ac-control.md`](apps/ac-control.md).

> **Status: plan only, nothing implemented.** Revised after owner review — see
> [Owner constraints](#owner-constraints), which withdraw the original "humidity taper" proposal and
> re-frame the aggressiveness and SoC items around their intended purpose.

## TL;DR

The felt-temperature model is working and correctly signed, but it is a **±0.8 °C** correction
sitting on top of effects worth **2–5 °C**. `EnvCoefficient` is not the lever: on cold days the zone
is usually *already open* and the room still does not reach setpoint.

| # | Finding | Size | Owner intent? |
|---|---|---|---|
| 1 | Drive term commands ≤ return-air temperature 77 % of heating time | up to the whole deficit | **partly** — coil-residual harvesting is deliberate, but it fires mid-cycle, not at cycle end |
| 2 | SoC → profile shift widens the allowed felt deficit from 2.0 °C to 4.0–5.0 °C, 48.7 % of the time | 2–3 °C | **yes** — grid-price avoidance. Fix = widen the neutral band |
| 3 | Eco Plus + any negative shift disables the zone entirely | zone off | **yes** — intentional at low SoC |
| 4 | `HumidityCoefficient` 0.15 is ~1.5× stronger than PMV supports | 0.3 °C | no — genuine mis-scaling |
| 5 | Overnight coast-down (living zones run 0–7 % of overnight hours) | ~4 °C by morning | to confirm |
| 6 | No wind/draught term; `EnvCoefficient` 0.1 | 0.7–0.8 °C today | no |

## Owner constraints

Recorded so later work does not re-litigate them:

1. **Aggressiveness is an efficiency feature.** It exists to use up residual heat/cold in the coils
   before spending power, and to avoid heating the coils then shutting down soon after and wasting
   the residual. Changes must preserve that.
2. **The SoC profile shift is grid-price avoidance.** When the battery is low the AC should back off
   so the house can last until cheap grid power or solar returns. Owner's own suggestion: *widen the
   neutral band* so more of the SoC range runs at the neutral level.
3. **The `null` profile → zone disabled is intentional**, not a bug — entirely disabling zones during
   low SoC is the intended behaviour. (It is also intermittent: over a 7-day log window all six rooms
   appear, so rooms are disabled only while the shift is active.)

## Evidence

From `/api/history` (recorder keeps ~10 days), the deployed add-on log, and `/api/states`.

### The felt temperature is not invariant

Hourly indoor temperature binned by outdoor temperature, **controlled for time of day** — the naive
binning is confounded, because the coldest outdoor hours are all early morning. Lounge setpoint is a
fixed 23 °C and has not changed in 10 days.

```
EVENING 17:00–22:00 AEST (occupied)          MORNING 07:00–10:00 AEST
 outdoor   n  lounge  zoneOn%  socmod         outdoor   n  lounge  zoneOn%  socmod
  10-12    9   22.61     44     -0.44           4-6     2   18.35     50     -1.50
  12-14   20   22.55     20     -0.20           6-8    14   18.90     79     -1.36
  14-16   19   23.07      0      0.00           8-10    5   18.36      0     -3.00
  16-18    5   22.78      0      0.00          10-12    5   18.98     80     -1.60

MIDDAY 11:00–16:00 AEST                       OVERNIGHT 23:00–06:00 AEST
 outdoor   n  lounge  zoneOn%  socmod         outdoor   n  lounge  zoneOn%
  10-12    8   21.70     75      0.25           6-8    15   18.41      0
  12-14   12   21.72     83      0.00           8-10   35   19.12      3
  14-16   18   21.62     56     -0.17          10-12   14   20.55      0
  16-18   13   21.95     77      0.15          12-14   16   19.09      0
```

1. **Evenings are nearly fine** (22.6–23.1 °C air) — but in felt terms an 11 °C evening lands at
   **21.5 °C** and a 17 °C evening at **22.2 °C**. The model asks for 23.2 °C air on the cold evening
   and gets 22.6 °C: the correction is computed and then not delivered.
2. **Middays sit 1.3 °C below setpoint with the zone open 56–83 % of the time.** The thermostat has
   already said "heat this room" and it still does not get there. No change to the felt-temperature
   formula can affect this case.
3. **Mornings sit at 18.4–19.0 °C regardless of outdoor temperature**, after an overnight coast-down.

Over a cold day the felt temperature therefore swings roughly **18.5 → 22 °C**.

### Finding 1 — the drive term, and where it conflicts with its own purpose

`SetTemperature` commands `RoomTemp ± floor(aggressiveness)`, where `aggressiveness` is *minutes
since the room last moved in the helpful direction ÷ 5 − 1*, **averaged** across active rooms.
Negative values command a setpoint below return air, which idles the compressor while the fan keeps
running — that *is* the coil-residual harvest, and it is a sensible thing to do.

The problem is **when** it fires. The reset is triggered by any 0.1 °C favourable tick, and the room
sensors quantise at 0.1 °C, so during active heating the term is pinned near −1 regardless of how far
the room is from setpoint. Measured over 10 days, restricted to **heating with ≥1 zone open**:

```
floor(aggressiveness)   share      commanded unit setpoint
        -1              44.8 %     returnAir − 1 °C   (compressor idles)
         0              32.2 %     returnAir          (no heating demand)
        ≥1              23.0 %     returnAir + n
  → at or below return air: 77.0 %
```

Restricted further to *lounge zone open **and** lounge >1 °C below its setpoint*: **85.5 %**.

So the coast is happening in the **middle** of heating cycles, when the room is 1–2 °C short, rather
than at the **end** of a cycle when the zone is about to satisfy and the residual would otherwise be
stranded. Residual harvested mid-cycle is largely re-heated minutes later, so it buys little; the
saving the feature is designed for occurs at cycle end. This is a targeting problem, not a reason to
remove the feature.

> **Caveat:** there is **no AC submetering** in this HA instance (no per-circuit power sensor for the
> unit — checked all `power`/`energy` entities), so the efficiency effect of any change here cannot
> be measured directly, only inferred from runtime and indoor temperature response.

### Finding 2 — the SoC shift and the neutral band

Duty cycle over 10 days, and the effect on a `Standard` room with a 23 °C setpoint:

```
modifier  share    effective profile   allowed felt deficit
   +1      8.6 %   Boost               1.5 °C
    0     39.7 %   Standard            2.0 °C
   -1     44.7 %   Eco                 4.0 °C
   -2      6.0 %   Eco Plus            5.0 °C
   -5      0.9 %   (null) zone disabled
```

Mean modifier is **−1.4** in the 4–8 °C outdoor bins versus **0.0** at 14–18 °C, so the widening
arrives precisely when it is cold. Time-weighted SoC distribution shows why — the daily trough parks
in the 30–45 % band, which is squarely inside the current −1 range (25–50):

```
SoC band   duty        SoC band   duty
 25-30 %    6.0 %       45-50 %    6.6 %
 30-35 %    8.8 %       50-55 %    4.9 %
 35-40 %   14.5 %  ←modal   55-90 %  ~31 %
 40-45 %    8.8 %       90-100 %   8.6 %
```

Widening the neutral band downward (the owner's suggestion), holding −2 at 15–25 and −5 at 0–15 so
only the neutral/−1 boundary moves:

| Neutral band | Neutral duty | −1 duty (its band) |
|---|---|---|
| 50–90 *(today)* | 39.7 % | 44.7 % (25–50) |
| 40–90 | 55.1 % | 29.3 % (25–40) |
| 35–90 | 69.6 % | 14.8 % (25–35) |
| **30–90** | **78.5 %** | **6.0 % (25–30)** |
| 25–90 | 84.5 % | 0 % (band vanishes) |

**30–90 is the recommended edge**: the modal trough (35–40 %) sits comfortably *inside* neutral
rather than straddling the boundary, so the `Tolerance` hysteresis is not fighting the daily cycle.
This keeps full economy behaviour below 30 % and both the −2 and −5 rules exactly as they are.

*Trade-off to accept knowingly:* running neutral at 30–50 % SoC drains the battery faster and may
push it into the low bands more often. The SoC number is only a proxy for the thing actually being
avoided (expensive grid import) — see Phase 2 for the principled alternative.

### Finding 4 — humidity: the owner is right, my first recommendation was wrong

I initially proposed tapering the humidity term to zero below ~22 °C. That is wrong. Computing
Fanger PMV (ISO 7730, sedentary met 1.1, v 0.1 m/s, t_r = t_a) and converting to the equivalent air
temperature shift per **+10 % RH**:

```
air °C   PMV clo=1.0   PMV clo=0.5   current model @0.15
   16       0.18          0.13            0.27
   20       0.24          0.16            0.35
   22       0.26          0.18            0.40
   26       0.33          0.24            0.50
   30       0.40          0.30            0.63
```

Two conclusions:

1. **Humidity does affect felt temperature at winter indoor temperatures** — ~0.25 °C per 10 % RH at
   21 °C. It belongs in the calculation. Withdrawn: the taper proposal.
2. **The existing structure is right; only the scale is wrong.** The Magnus vapour-pressure form
   already grows with temperature at almost exactly the rate PMV does (2.6× from 16→32 °C versus
   PMV's 2.4× at fixed clo). But it is uniformly **~1.5× too strong**: 0.40 °C/10 % RH at 22 °C where
   PMV says 0.26.

**Fix: `HumidityCoefficient` 0.15 → 0.10. Config only, no code.** Over the observed indoor RH range
(37–61 %) that reduces the humidity contribution from 0.89 °C to 0.59 °C.

Note the goal direction: keeping humidity in the felt calculation is *how* humidity invariance is
achieved — the controller compensates for it (slightly warmer air when dry, slightly cooler when
humid). Removing the term would let humidity swings pass straight through to perceived comfort.

### Finding 6 — `EnvCoefficient` and the missing draught term

Live line from the deployed log (2026-08-12 21:07, outdoor 12.6 °C raw / 13.8 °C smoothed):

```
Felt temp Lounge (Heat): air 21.1°C + envelope -0.7 + humidity -0.1 = felt 20.2°C;
  set 23.0°C, force/on/off 19.0/20.0/21.0   ← Eco, because socmod = -1
```

An order-of-magnitude operative-temperature calculation for a typical brick-veneer room
(single-glazed windows U≈5.4 over ~6 % of enclosure area, walls U≈0.6, ceiling U≈0.3, interior film
8.3 W/m²K, `T_op = (T_air + MRT)/2`) gives **k ≈ 0.03**, so the configured 0.1 is already ~3× the
pure radiant physics — more evidence the shortfall is delivery, not coefficient. Holding felt
constant requires `air = set + k/(1−k)·(set − outdoor)`:

| `kEnv` | extra air °C per °C outdoor deficit | required air at set 23 °C, outdoor 8 °C |
|---|---|---|
| 0.10 | +0.11 | 24.7 °C |
| 0.15 | +0.18 | 25.6 °C |
| 0.20 | +0.25 | 26.8 °C |

Above ~0.15 the model demands implausible air temperatures on cold days.

What *is* missing is **wind-driven draught/infiltration** — the reason a cold windy day feels worse
than a cold still day. Outdoor wind over the window ranged **7.6–46.4 km/h** (median 18.4) and is
entirely unused.

### Observability: are the existing logs adequate?

Measured on the deployed add-on log:

- The `Felt temp` Debug line already contains **everything needed** — air, envelope, humidity,
  smoothed and raw outdoor, felt, and all thresholds — for every room, every evaluation.
- Volume ≈ **20 k lines/day** (~6 felt lines/min, one per room per evaluation, ~every 40 s).
- The journal caps at **~139 k entries ≈ 7 days**; a 400 k-entry request returns 139 k (26 MB, ~13 s).
- Log stamps are **time-only (`[21:07:00]`), no date**, so multi-day analysis requires inferring day
  rollovers.
- Rooms gated off before the log statement (switch off, or `null` profile at low SoC) emit **no line
  at all**, so the log silently omits some of the cases worth studying.
- The lines are `Debug` and survive only because the `appsettings.json` level is misspelled
  `"Waring"`; correcting that typo would delete this telemetry.

**Verdict: adequate for spot checks and for analysis up to ~7 days, not for tracking a tuning change
over a fortnight, and not chartable in HA next to SoC/price/outdoor temp.** Cheapest sufficient fix
is a **low-rate summary line** (one line per room every ~15 min, or only on threshold crossings, with
a full date stamp) rather than per-evaluation spam. A dedicated per-room felt-temperature entity is
still worth it if the value should be chartable in HA or retained beyond the recorder window.

### Data-quality caveat

`weather.forecast_home` (met.no) is the **only** outdoor source — there is no local outdoor sensor.
It updates irregularly: median gap **2.9 h**, p75 5.7 h, **max 14.25 h**. The 15 h EMA makes this a
non-issue for the envelope term, but any faster-moving term (wind) must not assume fresh data.

## Plan

### Phase 0 — observability (prerequisite)

- Replace the per-evaluation `Felt temp` Debug line with a **rate-limited summary** (per room, every
  ~15 min or on threshold crossing) carrying a **full date stamp**, so a fortnight fits inside the
  7-day journal cap without losing resolution. Emit a line even when a room is gated off, with the
  reason — that is currently invisible.
- Fix the `"Waring"` → `"Debug"` typo in `appsettings.json` at the same time, so the telemetry does
  not depend on a misspelling.
- Only if HA-chartable history is wanted: publish per-room felt temperature as an entity.
- **Success metric for everything below:** standard deviation of occupied-hours felt temperature
  versus outdoor temperature over a fortnight. Today ~2 °C over a day, ~0.75 °C evening-to-evening;
  target <0.5 °C.

### Phase 1 — re-target the coil-residual coast (preserving its purpose)

Keep the coast; make it fire at cycle end instead of mid-cycle.

- **Gate the negative drive on proximity to the off-point.** Allow `aggressiveness < 0` only when the
  room's felt temperature is within ~1 °C of its off-point (i.e. the zone is about to satisfy, so the
  residual really would be stranded). Further from target, floor the drive at 0.
- **Require a meaningful improvement to reset the stall clock** — a cumulative ~0.3 °C or a sustained
  trend, not a single 0.1 °C sensor tick — so quantisation noise cannot pin the term at −1.
- **Aggregate with `Max`, not `Average`.** Satisfied rooms have their zones closed anyway; averaging
  them in dilutes the demand of the room that actually needs heat.
- Add a modest error term so a room far from setpoint drives harder, capped (`MaxDrive`).
- Clamp the commanded setpoint to the melview-supported range.

Secondary argument worth testing rather than assuming: heat pumps are most efficient in long steady
part-load runs, and frequent compressor restarts carry start-up and defrost penalties — so longer,
better-targeted cycles may well *improve* efficiency as well as comfort. Without submetering this
must be judged on runtime and response, not measured directly.

### Phase 2 — widen the neutral SoC band

- **Move the neutral band to 30–90 %** (from 50–90). Leaves −2 (15–25) and −5 (0–15) untouched;
  cuts −1 duty from 44.7 % to 12.0 %.
- Watch for a second-order effect: more AC at 30–50 % SoC drains the battery faster, which could
  increase time spent in the −2/−5 bands. Re-measure the duty table after a fortnight.
- **Principled alternative, if the second-order effect bites:** the stated intent is avoiding
  *expensive grid import*, and SoC is only a proxy for it. The battery app already knows Amber prices
  and its own floor-defence state, so the AC economy could key off actual/forecast price instead —
  full comfort at 35 % SoC when power is cheap or solar is imminent, real economy at 60 % when a
  price spike is coming. Bigger change; better matches the intent.

### Phase 3 — the felt-temperature model

- **(3a) `HumidityCoefficient` 0.15 → 0.10.** Config only. Aligns with PMV (see Finding 4).
- **(3b) Add a draught/wind term.** `WindOffset = −kWind · max(0, windKmh − calmKmh)`, applied only
  while outdoor is below indoor (a cold-draught term, not a summer breeze). Start `WindCoefficient`
  0.03 °C per km/h, `CalmWindKmh` 10 → −0.6 °C at 30 km/h. Feed it a short-τ (≈3 h) EMA given the
  sparse, gusty source. *Refinement:* scale by `(air − outdoor)/10`, since infiltration loss scales
  with both.
- **(3c) Raise `MaxComfortOffset` 3 → 5.** Inert today, but the sum can reach it once wind is added,
  and a silently clipped offset re-introduces weather dependence.
- **(3d) Re-tune `kEnv` last**, with Phase 0 data. Hold at 0.1 through Phases 1–2; only if occupied
  felt temperature still trends down with outdoor temperature, step 0.125 → 0.15 and stop there.
- **(3e) Optional:** a solar-gain term from `uv_index`/`cloud_coverage` — the summer counterpart of
  the winter draught term.

### Phase 4 — optional, overnight recovery

Only if Phases 1–3 leave a morning gap. The living zones are vetoed overnight by the motion rule, so
the house coasts to ~18.5 °C and then has to recover with a cold-morning, low-SoC controller. Options:
a modest overnight floor, or a pre-heat timed to the battery planner's cheap window — the latter also
helps Phase 2 by not arriving at morning with a flat battery.

## Validation

- `ComfortMath` additions (wind offset, clamp interaction) get unit tests in
  [`test/apps/HassModel/AC/ComfortMathTests.cs`](../test/apps/HassModel/AC/ComfortMathTests.cs),
  matching the existing pure-function style.
- Phase 1's drive calculation should be extracted into a pure, testable function so "coast only near
  the off-point", "`Max` not `Average`", and the noise-resistant reset are covered by tests.
- `dotnet test` only. **Do not run the app** — its scheduler commands the live AC
  ([`../CLAUDE.md`](../CLAUDE.md)).
- After deploying, re-run this document's queries over a fortnight and compare occupied-hours felt
  temperature against outdoor temperature.
