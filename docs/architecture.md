# Architecture

ArmA AI Bridge remains perspective-bound. The v0.2 Arma addon and native DLL continue to provide lightweight telemetry and explicit `query_environment` commands over the duplex Named Pipe `ArmaAiBridge.Arma3.Telemetry`.

Version 0.3 adds four Windows-side components:

- `AssistantPanel`: text conversation UI, model field, cancellation and status
- `MainWindow.Assistant`: injects the Assistant tab while leaving the proven v0.2 window code unchanged
- `ArmaQueryCoordinator`: caches the latest telemetry, validates tool arguments, sends commands and correlates results by `requestId`
- `OpenAiAssistantService`: calls `POST /v1/responses`, executes strict function calls locally and returns `function_call_output`

Every OpenAI tool argument is validated again locally. Range is limited to 25–1,500 metres, result count to 1–50 per category, and categories to building, vegetation, road, wall and rock. Queries time out after 12 seconds.

Before an API request, telemetry is rebuilt from an allowlist. Player name, UID, group ID and Arma object/network IDs are omitted. Conversation history exists only in memory. Questions and answers are not logged. Requests set `store: false`.
