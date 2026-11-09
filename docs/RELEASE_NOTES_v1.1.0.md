# G-Rebels SimHub Telemetry — Release v1.1.0

## Highlights & What's Changed
- **Official Telemetry Protocol Alignment**: Complete migration from reverse-engineered offsets to the official memory specification published by Reakktor (29 total telemetry channels).
- **Active Metadata Strings**: Real-time decoding and display of `VehicleName` (Active Craft), `Location` (Sector / Theater), and `Game`.
- **Dynamic Packet Sizing**: Zero-error ingestion of both current 236-byte packets and future 264-byte expanded packets.
- **Dashboard Enhancements (`G-Rebels Tactical HUD`)**:
  - Centered tactical header strip (`[ CRAFT: ... // SECTOR ... ]`) with automatic Unreal Engine actor name sanitization.
  - Forward Thrust vertical sidebar (`FWD_Thrust_Bar`) with dynamic Cyan (Forward) / Amber (Reverse) bidirectional indication.
  - Calibrated 3.0G Inertial G-Force Radar with vector clamping (`Math.min(dist, 128px)`).
  - Cleaned UDP link diagnostic matrix with 21-character monospace alignment across all 8 rows.
  - Removed full-width red title bar flash overlay on damage impacts.
- **Tactile / ShakeIt Haptic Profile**:
  - Added dedicated Boost / Afterburner shaker vibration container (48 Hz rumble).
  - Retuned cannon, missile, and hull collision effect channels.

---

## Telemetry Compatibility Notice (236-Byte vs 264-Byte)
In the current shipping build of *G-Rebels*, the UDP stream dispatches 236 bytes even when set to Expanded mode. The plugin safely consumes this stream and gracefully defaults the 7 missing Expanded fields to zero:
- `ImpactDirX`, `ImpactDirY`, `ImpactDirZ` (Collision direction uses kinematic delta-V fallback)
- `BoostActive`, `BoostLevel`, `BoostPercent`
- `Hull`, `HullPercent`
- `Shield`, `ShieldPercent`

When Reakktor enables the 264-byte dispatch in an upcoming game update, the plugin will ingest all 7 fields automatically without requiring a plugin update.

---

## ⚠️ Breaking Changes & Property Remappings (vs v1.0.3)
If you built custom SimHub dashboards, overlays, ShakeIt haptic profiles, or Motion profiles against v1.0.3, update your property references as follows:

| v1.0.3 Property | New Property (v1.1+) | Change Details |
|---|---|---|
| `GRebels.SpeedRaw` | `GRebels.Speed` | `SpeedRaw` removed; `Speed` now provides the direct velocity scalar. |
| `GRebels.LocalSurgeMs2` | `GRebels.LongitudinalAcc` | `LocalSurgeMs2` removed. Raw acceleration is now exposed without the prior `0.05` scaling multiplier. |
| `GRebels.LocalSwayMs2` | `GRebels.LateralAcc` | `LocalSwayMs2` removed; artificial scaling removed. |
| `GRebels.LocalHeaveMs2` | `GRebels.VerticalAcc` | `LocalHeaveMs2` removed; artificial scaling removed. |
| `GRebels.Expanded.Float07` | `GRebels.LateralVelocity` | Mapped to official lateral velocity vector (cm/s). |
| `GRebels.Expanded.Float08` | `GRebels.VerticalVelocity` | Mapped to official vertical velocity vector (cm/s). |
| `GRebels.Expanded.Float09` | `GRebels.LongitudinalVelocity` | Mapped to official longitudinal velocity vector (cm/s). |
| `GRebels.InputForward`, `Input.Forward` | `GRebels.ThrustMain` | Raw 3-axis main thrust (`-1.0 .. +1.0`). Note: `Throttle` and `ThrottlePercent` remain available. |
| `GRebels.InputVertical`, `ThrottleVertical` | `GRebels.ThrustVertical` | Raw vertical thruster axis (`-1.0 .. +1.0`). |
| `GRebels.InputLateral`, `ThrottleLateral` | `GRebels.ThrustLateral` | Raw lateral thruster axis (`-1.0 .. +1.0`). |
| `GRebels.Pitch` (inverted) | `GRebels.Pitch` (un-inverted) | Artificial `-1.0` inversion removed; now matches true game pitch angle. |

---

## 🛠️ Targeted Upgrade Actions (Upgrading from v1.0.3)

You do **not** need to reconfigure your game network or IP settings. However, based on customizations you may have made in v1.0.3, perform the following application-side updates:

### 1. Replace Both Plugin Files
- Copy **both** `SimHub.GRebels.dll` and `SimHub.GRebels.json` to your SimHub installation folder (`C:\Program Files (x86)\SimHub\`). 
- *Do not keep your previous `SimHub.GRebels.json`*; the schema has been updated to support the new field mappings, dynamic sizing, and string offsets.

### 2. SimHub Motion Profiles (Motion Platforms / Actuators)
If you configured a 2DOF, 3DOF, or 6DOF motion rig profile in SimHub for *G-Rebels*:
- **Remap Surge, Sway, & Heave**:
  - Remap Surge from `LocalSurgeMs2` to **`GRebelsTelemetryPlugin.GRebels.LongitudinalAcc`**.
  - Remap Sway from `LocalSwayMs2` to **`GRebelsTelemetryPlugin.GRebels.LateralAcc`**.
  - Remap Heave from `LocalHeaveMs2` to **`GRebelsTelemetryPlugin.GRebels.VerticalAcc`**.
- **Recalibrate Gain & Max Limits**:
  - v1.0.3 applied an internal `0.05` scaling multiplier to `LocalSurgeMs2`, `LocalSwayMs2`, and `LocalHeaveMs2`. v1.1.0 exposes direct raw acceleration values (approx. 20x larger numerical values).
  - **Recalibrate your Motion input limits / scale multipliers in SimHub before driving** to ensure actuators do not saturate or move aggressively.
- **Check Pitch Orientation**:
  - v1.0.3 had an artificial `-1.0` pitch inversion. If you checked "Invert" on your Pitch motion axis in SimHub to counteract this, uncheck it so your rig pitches up when pulling up.

### 3. ShakeIt Bass Shaker Profiles
- If you have custom tactile effects bound to `LocalSurgeMs2` (such as acceleration kick or engine surge):
  - Change the binding to **`GRebelsTelemetryPlugin.GRebels.LongitudinalAcc`**.
  - Adjust effect gain / threshold to match the unscaled acceleration range.
- (Optional) Import the updated `docs/G-Rebels - Single Shaker.siprofile` to enable the new dedicated 48 Hz **Boost** vibration effect (`GRebels.BoostActive`).

### 4. Dashboards & Overlays
- Import the updated `docs/G-Rebels Tactical HUD.simhubdash`. 
- If you built custom dashboards or overlays in v1.0.3:
  - Remap `SpeedRaw` -> `Speed`.
  - Remap `InputForward` -> `ThrustMain` (or `ThrottlePercent`).
  - Remap `Expanded.Float07..09` -> `LateralVelocity`, `VerticalVelocity`, `LongitudinalVelocity`.
  - Remove any custom JavaScript or NCalc formulas that manually inverted `Pitch`.
