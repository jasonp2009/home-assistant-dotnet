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

| SoC | Modifier |
|---|---|
| 90–100% | +1 (more aggressive) |
| 50–90% | 0 |
| 30–50% | −1 |
| 0–30% | −2 (more economical) |

[`GetEffectiveProfile`](../../src/apps/HassModel/AC/AcControl.cs#L218) then shifts the chosen profile
by that modifier: `desiredIndex = profileIndex − modifier`. Below the first profile it clamps to the
most aggressive (Boost Plus); past the last profile it returns `null`, which turns the zone **off**
entirely (the battery is too low to justify running that room).

## Driving setpoint & "aggressiveness"

`ShouldEnableZone` only decides *whether* a zone runs. *How hard* the unit drives is
`SetTemperature` ([AcControl.cs:122](../../src/apps/HassModel/AC/AcControl.cs#L122)). It computes an
**aggressiveness** term from how long the room temperature has been **failing to move in the desired
direction**:

- Per zone, `_tempLastChangedDict` records the last time the room temperature moved the *right* way
  (cooler while cooling, warmer while heating).
- The longer a room has stalled (no helpful movement) since it last changed or since the zone came
  on, the higher its aggressiveness. It is averaged across the active rooms, floored, and used to
  push the unit's **actual setpoint** past its measured room temperature:
  `SetTemp = RoomTemp ∓ aggressiveness`.

This is a time-since-progress feedback term, not a comfort term. Note it drives off the **unit's**
single `RoomTemp` (its return-air reading), whereas zone enable/disable uses the **per-room** sensors.

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
  so it does not take effect — the category falls back to `Default` (`Debug`). This is why the
  felt-temperature `Debug` lines below are visible in production today; if that key is ever corrected
  to `Warning`, set it to `Debug` instead to keep them.

## Debugging the felt temperature (deployed)

Read the deployed add-on logs over the HA REST API (see [deployed-logs.md](../deployed-logs.md)) and
look for:

- **`Felt-temperature control: …`** (`Information`, once at startup) — confirms the bound config
  (`EnvCoefficient`, `MaxComfortOffset`, humidity coefficient/reference, EMA τ and backfill window).
- **`Outdoor temp EMA seeded from N … sample(s) …`** (`Information`, once at startup) — how many
  weather-history points seeded the EMA and the resulting `smoothed` vs `instantaneous` outdoor temp.
- **`Felt temp <Room> (<Mode>): air … + envelope … + humidity … = felt …°C; set …, force/on/off …`**
  (`Debug`, once per room per evaluation) — the full per-room breakdown: the air temperature, each
  offset component (so you can see whether the radiant or the humidity term is driving the gap), the
  smoothed vs raw outdoor temperature, the resulting felt temperature, and the thresholds it is
  compared against. This is the line to grep when a room feels off.

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
correction). The total offset is clamped to ±`MaxComfortOffset` (default 3 °C) so a glitched outdoor
reading can't drive the unit to extremes. Start from the default and tune; the outdoor temperature
moves slowly, so the offset drifts gently and does not cause mode/zone thrash.

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
hotter) when muggy, slightly negative when very dry. The effect is small at mild winter conditions
and grows on a hot, humid summer afternoon (e.g. ~+2.8 °C at 30 °C / 70% RH), where it makes cooling
a little more aggressive. `HumidityCoefficient` defaults to 0.33 (Steadman). Rooms without a humidity
sensor simply omit this term.
