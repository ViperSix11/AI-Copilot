# Text assistant test plan

1. Regression: verify v0.2 telemetry, manual circle query and manual cone query.
2. Direct answer: ask `Welche Karte ist geladen und in welche Richtung schaue ich?`; expect zero tool calls.
3. Tool answer: ask `Gibt es Gebäude in dem Wald vor mir?`; expect a cone query with buildings and vegetation before the final answer.
4. Vicinity: ask `Welche Deckungsmöglichkeiten gibt es in meiner Nähe?`; expect a circle query.
5. Explicit range: ask for buildings within 600 metres; expect approximately 600 metres, not a fixed preset.
6. Follow-up: ask a second question referring to the first answer; expect short in-memory continuity.
7. Cancellation: cancel while the API or Arma query is pending; UI must recover.
8. Failure: test missing key, invalid model, disconnected Arma and query timeout.
9. Privacy: inspect a debug-captured request and confirm no player name, UID, group ID or object IDs.
10. Stability: run 20 mixed direct/tool questions without pipe disconnect, UI freeze or unbounded memory growth.

Voice integration is blocked until these stages pass.
