# SimHub Component - Build & Deployment

Build, deployment and field-map notes for the G-Rebels SimHub plugin.

> [!NOTE] **Acknowledgement:**
> Telemetry mappings, control inputs, and event mechanics in this component were derived
> and verified through empirical packet capture analysis, flight tests, and video correlation.
> DannyDan’s third-party plugin (`GRebelsPlugin.dll` v1.0.0 / v1.1.0) served as a valuable
> secondary reference for cross-checking field names and confirming candidate offsets.

## 1. Current Scope

- **Expanded layout only** (game update 2026-10-27+).
- There is **no automatic SRS/Expanded detection**. If the game sends an SRS
  packet, the plugin will interpret it with the Expanded offsets and publish
  wrong values.
- The plugin publishes every property in `Properties`; there is no
  disabled-by-default base configuration.
- The C# code does not need to change for this field-map update. The layout is
  entirely JSON-driven.

### Future Scope
- **Directional Shaker Impulses**: Synthesizing 6-axis discrete tactile pulses (`CollisionImpulseFront`, `CollisionImpulseRear`, `CollisionImpulseLeft`, `CollisionImpulseRight`, `CollisionImpulseBottom`, `CollisionImpulseTop`) for dedicated 4-corner or multi-transducer shaker rigs.
- **Directional Native Events**: Discrete directional trigger events (`CollisionFront`, `CollisionRear`, `CollisionLeft`, `CollisionRight`, `CollisionBottom`, `CollisionTop`) for triggering directional SimHub actions and audio effects.

## 2. Building

For build prerequisites, build commands, and custom path configuration options, see the [Building from Source](../README.md#building-from-source) section in `README.md`.

## 3. Deploying

For deployment procedures—including manual deployment steps and removal instructions—see the [Deployment](../README.md#deployment) section in `README.md`.

## 4. Configuration Schema

The current plugin uses this shape for its external field map (`SimHub.GRebels.json`):

```json
{
  "Port": 33001,
  "BindAddress": "127.0.0.1",
  "StaleSeconds": 1.0,
  "WeaponPulseMs": 100,
  "HitPulseMs": 200,
  "HitStrengthRef": 25.0,
  "DumpFirstPackets": 0,
  "Fields": {
    "ExpectedSize": 236,
    "Fields": {
      "Speed": { "Type": "float", "Offset": 172 }
    }
  },
  "Properties": [
    { "Property": "SpeedRaw", "Field": "Speed", "Min": 0, "Max": 100000 }
  ],
  "EdgeEvents": []
}
```

Notes:

- `Fields.ExpectedSize` is `236`. A 233-byte packet is still parsed for fields
  inside the payload, but it increments `LayoutMismatchCount`.
- `Type` can be `float`, `double`, `int`, `short`, `byte` or `string`.
- Per-field `Scale` and `Bias` are applied while parsing.
- A property can apply its own `Scale`, `Bias`, `Min` and `Max`; this is how the
  signed throttle is also published as a percentage.
- `EdgeEvents` is empty. The plugin's edge detector compares a field against a
  threshold; it does not synthesize events from counter deltas.

## 5. Post-Deployment Verification Checklist

- [ ] Plugin loads and reports the expected field count.
- [ ] Packet rate is reasonable and `FeedStale` is false during play.
- [ ] `Throttle` goes from `-1` through `0` to `+1`.
- [ ] `ThrottlePercent` goes from `0` through `50` to `100`.
- [ ] `ThrottleVertical` and `ThrottleVerticalPercent` respond to vertical thrusters.
- [ ] `ThrottleLateral` and `ThrottleLateralPercent` respond to strafe thrusters.
- [ ] `ThrottleTotalPercent` reflects composite 3-axis thruster load.
- [ ] `InputForward`, `InputVertical`, and `InputLateral` move independently.
- [ ] `SpeedRaw` rises and falls with movement.
- [ ] `Pitch`, `Roll`, `Yaw` and `Heading` respond; verify signs before using
      ShakeIt effects.
- [ ] `LocalSurgeMs2`, `LocalSwayMs2` and `LocalHeaveMs2` respond and stay
      within `-10..10`.
- [ ] `PrimaryCount` and `SecondaryCount` increment correctly.
- [ ] `PrimaryFired` and `SecondaryFired` pulse to 100 on each shot (and fire SimHub events) without false triggers on pause/exit.
- [ ] `HitCount` increments correctly on incoming projectile hits.
- [ ] `HitFired` (pulse to 100) and `HitImpulse` (strength-weighted decay) activate on incoming hits and fire `HitReceived` event.
- [ ] `CollisionCount` increments correctly on physical collisions (and resets on respawn).
- [ ] `CollisionFired` (pulse to 100) and `CollisionImpulse` (strength-weighted decay) activate on physical collisions, and fire `CollisionReceived` event.
- [ ] `CollisionLastStrength` latches collision strength across zero-resets.

## 6. Troubleshooting

| Issue | Cause | Action |
|---|---|---|
| Speed looks like pitch or attitude values | SRS packet received | Use Expanded output; this config has no SRS detection. |
| `Throttle` behaves like acceleration | SRS packet received | Same as above. `@200` is acceleration in SRS. |
| `SpeedRaw` is huge | Expanded speed unit is unresolved | Do not treat it as km/h. Measure against the in-game HUD before scaling. |
| `LocalHeaveMs2` looks wrong | `@184` is byte-identical to `@196` | Treat the vertical-acceleration label as suspect until a dedicated test. |
| `ThrottlePercent` is 0 when the feed stops | Stale feed returns 0 for every mapped property | Expected with the current plugin code. |
| 233-byte packets counted as mismatches | `ExpectedSize` is 236 | Fields inside the payload still parse; the 233-byte variant is not fully mapped. |

## 7. Expanded Field Map

All offsets are decimal byte offsets into the 236-byte payload.

| Field | Type | Offset | Meaning / notes |
|---|---|---:|---|
| `Pitch` | f32 | 160 | Attitude. |
| `PitchInv` | f32 | 160 | `Pitch` with `Scale: -1`. |
| `Roll` | f32 | 164 | Attitude. |
| `Yaw` | f32 | 168 | Attitude. |
| `Heading` | f32 | 168 | Same raw value as `Yaw`. |
| `Speed` | f32 | 172 | Raw speed; unit unresolved. |
| `LongitudinalAcc` | f32 | 176 | Surge input. |
| `LateralAcc` | f32 | 180 | Sway input. |
| `VerticalAcc` | f32 | 184 | Heave input; label suspect because `@184 == @196`. |
| `Expanded.Float07` | f32 | 188 | Raw signed forward velocity-like value. |
| `Expanded.Float08` | f32 | 192 | Raw lateral velocity-like value. |
| `Expanded.Float09` | f32 | 196 | Raw signed vertical velocity-like value. |
| `InputForward` | f32 | 200 | Signed forward/reverse throttle, `-1..+1`. |
| `InputVertical` | f32 | 204 | Signed vertical thrust, `-1..+1`. |
| `InputLateral` | f32 | 208 | Signed lateral thrust. |
| `PrimaryCount` | i32 | 212 | Cannon rounds. |
| `SecondaryCount` | i32 | 216 | Missiles fired. |
| `HitCount` | i32 | 220 | Incoming hit counter. |
| `CollisionCount` | i32 | 224 | Cumulative physical collision counter. |
| `HitStrength` | f32 | 228 | Latest hit projectile damage/strength. |
| `CollisionStrength` | f32 | 232 | Signed physical collision contact force (-250..+250). |

## 8. Published Properties

All properties are published with prefix `GRebels.` in SimHub:

| Property | Source | Scale / range | Notes |
|---|---|---|---|
| `GRebels.SpeedRaw` | `Speed` @172 | `0..100000` | Raw; **not** calibrated as km/h. |
| `GRebels.Pitch` | `PitchInv` @160 | `-90..90` | Inverted by the field's `Scale: -1`. |
| `GRebels.Roll` | `Roll` @164 | `-180..180` | |
| `GRebels.Yaw` | `Yaw` @168 | `-180..180` | |
| `GRebels.Heading` | `Heading` @168 | `-180..180` | |
| `GRebels.LocalSurgeMs2` | `LongitudinalAcc` @176 | `raw * 0.05`, clamp `±10` | Axis sign/label not independently proven. |
| `GRebels.LocalSwayMs2` | `LateralAcc` @180 | `raw * 0.05`, clamp `±10` | Axis sign/label not independently proven. |
| `GRebels.LocalHeaveMs2` | `VerticalAcc` @184 | `raw * 0.05`, clamp `±10` | `@184` duplicates `@196`; label suspect. |
| `GRebels.Expanded.Float07` | `@188` | raw | Forward velocity-like; negative in reverse. |
| `GRebels.Expanded.Float08` | `@192` | raw | Lateral velocity-like. |
| `GRebels.Expanded.Float09` | `@196` | raw | Vertical velocity-like. |
| `GRebels.Throttle` | `InputForward` @200 | `-1..+1` | `+1` forward, `-1` reverse, `0` neutral. |
| `GRebels.ThrottlePercent` | `InputForward` @200 | `(value + 1) * 50`, `0..100` | `0` reverse, `50` neutral, `100` forward. |
| `GRebels.ThrottleVertical` | `InputVertical` @204 | `-1..+1` | `-1` down, `+1` up. |
| `GRebels.ThrottleVerticalPercent` | `InputVertical` @204 | `(value + 1) * 50`, `0..100` | `50` neutral. |
| `GRebels.ThrottleLateral` | `InputLateral` @208 | `-1..+1` | `-1` left, `+1` right. |
| `GRebels.ThrottleLateralPercent` | `InputLateral` @208 | `(value + 1) * 50`, `0..100` | `50` neutral. |
| `GRebels.ThrottleTotalPercent` | Synthesized | `0..100` | Composite 3-axis thruster magnitude. |
| `GRebels.InputForward` | `InputForward` @200 | `-1..+1` | Alias for forward axis. |
| `GRebels.InputVertical` / `Input.Vertical` | `InputVertical` @204 | `-1..+1` | Alias for vertical thruster axis. |
| `GRebels.InputLateral` / `Input.Lateral` | `InputLateral` @208 | `-1..+1` | Alias for lateral strafe axis. |
| `GRebels.PrimaryCount` | `PrimaryCount` @212 | `0..2147483647` | Cumulative cannon round counter. |
| `GRebels.SecondaryCount` | `SecondaryCount` @216 | `0..2147483647` | Cumulative missile counter. |
| `GRebels.PrimaryFired` | Synthesized | `0 or 100` | Pulses to 100 for `WeaponPulseMs` on cannon fire. |
| `GRebels.SecondaryFired` | Synthesized | `0 or 100` | Pulses to 100 for `WeaponPulseMs` on missile fire. |
| `GRebels.HitCount` | `HitCount` @220 | `0..2147483647` | Cumulative incoming hit counter. |
| `GRebels.HitStrength` | `HitStrength` @228 | `0..30` | Latest hit projectile damage/strength (calibrated against observed ~8.8–26.7). |
| `GRebels.HitFired` | Synthesized | `0 or 100` | Pulses to 100 for `HitPulseMs` on incoming hit. |
| `GRebels.HitImpulse` | Synthesized | `0.0..100.0` | Strength-weighted decaying haptic impulse for hits. |
| `GRebels.CollisionCount` | `CollisionCount` @224 | `0..2147483647` | Cumulative collision counter; resets on respawn, freezes during continuous contact. |
| `GRebels.CollisionStrength` | `CollisionStrength` @232 | `-250..250` | Signed contact normal force/impedance from packet. Direct wall collisions produce compressive negative loads (-200..-247); floor contacts produce low negative/near-zero values. |
| `GRebels.CollisionStrengthAbs` | Synthesized | `0..250` | Absolute collision magnitude for UI bars and gauges. |
| `GRebels.CollisionLastStrength` | Synthesized | `0..250` | Latched collision strength preserved across resets. |
| `GRebels.CollisionFired` | Synthesized | `0 or 100` | Pulses to 100 for `CollisionPulseMs` on physical collision impact. |
| `GRebels.CollisionImpulse` | Synthesized | `0.0..100.0` | Strength-weighted decaying haptic impulse for collisions. |
| `GRebels.CollisionDirection` | Synthesized | `string` | Dominant impact direction during pulse: `Front`, `Rear`, `Left`, `Right`, `Bottom`, `Top`, or `None`. |
| `GRebels.CollisionDirectionCode` | Synthesized | `0..6` | Numeric impact direction: `0`=None, `1`=Front, `2`=Rear, `3`=Left, `4`=Right, `5`=Bottom, `6`=Top. |

`Throttle` is the game's signed control axis. It is **not** a persistent
0-100 throttle position and is not written into the `.simdef` throttle field.
`ThrottlePercent` is a derived convenience value, not a second telemetry field.

## 9. Known Limitations

- SRS packets are misread because layout detection is not implemented.
- Expanded `@172` speed units are unresolved; do not publish it as km/h yet.
- `@184` and `@196` are byte-identical in every Expanded capture tested, so the
  heave label is not physically established.
- The 233-byte variant is not fully mapped.
- On a stale feed, all mapped properties return `0`; `ThrottlePercent` therefore
  returns `0`, not neutral `50`.

## 10. References

- [`README.md`](../README.md) - Plugin overview and configuration guide.
- [`SimHub.GRebels.json`](../SimHub.GRebels.json) - The live Expanded field map.
- [`G-Rebels - Single Shaker.siprofile`](G-Rebels%20-%20Single%20Shaker.siprofile) - Pre-configured ShakeIt profile for single bass shaker setups.
- [`G-Rebels Tactical HUD.simhubdash`](G-Rebels%20Tactical%20HUD.simhubdash) - Demonstration dashboard showcasing full 6-DOF spacecraft combat telemetry and Engine Boost ('C') control.
- [`DASHBOARD_GUIDE.md`](DASHBOARD_GUIDE.md) - Complete user guide for importing and operating the demonstration dashboard.

## 11. Using GRebels Properties in SimHub

1. Create or import a SimHub profile for G-Rebels (or import `docs/G-Rebels - Single Shaker.siprofile`).
2. To test telemetry visually or control the game via touch/mouse, double-click `docs/G-Rebels Tactical HUD.simhubdash` to import the demonstration dashboard (see [`DASHBOARD_GUIDE.md`](DASHBOARD_GUIDE.md) for full instructions).
3. Add or inspect ShakeIt custom effects.
4. Select the device and open the property picker.
5. Search for `GRebels.` and choose the required property.
6. Start with conservative gains, especially for the unverified acceleration
   and collision channels.
