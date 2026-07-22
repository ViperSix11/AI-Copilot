# ArmA AI Bridge — Papa Bear

ArmA AI Bridge is a Windows companion for **Arma 3**. It connects a
perspective-bound client addon to a local WPF application, maintains a
privacy-minimized world state, and lets Papa Bear answer typed or push-to-talk
questions through the OpenAI Responses API.

Version `0.6.0` is the Milestone 4A voice-position MVP. It adds explicit,
15-second push-to-talk capture, AssemblyAI transcription, and ElevenLabs
playback without changing Arma perception or introducing a separate voice
reasoning path.

## What is implemented

The current bridge provides:

- 4 Hz local player telemetry, including loaded world, map grid, position,
  headings, movement, current vehicle, and perspective-bound known contacts;
- an explicit mission/session handshake;
- own-side friendly groups, units, and crewed vehicles via deltas plus periodic
  full reconciliation;
- a typed, read-only registry for mission-declared assets and capabilities;
- a local provenance-aware world model with freshness, confidence, uncertainty,
  session-scoped aliases, and reset handling;
- a text Assistant using stateless OpenAI Responses requests and strict local
  read tools;
- manual request-driven Arma environment queries;
- push-to-talk questions and spoken Papa Bear answers;
- read-only World State diagnostics.

Mission-declared support assets and capabilities have automated coverage but
have not completed live acceptance. They remain read-only and are not expanded
by Milestone 4A.

## Data and voice flow

```text
Arma SQF addon
  -> x64 transport DLL
  -> duplex Windows Named Pipe
  -> local world model
  -> purpose-specific OpenAI snapshot

Typed question --------------------------┐
                                        ├-> shared assistant turn
Hold-to-talk -> WAV -> AssemblyAI text --┘   -> optional local read tools
                                             -> final answer
                                             -> ElevenLabs MP3
                                             -> default Windows audio device
```

Typed and spoken questions call the same `SubmitUserTurnAsync` path. Every turn
builds a fresh current-situation snapshot before OpenAI is called. A spoken
position question therefore uses the current map, grid, position, freshness,
and confidence already supplied by the accepted telemetry path.

The model may select only these locally validated read tools:

```text
query_environment(shape, direction, rangeMeters, angleDegrees, categories, maxResultsPerCategory)
query_friendly_forces(entityType, maxDistanceMeters, includeStale, limit)
query_assets(kind, availableOnly, maxDistanceMeters, includeStale, limit)
query_mission_capabilities(enabledOnly, includeStale)
```

`query_environment` crosses the Named Pipe as a bounded Arma query. The three
friendly-force tools read the local world model. No tool executes support or
arbitrary SQF, C++, PowerShell, or operating-system commands.

## Provider setup

Open the **API keys** tab. Settings are encrypted with Windows DPAPI for the
current Windows user.

1. Save an **OpenAI API key** for typed and spoken assistant reasoning. The
   default model is `gpt-5-mini` and can be changed in the Assistant tab.
2. Save an **AssemblyAI API key** for completed-utterance transcription.
3. Save an **ElevenLabs API key** and **voice ID** for Papa Bear speech.

The application never writes credential values to its logs.

## Run and verify

1. Build or download one matching development artifact.
2. Enable its `@Arma_AI_Bridge` mod in Arma 3.
3. Start `ArmA AI Bridge.exe`, then start an Arma mission.
4. Confirm **World State** shows a live session and current player position.
5. Save all three provider credentials.
6. In **Assistant**, type a position question and confirm a grounded text
   answer.
7. Press and hold **Test Microphone**, speak briefly, release, and confirm local
   playback. This test uses no network provider.
8. Press and hold **Test Transcription**, say “Papa Bear, welche Position habe
   ich?”, release, and confirm one AssemblyAI transcript with no assistant
   answer.
9. Select **Test Papa Bear Voice** and hear “Papa Bear online. Radio check
   complete.”
10. Press and hold **Hold to Talk**, ask the German position question, and
    release. Confirm the stages progress through recording, transcribing,
    thinking, generating voice, and speaking; the displayed and spoken answer
    must match the current map/grid/position.
11. Move in Arma and repeat to prove the next voice turn uses a fresh snapshot.

The final transcript appears as soon as AssemblyAI succeeds, and the text
answer appears as soon as OpenAI succeeds. A later speech failure leaves both
visible and reports partial success. **Replay Last Answer** reuses retained
audio after a playback failure or retries synthesis from the completed text
answer without repeating AssemblyAI or OpenAI. **Cancel** stops recording,
transcription, OpenAI/tool work, synthesis, or playback. Audio capture stops
automatically at 15 seconds, and overlapping operations are rejected.

The complete live gate, including credential/network failures and retention
checks, is in
[`docs/papa-bear-v1/codex-milestone-4a-voice-position-mvp.md`](docs/papa-bear-v1/codex-milestone-4a-voice-position-mvp.md).

## Privacy and fair play

- Raw telemetry is reduced locally; it is not forwarded wholesale to OpenAI.
- OpenAI receives the current purpose-specific snapshot and only selected,
  bounded tool results.
- Player profile names, UIDs, raw engine IDs, and source mission/session IDs do
  not enter OpenAI context.
- Enemy facts remain limited to information already available to the local
  player/group/current vehicle sensors. The bridge does not export hidden enemy
  state or an unrestricted server world.
- Microphone audio leaves the PC only after explicit Test Transcription or
  Hold-to-Talk capture, and only for AssemblyAI.
- AssemblyAI's final transcript reaches OpenAI only for a real spoken assistant
  turn. ElevenLabs receives only the final answer or fixed voice-test phrase.
- Audio, transcripts, questions, answers, full prompts, snapshots, tool results,
  API keys, provider identifiers, and provider response bodies are not logged.
- Microphone WAV files are bounded and deleted after every success, failure, or
  cancellation. Synthesized answers are kept only in memory for replay.
- OpenAI requests use `store: false`. This does not itself mean Zero Data
  Retention for provider-side abuse monitoring or other provider policies.

## Current limitations

Version 0.6.0 does **not** implement always-on listening, wake words, VAD,
streaming speech, global hotkeys, operational memory, SQLite, map gazetteers,
full static map indexing, side-wide enemy/contact collection, perception of
empty objects, player-report memory, proactive alerts, voice-triggered support,
routes, waypoints, ACE, or ballistics.

The bridge knows only accepted Milestones 1–3 telemetry and current
mission-declared read-only data. Mission capability declarations do not grant
an execution tool. Voice transcription quality, audio-device compatibility,
provider quotas, and the selected ElevenLabs voice require live verification.

Draft PR #10 remains unmerged experimental observational-memory work pending a
post-MVP architecture review; version 0.6.0 does not include it.

## Build and test on Windows

Prerequisites are Windows 10/11 x64, .NET 8 SDK, CMake with a Visual Studio x64
toolchain, Python, and optionally Arma 3 Tools for Bohemia AddonBuilder output.
Without AddonBuilder, the build uses the repository's deterministic uncompressed
development-PBO packer.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
py -3 scripts/verify_repository.py
dotnet test tests/ArmaAiBridge.App.Tests/ArmaAiBridge.App.Tests.csproj -c Release
./scripts/build.ps1
```

The produced `artifacts` tree contains the win-x64 WPF application and a
matching `@Arma_AI_Bridge` mod with `mod.cpp`, `arma_ai_bridge_x64.dll`, and
`addons/arma_ai_bridge_client.pbo`. GitHub Actions builds and verifies the same
development artifact on Windows.

## Release history

- `0.1.0`: project foundation and one-way telemetry prototype.
- `0.2.0`: duplex Named Pipe and bounded manual environment query.
- `0.3.0`: stateless OpenAI text assistant and repaired multi-round tool loop.
- `0.4.0`: local provenance-aware world model and minimized snapshots.
- `0.5.0`: mission/session handshake, friendly-force picture, capability
  registry, diagnostics, and read-only force tools.
- `0.6.0`: push-to-talk microphone capture, AssemblyAI completed transcription,
  shared typed/spoken turn path, ElevenLabs speech, replay, cancellation, and
  voice tests.
