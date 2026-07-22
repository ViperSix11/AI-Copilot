# Repository instructions

Before changing code, read `docs/papa-bear-v1/README.md` and the milestone document named in the task.

## Non-negotiable rules

- Treat the documents under `docs/papa-bear-v1/` as the authoritative product specification.
- Do not implement work from later milestones unless the task explicitly authorizes it.
- Preserve the perspective-bound fair-play model: no hidden enemy state, no unrestricted server-world export and no anti-cheat bypass.
- OpenAI may select typed tools, but it must never execute arbitrary SQF, C++, PowerShell or operating-system commands.
- Validate every model-generated tool argument locally before it reaches Arma.
- Ballistic answers must come from a deterministic ACE-aware service, never from language-model estimation.
- Prefer documented Arma, CBA and ACE3 public interfaces. Isolate unavoidable internal ACE access behind a versioned adapter.
- Keep API keys encrypted locally and never log keys, full prompts, private telemetry or spoken conversation content.
- Keep changes reviewable: one milestone per branch/PR, focused tests, green Windows CI and an explicit manual Arma test plan.

## Current implementation stack

- Windows 10/11 x64
- C# / .NET 8 / WPF application
- C++ x64 Arma extension
- SQF client addon
- Duplex Windows Named Pipe
- OpenAI Responses API
- OpenAI audio transcription and Responses API, with ElevenLabs TTS
