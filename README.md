# ArmA AI Bridge — Papa Bear

ArmA AI Bridge is a Windows companion for **Arma 3**. A perspective-bound SQF
addon exports selected game state through a native x64 extension and a local
Named Pipe. The WPF application maintains a privacy-minimized world model and
lets **Papa Bear** answer typed or push-to-talk questions about the current
mission.

**Current release: 0.8** (`0.8.0` assembly version).

Version 0.8 adds a local SQLite Unified State Mirror and deterministic
contextual interpretation to the live-accepted voice proof of concept. A player can ask
by microphone, for example, “Papa Bear, welche Position habe ich?”, and receive
a grounded text answer plus spoken output using the configured ElevenLabs
voice. The desktop shell now uses the visible title **ArmA AI Bridge - Papa Bear**
and a higher-contrast tactical terminal theme with larger conversation,
diagnostics and log text for long-session readability.

## Product status

The current release is deliberately narrow. It proves the complete vertical
product chain before broader tactical awareness is added:

```text
Arma 3 live state
  -> SQF client addon
  -> arma_ai_bridge_x64.dll
  -> local Named Pipe
  -> privacy-minimized world model

Typed question -----------------------------┐
                                            ├-> shared AssistantTurnService
Push-to-talk -> WAV -> OpenAI transcription ┘   -> OpenAI Responses
                                                -> optional validated local tools
                                                -> visible answer
                                                -> ElevenLabs TTS
                                                -> Windows audio output
```

Typed and spoken turns use the same reasoning path and a fresh world-state
snapshot. Voice is an interface layer; it does not own game state or maintain a
separate conversation model.

## Implemented capabilities

### Arma bridge

- one bounded `state-snapshot-v2` envelope every four seconds, backed by
  independently sampled 1/2/4/8-second SQF section caches;
- loaded world and map size;
- current map grid and player position;
- body and view heading, speed, stance, damage and life state;
- current weapon, magazine and ammunition state;
- current vehicle, role, fuel, damage and speed;
- side-wide contacts already known to local own-side representatives, including
  estimated position and uncertainty but never hidden hostile truth;
- mission/session handshake;
- own-side groups, units and crewed vehicles with delta updates and periodic
  reconciliation;
- bounded request-driven environment queries.
- one bounded, config-only export of official `CfgWorlds` named locations per
  mission session; no terrain-object scan or static map index.

### Local application

- duplex Named Pipe transport;
- provenance-aware world model with freshness, uncertainty and session reset
  handling;
- privacy-safe local aliases for entities;
- World State diagnostics;
- encrypted API settings using Windows DPAPI;
- stateless OpenAI Responses tool loop;
- typed assistant conversation;
- 15-second push-to-talk microphone capture;
- isolated microphone, transcription and voice-output tests;
- OpenAI completed-utterance transcription;
- ElevenLabs speech synthesis;
- replay, cancellation and partial-success handling.
- in-memory named-location gazetteer with atomic paged assembly;
- mission-scoped SQLite current-state mirror with transactional snapshot ingest,
  reconciliation metadata, staleness and explicit cache reset;
- deterministic weather, loadout, force and contact summaries plus bilingual
  question-aware context selection;
- deterministic position containment, distance, bearing, cardinal direction,
  salience ranking and current/last-known interpretation;
- local response profiles with deterministic final-text terminators.
- deterministic Vanilla-config firing solutions when ACE Advanced Ballistics is
  inactive, plus a version-gated ACE3 3.21.x runtime adapter when it is active;
- conditional five-second English radio acknowledgements and deterministic
  speech-safe number/unit normalization;
- configurable background press-and-hold push-to-talk, default Shift + Space,
  using verified Windows Raw Input without hooks, injection or key suppression;
- versioned local ballistic profiles with strict class matching, validation and
  compact deterministic firing-solution results.

### Local read tools

The current assistant may use only validated read-only tools:

```text
query_friendly_forces(entityType, maxDistanceMeters, includeStale, limit)
query_assets(kind, availableOnly, maxDistanceMeters, includeStale, limit)
query_mission_capabilities(enabledOnly, includeStale)
find_named_locations(query, maxDistanceMeters, limit)
query_state(section, includeStale, limit)
calculate_firing_solution(rangeMeters, bearingDegrees, targetElevationAslMeters, targetHeightAboveTerrainMeters)
```

No tool can execute arbitrary SQF, C++, PowerShell or operating-system commands.
Mission-declared assets and capabilities are read-only in this release.

## Provider configuration

Open the **API keys** tab and save:

1. **OpenAI API key** — used for both audio transcription and Responses
   reasoning;
2. **ElevenLabs API key** — used only for Papa Bear speech output;
3. **ElevenLabs voice ID** — selects Papa Bear’s voice.

No separate speech-to-text account is required. OpenAI TTS is not used.
Credentials are encrypted for the current Windows user and are never written to
application logs.

## Installation

A matching artifact contains:

```text
app/
  ArmA AI Bridge.exe
  ArmA AI Bridge.dll
  NAudio and runtime dependencies

mod/
  @Arma_AI_Bridge/
    addons/arma_ai_bridge_client.pbo
    arma_ai_bridge_x64.dll
    mod.cpp
```

1. Copy the complete `app` and `mod` folders from the same build.
2. Add `mod/@Arma_AI_Bridge` as a local mod in the Arma 3 Launcher.
3. Start `app/ArmA AI Bridge.exe`.
4. Start an Arma mission with the mod enabled.
5. Confirm that the application reports **Arma connected** and receives current
   telemetry.

Do not mix the EXE, DLL and PBO from different builds.

## Contextual position acceptance

The following sequence was accepted live for version 0.7. Version 0.8 retains
it and adds the exact Stratis and alternate-map checks in
`docs/papa-bear-v1/codex-milestone-5-contextual-interpreter.md`:

1. Start any Arma mission and verify map, position and grid telemetry.
2. Select **Test Papa Bear Voice** and hear:
   “Papa Bear online. Radio check complete.”
3. Hold **Test Microphone**, speak, release and hear the local recording.
4. Hold **Test Transcription**, speak and receive exactly one OpenAI transcript
   without invoking Responses or ElevenLabs.
5. Hold **Hold to Talk** and ask:
   “Papa Bear, welche Position habe ich?”
6. Confirm that the transcript appears once.
7. Confirm that the answer contains the current map and correct grid or
   coordinates.
8. Confirm that ElevenLabs speaks the complete displayed answer once.
9. Ask the same question by keyboard and verify the position agrees.

The active voice stages are:

```text
ready -> recording -> transcribing -> thinking
      -> generating-voice -> speaking -> ready
```

The transcript appears immediately after transcription. The text answer appears
immediately after Responses. If ElevenLabs synthesis or Windows playback fails,
the completed transcript and answer remain visible. **Replay Last Answer**
retries only the missing speech stage; it does not repeat transcription or
reasoning.

## Privacy and fair play

- The addon is perspective-bound and does not export an unrestricted server
  world.
- Hidden enemy truth is not deliberately exposed to the assistant.
- Raw 4 Hz telemetry is normalized locally rather than forwarded wholesale.
- OpenAI receives a purpose-specific snapshot and only bounded selected tool
  results.
- Player profile names, UIDs, raw engine IDs and source mission/session IDs are
  excluded from OpenAI context.
- Microphone audio leaves the PC only after an explicit transcription or
  push-to-talk action.
- ElevenLabs receives only the final answer or the fixed voice-test phrase.
- Audio, transcripts, questions, answers, prompts, snapshots, tool results,
  provider response bodies, API keys and voice IDs are not logged.
- Temporary WAV recordings are bounded and deleted after success, failure or
  cancellation.
- OpenAI Responses requests use `store: false`; this is not equivalent to a
  provider-wide Zero Data Retention agreement.

## Current limitations

Version 0.8 does not provide:

- always-on listening, wake words or VAD;
- streaming transcription or streaming speech output;
- microphone/output-device selection in the UI;
- proactive military contact reports;
- player-reported observations or persistent operational memory;
- persistent, runtime-object or full-static-map indexing;
- perception of empty vehicles and other non-contact objects;
- ACE versions outside the documented 3.21.x adapter baseline, powered/guided
  projectiles, or optic-click calculation;
- route planning, landing-zone scoring or support execution;
- hardened multiplayer packaging, signatures or installer/updater support.

These are follow-on capabilities, not dependencies of the accepted MVP.

## Roadmap status

Release 0.8 Unified State Mirror & Interpreter is the only active product
milestone. The
following older proposals are retained as historical context only; none is an
active dependency or authorized implementation scope.

## Historical roadmap proposals

### M4B — Arma knowledge mirror

Read Arma’s existing own-side target knowledge across friendly groups. Do not
build a parallel perception simulation. First target: a remote friendly unit
recognizes an enemy and Papa Bear receives the same engine-provided knowledge.

### M4C — Military contact reporting

Add official named-location resolution and one deduplicated proactive contact
message using the existing ElevenLabs output path.

### M5 — Player reports and selected physical objects

Allow explicit spoken player reports and evaluate narrowly scoped support for
empty vehicles, supplies and tactical objects only where Arma’s knowledge model
does not already provide the required information.

### Later backlog

ACE ballistic integration, validated support actions, route planning and
multiplayer packaging remain deferred until the narrow tactical POC is stable.

## Build and test on Windows

Prerequisites:

- Windows 10/11 x64;
- .NET 8 SDK;
- CMake with a Visual Studio x64 toolchain;
- Python 3;
- optionally Arma 3 Tools for official AddonBuilder PBO output.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
py -3 scripts/verify_repository.py
dotnet test tests/ArmaAiBridge.App.Tests/ArmaAiBridge.App.Tests.csproj -c Release
./scripts/build.ps1
```

Without AddonBuilder, CI uses the repository’s deterministic uncompressed
development-PBO packer. Every release artifact must contain the matching WPF
application, native DLL, `mod.cpp` and PBO.

## Version history

- **0.1.0** — project foundation and one-way player telemetry prototype.
- **0.2.0** — duplex Named Pipe and bounded manual environment queries.
- **0.3.0** — stateless OpenAI text assistant and repaired multi-round tool
  loop.
- **0.4.0** — local provenance-aware world model and minimized OpenAI snapshots.
- **0.5.0** — mission/session handshake, friendly-force picture, diagnostics,
  read-only asset/capability registry and force tools.
- **0.6.0** — initial push-to-talk implementation, shared typed/spoken turn path,
  ElevenLabs output, replay and voice diagnostics; development candidate.
- **0.7** — live-accepted Voice Position MVP, OpenAI audio transcription,
  ElevenLabs-only speech output, partial-success preservation, clean provider
  setup and verified matching Windows artifact.

- **0.8** — bounded official named-location gazetteer, transactional SQLite
  current-state mirror, deterministic contextual interpretation, strict
  `query_state`, bounded location lookup, local response profiles,
  Vanilla-config firing solutions, conditional acknowledgements, speech-safe
  English, global PTT and a high-contrast Papa Bear tactical desktop theme.

Detailed architectural records and milestone acceptance specifications are under
[`docs/papa-bear-v1`](docs/papa-bear-v1/).
