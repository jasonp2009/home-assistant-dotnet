# AC control app

Controls a single **ducted Mitsubishi air conditioner** with multiple **zones** (one per room), so
each room behaves like it has its own thermostat. The unit is driven through the Mitsubishi
[melview](https://api.melview.net) cloud API (not through Home Assistant), while the per-room
sensors, setpoints, switches and logs are Home Assistant entities.

- Code: [`../../src/apps/HassModel/AC/`](../../src/apps/HassModel/AC/)
- Config: [`../../src/apps/HassModel/AC/AcControl.yaml`](../../src/apps/HassModel/AC/AcControl.yaml)
- Mitsubishi client: [`../../src/apps/HassModel/AC/MitsubishiClient/`](../../src/apps/HassModel/AC/MitsubishiClient/)

## How it runs

[`AcControl`](../../src/apps/HassModel/AC/AcControl.cs) is a `[NetDaemonApp]` implementing
`IAsyncInitializable`. On startup it logs into the melview API, applies the current battery
state-of-charge profile shift, then runs one full evaluation. After that it re-evaluates whenever
anything relevant changes:

- a room's **AC toggle**, **set temperature**, **temperature sensor**, or **profile select** changes;
- a room's **motion** or **contact** sensor changes;
- the **weather** entity (`weather.forecast_home`) changes;
- the **battery state-of-charge** sensor changes (recomputes the profile shift — see [SoC profile shift](#soc-profile-shift));
- a **60-second poll** refreshes the live AC state from melview and re-evaluates if the unit's
  measured room temperature changed.

Each evaluation is `HandleChange` ([AcControl.cs:107](../../src/apps/HassModel/AC/AcControl.cs#L107)):

1. Choose the unit **mode** (`Cool`/`Heat`) — `GetDesiredAcMode`.
2. Set the unit's driving **setpoint** — `SetTemperature` (the "aggressiveness" term, below).
3. For every room, enable/disable its **zone** — `ShouldEnableZone`.
4. Turn the **whole unit** on iff any zone is on, and set **fan** to `High` when more than two zones
   are on, otherwise `Low`.
5. Mirror the resulting state into Home Assistant **log helper entities**.

> The unit has a single shared mode and setpoint; only the **zones** are per-room. So heating one
> room while cooling another is impossible — `GetDesiredAcMode` picks whichever mode the most rooms
> currently want, and stays on the current mode as long as at least one room still wants it.

## Per-zone thermostat: `ShouldEnableZone`

[`ShouldEnableZone`](../../src/apps/HassModel/AC/AcControl.cs#L184) is the core decision: should this
room's zone be open right now? It regulates the room's **measured air temperature**
(`room.CurrentTemperate`, parsed from the room's temperature sensor) against the user's
**set temperature**, using a hysteresis band whose widths come from the room's effective **profile**:

For **cooling** (heating mirrors with the signs flipped):

| Point | Value | Meaning |
|---|---|---|
| `forcePoint` | `setTemp + ForceTolerance` | when the zone is **off**, how hot the room must get before the zone is forced on |
| `onPoint` | `setTemp + OnTolerance` | when the unit is already **on**, the (tighter) threshold to keep/turn the zone on |
| `offPoint` | `setTemp + OffTolerance` | cool past this and the zone turns off |
| `weatherOffPoint` | `setTemp − WeatherOffset` | **economy gate**: if it is already this cool *outside*, don't cool at all |

So in cooling: if it is cool enough outside the zone stays off; otherwise the zone turns on above the
on/force point and off below the off point. Heating is the mirror image (turn on when the room is
*below* the point, gate off when it is already warm enough outside).

The decision is also gated by occupancy — see [Occupancy gating](#occupancy-gating).

## Profiles

A profile is a set of hysteresis widths. The user picks one per room via an `input_select`
(`AcProfileSelectEntity`). Profiles are ordered from most aggressive to most economical
([AcControl.yaml](../../src/apps/HassModel/AC/AcControl.yaml)):

| Profile | ForceTolerance | OnTolerance | OffTolerance | WeatherOffset |
|---|---|---|---|---|
| Boost Plus | 1 | 1 | 0.5 | 5 |
| Boost | 1.5 | 1 | 0.5 | 4 |
| Standard *(default)* | 2 | 1.5 | 1 | 3 |
| Eco | 4 | 3 | 2 | 2 |
| Eco Plus | 5 | 4 | 3 | 1 |

A tighter band (Boost) holds the room closer to setpoint at the cost of more runtime; a wider band
(Eco) lets the room drift further before acting.

## SoC profile shift

The battery's state of charge nudges every room toward a more aggressive or more economical profile,
so the AC leans on cheap/abundant stored energy when the battery is full and backs off when it is
low. [`HandleSocChange`](../../src/apps/HassModel/AC/AcControl.cs#L236) maps SoC to a
`_curSocModifier` via `SocAdjusts` (with a hysteresis `Tolerance` so it doesn't flap at a boundary):

| SoC | Modifier | |
|---|---|---|
| 90–100% | +1 | more aggressive |
| 30–90% | 0 | neutral |
| 25–30% | −1 | more economical |
| 15–25% | −2 | |
| 0–15% | −5 | past the last profile → zone off entirely |

The **neutral band deliberately reaches down to 30%**. Measured over 10 days, the daily SoC trough
parks in the 30–45% range (modal bucket 35–40%), so a neutral band starting at 50% left the whole
house on `Eco` — a 4 °C allowed felt deficit instead of 2 °C — for ~45% of the time, and did so
disproportionately in cold weather (mean modifier −1.4 at 4–8 °C outdoor versus 0.0 at 14–18 °C),
because cold → more heating → flatter battery → wider deadband → less heating. The boundary sits at
30 rather than 35 so the modal trough falls *inside* the neutral band instead of straddling its edge
and fighting the `Tolerance` hysteresis.

> Note the `Tolerance` is hysteresis on *leaving* a band, so with `Tolerance: 2` the −1 band (25–30)
> is held across 23–32 once entered. The bands are intentionally allowed to overlap in that sense.

[`GetEffectiveProfile`](../../src/apps/HassModel/AC/AcControl.cs#L218) then shifts the chosen profile
by that modifier: `desiredIndex = profileIndex − modifier`. Below the first profile it clamps to the
most aggressive (Boost Plus); past the last profile it returns `null`, which turns the zone **off**
entirely (the battery is too low to justify running that room).

## Driving setpoint & the coil-residual coast

`ShouldEnableZone` only decides *whether* a zone runs. *How hard* the unit drives is `SetTemperature`,
which sets the unit's setpoint as an offset from its own return-air reading:
`SetTemp = RoomTemp ± drive`. The maths is pure and unit-tested in
[`DriveMath`](../../src/apps/HassModel/AC/DriveMath.cs).

The drive has two terms, per room:

| Term | Meaning |
|---|---|
| **proportional** | `DriveErrorGain × error`, where `error` is how far the room's **felt** temperature still is from its `offPoint` — the point at which its zone would switch off |
| **stall** | `minutes since the room last made progress ÷ 5 − 1`, floored at −1 — the original time-since-progress feedback |

They are aggregated across active rooms with **`Max`**, not an average: the unit has one setpoint and
the zones gate delivery, so a room that is already satisfied has its zone shut anyway and must not be
able to dilute a room that is still cold.

### Why a *negative* drive exists

A negative drive commands a setpoint past the unit's own return-air temperature, which idles the
compressor while the fan keeps running. That is deliberate — it blows the heat (or cold) still stored
in the coil into the house rather than stranding it, and avoids paying to warm the coil only to shut
down moments later.

**But that only pays off at the end of a cycle.** Residual harvested mid-cycle, while the room is
still well short of target, is simply re-heated minutes later. So the stall term's sign is gated on
`DriveCoastWindow`:

- **Within `DriveCoastWindow` (default 1 °C) of the off-point** — the cycle is ending, the stall term
  applies in full and may pull the drive negative. The coast behaves exactly as it always did.
- **Further out** — the stall term is clamped to a *bonus*: it can still add drive to a room that is
  failing to respond, but it can no longer subtract. The unit is never told to stop while a room is
  still well short of its target.

### Progress must be sustained, not a single tick

`_tempLastChangedDict` records when a room last made progress, and the stall clock resets from it. The
room sensors quantise at **0.1 °C**, so resetting on any single favourable tick pinned the drive at −1
right through the middle of heating cycles. Instead, `DriveMath.AccumulateProgress` accumulates *net*
movement in the conditioned direction — the wrong way subtracts, and the total is floored at zero — and
only resets the clock once it clears `DriveProgressThreshold` (default 0.3 °C). A room oscillating on
sensor noise therefore never registers as responding.

### Rounding

`DriveMath` deliberately returns a **fractional** drive. `MitsubishiClient.SetTemperature` already
integerises the final setpoint, and does so *in the conditioning direction* (`Ceiling` when heating,
`Floor` when cooling). Rounding in the drive as well would round the wrong way first and discard up to
a degree before the client ever saw it.

> Note the drive works off the **unit's** single `RoomTemp` (its return-air reading), whereas zone
> enable/disable and the drive's own `error` term use the **per-room** felt temperatures.

`AcAggressivenessLogEntity` logs the **unrounded, uncapped** drive, so the fractional detail stays
visible in history and it is obvious when `MaxDrive` binds.

## Occupancy gating

[`CheckContactAndMotion`](../../src/apps/HassModel/AC/AcControl.cs#L254) can veto a zone:

- If a room defines a `MotionEnabledFrom`/`MotionEnabledTo` window and **now is outside** it, motion
  is **not** required (the zone is allowed regardless). Example: a bedroom set to 09:00–21:00 ignores
  motion overnight so it works while you sleep.
- Otherwise the zone is allowed **unless** a **contact** sensor has been open for >5 min (a door/
  window left open) **or** all **motion** sensors have been off for >15 min (room empty).

## Mitsubishi (melview) client

[`MitsubishiClient`](../../src/apps/HassModel/AC/MitsubishiClient/MitsubishiClient.cs) talks to
`api.melview.net`. It logs in for a cookie, then every command POSTs to `unitcommand.aspx` and parses
the returned [`AcState`](../../src/apps/HassModel/AC/MitsubishiClient/Models/AcState.cs). Commands are
short codes: `PW{0/1}` power, `MD{n}` mode, `TS{n}` setpoint, `Z{zone}{0/1}` zone on/off, `FS{n}`
fan. Each setter is a no-op if the unit is already in the requested state. `UpdateState(null)` just
refreshes state.

## Home Assistant entities

Per room ([`AcRoomConfig`](../../src/apps/HassModel/AC/AcConfig.cs)): a temperature sensor, a set-
temperature `input_number`, an on/off `input_boolean`, a profile `input_select`, optional motion/
contact `binary_sensor`s, and a `ZoneOnLogEntity`. Globally
([`AcConfig`](../../src/apps/HassModel/AC/AcConfig.cs)): the battery SoC sensor plus log helpers
(`AcOnLog`, `AcModeLog`, `AcAggressivenessLog`, `SocModifierLog`). The `weather.forecast_home` entity
supplies the outdoor temperature for the economy gate.

Every room sensor is a **temperature *and* humidity** sensor, so a paired `..._humidity` entity
exists for each room (see [Felt-temperature control](#felt-temperature-control) for how this is used).

## Gotchas / notes

- **Do not run this app locally** — like the battery app, its scheduler issues real commands to the
  live AC. Verify with `dotnet test`. See [`../../CLAUDE.md`](../../CLAUDE.md).
- **Single mode/setpoint** for the whole unit; only zones are per-room (see above).
- **Two different temperatures**: zone decisions use per-room HA sensors; the aggressiveness/driving
  setpoint uses the unit's own return-air `RoomTemp`.
- The log level for `src.apps.HassModel.AC.AcControl` in `appsettings.json` is misspelled `"Waring"`,
  so it does not take effect — the category falls back to `Default` (`Debug`). **Set it to
  `"Information"`**: that keeps all the telemetry below except the per-evaluation `Debug` breakdown,
  which is what drives the log volume. Do *not* set it to `"Warning"` — that would suppress the
  summaries too. `appsettings.json` is not tracked by git, so this has to be changed by hand.

## Debugging the felt temperature (deployed)

Read the deployed add-on logs over the HA REST API (see [deployed-logs.md](../deployed-logs.md)) and
look for:

| Log line | Level | Rate |
|---|---|---|
| `Felt-temperature control: …` | `Information` | once at startup — confirms the bound config |
| `Outdoor temp EMA seeded from N … sample(s) …` | `Information` | once at startup — samples found, smoothed vs instantaneous outdoor temp |
| `Felt <date> <Room> (<Mode>): air … = felt …°C \| set …, force/on/off … \| outdoor … \| zone …` | `Information` | per room, every `FeltLogIntervalMinutes`, **plus immediately on any decision change** |
| `Felt <date> <Room> (<Mode>): zone off — <veto>` | `Information` | same, for rooms refused before the comparison could run |
| `Drive <date> (<Mode>): N room(s) driving, raw … -> …°C past return air …` | `Information` | same cadence, for the driving setpoint |
| `Felt temp <Room> …` (full breakdown) | `Debug` | once per room per evaluation — deep dives only |
| `Drive <Room>: felt … vs off-point … (error …), stalled … min -> …` | `Debug` | per room per evaluation — why the drive is what it is |

### Why the summaries are rate-limited

The add-on journal holds roughly **7 days** (~139 k entries); at `Debug` the app writes ~20 k
lines/day, so the full breakdown cannot survive long enough to judge a tuning change made a fortnight
ago. The `Information` summaries are paced to `FeltLogIntervalMinutes` (default 15) so a fortnight
fits comfortably, and they carry a **full date** because the journal's own stamps are time-only.

They are also emitted for **vetoed** rooms. Previously a room refused before the felt-temperature
comparison — switched off, occupancy veto, or pushed past the last profile by the SoC shift — produced
no line at all, which silently hid exactly the rooms worth investigating. The veto reason is now named
(`Occupancy`, `SwitchedOff`, `NoReading`, `NoProfile`, `NotConditioning`).

## Felt-temperature control

The dry-bulb air temperature a sensor reads is only one input to how warm or cold a room actually
*feels*. The controller therefore regulates an estimated **felt** (apparent) temperature: it runs the
[hysteresis above](#per-zone-thermostat-shouldenablezone) against
`ComfortMath.FeltTemperature(...)` instead of the raw `room.CurrentTemperate`. The felt temperature
is the air temperature plus a set of physically-motivated offsets (clamped to ±`MaxComfortOffset`).

[`ComfortMath`](../../src/apps/HassModel/AC/ComfortMath.cs) is pure, IO-free math (unit-tested in
[`test/.../AC/ComfortMathTests.cs`](../../test/apps/HassModel/AC/ComfortMathTests.cs)).

### Radiant (envelope) offset

The dominant indoor comfort factor besides air temperature is **mean radiant temperature** — the
temperature of the surfaces around you. A room's external walls and windows sit between the indoor
air and the outdoor air, so the colder it is outside the more those surfaces draw body heat away
(the room *feels* colder than the sensor reads); the hotter it is outside, the warmer they radiate.

`EnvelopeOffset = kEnv · (outdoorTemp − airTemp)`:

- **Winter** — outdoor < air → negative offset → felt temp below air temp → the controller heats
  more. *Example:* air 21 °C, outdoor 8 °C, `kEnv` 0.1 → felt ≈ 19.7 °C, so a 22 °C setpoint keeps
  heating where raw air temp alone would have let it coast.
- **Summer** — outdoor > air → positive offset → felt temp above air temp → cools more.

`kEnv` (config `EnvCoefficient`) rolls each room's exposure (window area / insulation) into one
number. It is global by default (`AcConfig.EnvCoefficient`, 0.1) with an optional per-room override
(`AcRoomConfig.EnvCoefficient`) — the internal **Hallway** is set to `0` (no external surfaces, no
correction). The total offset is clamped to ±`MaxComfortOffset` (default 5 °C) so a glitched outdoor
reading can't drive the unit to extremes. Start from the default and tune; the outdoor temperature
moves slowly, so the offset drifts gently and does not cause mode/zone thrash.

> `MaxComfortOffset` is a **sanity guard, not a tuning knob**. If it binds in ordinary weather it is
> silently flattening the correction, which hands back exactly the weather dependence the felt
> temperature exists to remove. A test asserts it stays clear in the harshest plausible local winter
> (0 °C outdoors, 21 °C indoors, 50 km/h wind).

#### How far can `kEnv` go?

Holding the felt temperature constant requires `air = set + kEnv/(1−kEnv)·(set − outdoor)`:

| `kEnv` | extra air °C per °C outdoor deficit | required air at set 23 °C, outdoor 8 °C |
|---|---|---|
| 0.10 | +0.11 | 24.7 °C |
| 0.15 | +0.18 | 25.6 °C |
| 0.20 | +0.25 | 26.8 °C |

Beyond ~0.15 the model demands implausible air temperatures on cold days. For reference, an
order-of-magnitude operative-temperature calculation (`T_op = (T_air + MRT)/2`, single-glazed windows
over ~6% of enclosure area) puts the *pure radiant* value near **0.03**, so the configured 0.1 is
already generous — if the house still feels cold on cold days, suspect delivery before this
coefficient.

### Draught (wind) offset

Wind drives cold outside air through the envelope and raises air movement indoors, both of which carry
heat away from skin — which is why a cold *windy* day feels markedly worse than a cold still day at
the same temperature. Local wind ranges roughly 8–46 km/h, and none of it reached the felt temperature
before this term existed.

`WindOffset = −WindCoefficient · max(0, windKmh − CalmWindKmh)`

- Zero at or below `CalmWindKmh` (10 km/h) — ordinary background air movement is already priced into
  how a room normally feels.
- **One-sided**: it applies only while it is *colder outside than in*. Air forced in from a warmer
  outdoors is not a cold draught, and in cooling season a breeze is more likely to be welcome, so wind
  never makes a room feel cooler than the rest of the model already thinks it is.
- `WindCoefficient` defaults to **0.03** °C per km/h → −0.6 °C at 30 km/h.

The wind speed is EMA-smoothed like the outdoor temperature but with a much shorter constant,
`WindTimeConstantHours` (3 h against 15 h). Draught is felt as the weather does it, not filtered
through the building's thermal mass; it is smoothed at all only because wind is gusty and the weather
entity updates irregularly (median 2.9 h between samples, max 14.25 h). It is seeded on startup from
`wind_speed` history the same way.

> This is distinct from the profile's `WeatherOffset`, which stays an independent on/off **economy
> gate** on the outdoor temperature; the envelope offset is a continuous **comfort** correction on
> the regulated temperature.

#### Smoothing the outdoor temperature

The surfaces driving the radiant offset have **thermal mass**, so they respond slowly — they don't
track the daily air-temperature swing. Feeding the *instantaneous* outdoor reading into the envelope
offset would make the correction oscillate on the day/night cycle (and push `feltTemp` across the
hysteresis band on the weather's clock). So the envelope offset uses an **exponentially-smoothed**
outdoor temperature, `SmoothedWeatherTemperature`, not the raw reading.

The smoother is a standard EMA (`ComfortMath.EmaStep`), updated on each weather change and on the
60 s loop: `ema += (1 − exp(−Δt/τ)) · (reading − ema)`. Because it is the discretised first-order
(RC) response, the time constant `τ` (`OutdoorTempTimeConstantHours`, default 15 h) *is* the
envelope's thermal time constant — larger = smoother and slower. On startup `SeedOutdoorTempEmaAsync`
backfills it by replaying `OutdoorTempBackfillHours` (default 48 h) of `weather.forecast_home`
temperature history (via `HaHistoryClient.GetAttributeHistoryAsync` — temperature is a weather
*attribute*, so this reads attributes rather than state) through `ComfortMath.SeedEma`, so it boots
at a sensible value instead of cold-starting at the current reading. The weather entity's recorded
history is sparse (~a dozen irregular points over 48 h), which the irregular-Δt EMA handles fine; if
the backfill returns nothing it simply starts from the current reading. Setting `τ = 0` disables
smoothing. The `WeatherOffset` economy gate continues to use the *instantaneous* outdoor temperature.

### Humidity offset

Humid air feels warmer than dry air at the same temperature (sweat evaporates less freely). Each room
sensor is a temperature **and humidity** sensor, so when a room has a `HumiditySensorEntity` the felt
temperature also includes the Steadman apparent-temperature vapour-pressure term:

`HumidityOffset = HumidityCoefficient · (e(T, rh) − e(T, refRh))`

where `e` is the water-vapour pressure (`ComfortMath.VapourPressure`, Magnus approximation) and
`refRh` is `ReferenceHumidity` (default 50%). Anchoring to a reference humidity means a typical indoor
humidity contributes ≈0, so only unusual humidity moves the felt temperature — positive (feels
hotter) when muggy, slightly negative when very dry. Rooms without a humidity sensor omit this term.

#### Calibrating `HumidityCoefficient`

`HumidityCoefficient` defaults to **0.10**, calibrated against **Fanger PMV** (ISO 7730, sedentary
met 1.1, still air, `t_r = t_a`) expressed as the equivalent air-temperature shift per **+10% RH**:

| air °C | PMV (clo 1.0) | this model @ 0.10 |
|---|---|---|
| 16 | 0.18 | 0.18 |
| 22 | 0.26 | 0.26 |
| 26 | 0.33 | 0.33 |
| 30 | 0.40 | 0.42 |

Two things fall out of that table, and both matter:

- **The Magnus vapour-pressure form is the right shape.** Its sensitivity grows with temperature at
  almost exactly PMV's rate (≈2.6× from 16→32 °C, against PMV's ≈2.4× at fixed clothing), so no
  temperature-dependent weighting is needed — the physics is already in the exponential.
- **Humidity is *not* negligible indoors in winter.** At 20 °C a 10% RH change is still worth ≈0.24 °C.
  The term must therefore stay active year-round. Note the direction of the goal: *keeping* humidity in
  the felt temperature is what makes comfort humidity-invariant, because the controller then
  compensates for it (slightly warmer air when dry, slightly cooler when muggy). Removing or tapering
  the term would let humidity swings pass straight through to how the room feels.

The textbook Steadman coefficient of **0.33** is an *outdoor* apparent-temperature figure and
over-weights humidity at room temperature by roughly 3×. The previous value of **0.15** was ≈1.5× too
strong (0.40 °C per 10% RH at 22 °C, against PMV's 0.26). The calibration is pinned by tests in
[`ComfortMathTests`](../../test/apps/HassModel/AC/ComfortMathTests.cs) so it cannot drift silently.
