# Staged test plan

Do not enable cloud AI until all local bridge stages pass.

## Stage 1 — GUI and local pipe

1. Build and start `ArmA AI Bridge.exe`.
2. Confirm the status changes to `Listening`.
3. Run `scripts/send-test-telemetry.ps1`.
4. Confirm:
   - status changes to `Arma connected`
   - map is `VR`
   - position is displayed
   - three front probes are displayed
   - the raw JSON is formatted
   - a log file appears under `%LOCALAPPDATA%\ArmA AI Bridge\logs`

Pass condition: ten repeated test sends cause no crash and reconnect cleanly.

## Stage 2 — Native extension load

1. Build `arma_ai_bridge_x64.dll`.
2. Install the development mod.
3. Start ArmA AI Bridge before Arma.
4. Launch a local Editor mission with the mod enabled.
5. Inspect the Arma RPT for:

```text
CallExtension loaded: arma_ai_bridge
[ArmA AI Bridge] Client telemetry starting. Bridge response: pong
```

Pass condition: GUI reports `Arma connected` and receives snapshots for five minutes.

## Stage 3 — Player telemetry

Validate on foot and in a vehicle:

- position changes smoothly
- view heading follows freelook rather than only body direction
- weapon, muzzle, magazine and loaded rounds are plausible
- vehicle class, fuel, damage and role appear only when in a vehicle

Pass condition: no SQF errors in RPT and no malformed JSON warnings in the GUI log.

## Stage 4 — Environment probes

Use Altis or Stratis rather than Virtual Reality because VR has no terrain objects.

Test these scenes:

1. open field facing open terrain
2. dense forest without buildings
3. forest with a nearby house or ruin
4. town edge
5. rotate view 180 degrees without moving

Pass condition: probe counts change with view direction; `buildingsInVegetation` becomes true only where both conditions are present.

The `forestLikely` threshold is intentionally heuristic and will be calibrated from observed logs.

## Stage 5 — Perception contacts

1. Place an enemy behind terrain and ensure it has not been detected.
2. Confirm it does not appear in `contacts`.
3. Allow the player or group AI to detect it.
4. Confirm it appears with estimated position and error margin.
5. Break contact and confirm the age values increase.

Pass condition: the exported contact does not contain a separate exact hidden position.

## Stage 6 — Stability gate

Run a local multiplayer scenario for at least 30 minutes.

Monitor:

- Arma FPS before and after enabling the mod
- RPT errors
- bridge queue `dropped` status
- GUI memory usage
- reconnect after closing and reopening ArmA AI Bridge

Only after this gate passes should version 0.2 connect OpenAI and ElevenLabs.
