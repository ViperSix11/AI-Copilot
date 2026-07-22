# ArmA AI Bridge

ArmA AI Bridge is a Windows-side tactical assistant for **Arma 3**. Version `0.3.0` adds a text assistant to the verified v0.2 telemetry and dynamic map-query bridge.

```text
Question -> OpenAI Responses API -> optional local read tool
         -> world model or Arma map query -> tool result -> answer
```

## Version 0.3.0

- New **Assistant** tab, inserted without changing the proven v0.2 dashboard or map-query code
- OpenAI Responses API integration using the saved DPAPI-encrypted API key
- Default model `gpt-5-mini`, editable in the Assistant tab
- Strict `query_environment` function tool
- Automatic circle/cone selection, direction, range and categories
- Up to three local tool rounds per question
- Short in-memory follow-up history
- `store: false` on Responses requests
- Privacy-minimized world snapshots: player name, UID, group label and raw object IDs are excluded
- Local provenance-aware world state with session-scoped entity aliases,
  freshness, confidence and mission/map reset handling
- Read-only **World State** diagnostics and purpose-specific OpenAI snapshots
- Session handshake, own-side force deltas, and periodic full reconciliation
- Privacy-safe friendly group, unit, vehicle, and support-asset aliases
- Typed mission capability registry and three local read-only force tools
- Questions and answers are not written to the application log
- Existing manual map query and stateless OpenAI tool behavior remain available

## Use

1. Enable the freshly packaged `@Arma_AI_Bridge` mod.
2. Start `ArmA AI Bridge.exe` and an Arma mission.
3. Save the OpenAI API key under **API keys**.
4. Open **Assistant** and ask a question.

Examples:

```text
Welche Karte ist geladen und in welche Richtung schaue ich?
Gibt es Gebäude in dem Wald vor mir?
Welche Deckungsmöglichkeiten gibt es in meiner Nähe?
```

The model receives four locally validated tools:

```text
query_environment(shape, direction, rangeMeters, angleDegrees, categories, maxResultsPerCategory)
query_friendly_forces(entityType, maxDistanceMeters, includeStale, limit)
query_assets(kind, availableOnly, maxDistanceMeters, includeStale, limit)
query_mission_capabilities(enabledOnly, includeStale)
```

Terrain scans remain request-driven. There are no fixed distance probes.

## Privacy

Raw telemetry is ingested into a local world model and is not forwarded directly
to OpenAI. OpenAI receives a purpose-specific snapshot containing relevant map,
player-state, vehicle, known-contact, friendly-force summary, and mission
capability summary facts with provenance, freshness and uncertainty. Detailed
force data is added only through a bounded local read tool. Player names,
UIDs, raw group labels, raw engine IDs, and source mission/session IDs are not
included.
API requests use `store: false`; this is not the same as Zero Data Retention for
normal abuse-monitoring data.

## Build

```powershell
Set-ExecutionPolicy -Scope Process Bypass
./scripts/build.ps1
```

Milestone 3 rebuilds the Arma addon and native transport. The full protocol,
privacy policy, and live WEST acceptance mission are specified in
[`docs/papa-bear-v1/codex-milestone-3.md`](docs/papa-bear-v1/codex-milestone-3.md).
