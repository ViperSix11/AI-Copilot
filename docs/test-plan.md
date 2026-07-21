# Test plan

## Stage 1 — inbound telemetry without Arma

1. Start `ArmA AI Bridge.exe`.
2. Verify status `Listening`.
3. Run `scripts/send-test-telemetry.ps1`.
4. Verify map, position, heading, contacts, raw JSON and log output.

## Stage 2 — package and load the Arma addon

1. Run `scripts/build.ps1` or `scripts/package-mod.ps1`.
2. Confirm `arma_ai_bridge_client.pbo` exists under `artifacts/mod/@Arma_AI_Bridge/addons`.
3. Copy `@Arma_AI_Bridge` into the Arma 3 installation directory.
4. Enable the mod in the launcher.
5. Start the Windows application, then an Editor mission.
6. Verify `Arma connected` and live telemetry.

## Stage 3 — duplex query smoke test

1. Open the `Map query` tab.
2. Send a 300 m circle query for buildings.
3. Verify a matching `requestId` appears in the result and log.
4. Send an 800 m, 40 degree view cone for buildings and vegetation.
5. Rotate the player and repeat; bearings and results must change.
6. Select roads, walls and rocks individually and verify valid result envelopes.
7. Enter invalid ranges and limits; the GUI must reject them.

## Stage 4 — resilience

- Close and reopen the Windows application while a mission is running.
- Restart the mission and respawn the player.
- Send repeated queries and confirm no stale `requestId` is reused.
- Confirm telemetry remains responsive during a query.
- Inspect the Arma RPT and application log for errors.

## Acceptance gate for AI integration

Proceed to OpenAI only after:

- telemetry runs for 30 minutes without disconnect loops
- at least 50 dynamic queries complete successfully
- C# and native CI builds are green
- no fixed environment probe data appears in telemetry
- query limits prevent unbounded scans
