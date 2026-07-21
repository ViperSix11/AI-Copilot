# ArmA AI Bridge

ArmA AI Bridge is a Windows-side tactical assistant for **Arma 3**. Version `0.3.0` adds a text assistant to the verified v0.2 telemetry and dynamic map-query bridge.

```text
Question -> OpenAI Responses API -> optional query_environment tool
         -> local Arma map query -> tool result -> natural-language answer
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
- Privacy-minimized telemetry: player name, UID, group ID and object IDs are excluded
- Questions and answers are not written to the application log
- Existing v0.2 PBO and DLL remain protocol-compatible; only the Windows application changes

## Use

1. Keep the existing `@Arma_AI_Bridge` mod enabled.
2. Start `ArmA AI Bridge.exe` and an Arma mission.
3. Save the OpenAI API key under **API keys**.
4. Open **Assistant** and ask a question.

Examples:

```text
Welche Karte ist geladen und in welche Richtung schaue ich?
Gibt es Gebäude in dem Wald vor mir?
Welche Deckungsmöglichkeiten gibt es in meiner Nähe?
```

The model receives one locally validated tool:

```text
query_environment(shape, direction, rangeMeters, angleDegrees, categories, maxResultsPerCategory)
```

Terrain scans remain request-driven. There are no fixed distance probes.

## Privacy

Only a reduced telemetry copy is sent to OpenAI. It contains relevant map, player-state, vehicle, known-contact and sensor fields, but no player name, player UID, group ID, contact ID or sensor ID. API requests use `store: false`; this is not the same as Zero Data Retention for normal abuse-monitoring data.

## Build

```powershell
Set-ExecutionPolicy -Scope Process Bypass
./scripts/build.ps1
```

The Arma addon does not need to be repacked for v0.3.0. Version `0.4.0` will add push-to-talk, speech recognition and ElevenLabs output after the text path is stable.
