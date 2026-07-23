# ArmA AI Bridge — Papa Bear

ArmA AI Bridge is a local Windows companion for **Arma 3**. Its radio
assistant, **Papa Bear**, answers typed and spoken questions using a
perspective-bound picture of the current mission.

The project deliberately separates game authority from language generation:
Arma supplies bounded facts, local deterministic services normalize and select
the relevant evidence, OpenAI produces the natural-language answer, and
ElevenLabs optionally speaks the completed text.

| Release | Platform | Status |
| --- | --- | --- |
| **0.9.1 — Robust Context-on-Demand candidate** | Windows 10/11 x64, Arma 3, .NET 8 | AI-selected bounded retrieval, player-message journaling, developing event windows and bounded friendly equipment queries are implemented; live acceptance and GitHub CI status are reported separately |

The complete release and repository history is in
[`CHANGELOG.md`](CHANGELOG.md).

## How it works

```text
Arma 3
  └─ perspective-bound SQF client addon
       ├─ session handshake and state-snapshot-v2
       ├─ own-side friendly and known-contact picture
       └─ official named-location gazetteer
            ↓
     transport-only native x64 extension
            ↓
       duplex Windows Named Pipe
            ↓
     ArmA AI Bridge WPF application
       ├─ mission-scoped SQLite State Mirror
       ├─ contact lifecycle and player-report memory
       ├─ hierarchical context broker and deterministic spatial language
       ├─ read-only World State and Context on Demand diagnostics
       └─ shared typed/voice AssistantTurnService
            ↓
       OpenAI transcription and Responses
            ↓
       visible answer → ElevenLabs speech → Windows playback
```

Typed input, local push-to-talk, global push-to-talk, and the opt-in
voice-activated microphone all enter the same assistant path. Voice does not
maintain a separate world model or conversation.

## Release 0.8 capabilities

### Perspective-bound Arma state

- Explicit mission/session handshake and protocol feature versions.
- One bounded `state-snapshot-v2` envelope every eight seconds, built from
  independently sampled player, environment, time, loadout, friendly-force,
  known-contact, task, and marker sections.
- Current player side and dynamic `groupId (group player)` callsign.
- Player position, grid, and elevation retained locally for authorized
  deterministic calculations but excluded from ordinary OpenAI context.
- Own-side groups and units plus side-visible hostile or unidentified contacts
  already available through Arma target knowledge.
- Estimated contact position, uncertainty, age, lifecycle, and privacy-safe
  observer callsigns; never unrestricted hostile truth.
- Bounded official `CfgWorlds` named-location export. There is no complete
  building, road, vegetation, terrain-object, or static-map scan.
- Locally visible positive-alpha mission markers, including point, rectangle,
  ellipse, and privacy-safe Bullseye reference interpretation.
- Mission tasks and read-only mission-declared capabilities/assets. No support
  action is executed.
- Overcast and mission time. Temperature and wind are not collected, stored,
  displayed, or forwarded.

### Local State Mirror and tactical memory

- Transactional SQLite State Mirror with schema migration, per-section
  readiness, freshness, atomic replacement, stale preservation, and
  mission/session reset.
- Privacy-safe mission-scoped aliases and one-way hashes for transport
  identities.
- Retained hostile/unidentified contact tracks and observations with
  current/last-known/dead semantics, uncertainty, corroboration, and reporter
  callsigns.
- Session-scoped player reports, corrections, retractions, lore, and structured
  six-digit grid anchors.
- Deterministic grid-to-grid distance and cardinal-direction calculations using
  explicit player-reported anchors, never hidden canonical position fallback.
- Confirmed **Reset AI Context** action that clears tactical state, reports,
  lore, contact history, and dialogue focus without deleting encrypted provider
  settings.

### Context-on-demand and radio behavior

- Minimal plain-English provider seed containing only the actual player
  message or a human-readable event summary, dynamic callsign/side, and the
  available information-area names.
- Model-selected `inspect_context_catalogue` and `query_context` tools backed by
  strict local validation, bounded summaries, privacy projection and exact
  local spatial calculations.
- A deterministic provider-boundary formatter converts selected structured
  results into short English facts. Database rows, schemas, raw aliases,
  timestamps, tags and serialized query envelopes remain local.
- Permission-gated, scope-bound one-shot access to a separate local map
  intelligence database.
- Four-stage **Context on Demand** diagnostics: minimal seed, actual AI plan,
  retrieved context and model-visible exchange, with last/five-/thirty-minute
  provider token totals.
- Unsolicited contact changes use the same context tools and may remain silent;
  the event handler does not route categories from keywords.
- Dynamic Arma group callsign in acknowledgements and final answers, with a
  neutral fallback when unavailable.
- Editable operator pre-prompt and bounded response-profile controls.
- English-only speech normalization for callsigns, numbers, units, grids, and
  optional terminators.
- Context-dependent single or multi-call delivery with bounded pauses, optional
  receipt confirmation, and locally simplified repeat handling.
- Hierarchical position descriptions:

  1. nearest locally authorized Bullseye;
  2. nearby mission location, active objective, official named place, or living
     stationary friendly group;
  3. six-digit grid fallback.

- Eight-point cardinal directions and natural tactical range rounding instead
  of raw coordinate pairs or routine numeric bearings.
- Deduplicated proactive announcements for genuinely new or reacquired
  own-side-known hostile/unidentified contacts.
- Spatially compatible infantry and vehicle transitions are consolidated.
  Reacquisition requires at least 30 seconds continuously last-known.

### Voice and input

- Typed questions and answers.
- Local press-and-hold microphone capture.
- Configurable global press-and-hold PTT using Windows Raw Input. The default is
  **Shift + Space**; the app does not reserve, suppress, or inject the chord.
- Opt-in **Mic always on** mode with local voice activity detection, no wake
  word, no silence uploads, trailing-silence completion, and a 15-second cap.
- OpenAI completed-utterance transcription.
- OpenAI Responses with locally managed bounded history and `store: false`.
- ElevenLabs-only assistant speech output.
- Every ElevenLabs transmission uses English number words rather than digits.
- Transcript shown immediately after transcription; answer shown immediately
  after reasoning.
- TTS or playback failure preserves the visible conversation and allows
  **Replay Last Answer** without repeating transcription or OpenAI reasoning.
- Microphone, transcription, voice, Raw Input, and voice-activation diagnostics.

## Local and model tool boundary

Normal assistant turns receive the locally interpreted tactical context rather
than a raw database or telemetry dump. Only the closed mission-memory functions
may be offered when the current input permits a memory operation:

```text
remember_information
search_memory
update_memory
forget_memory
```

The application also has bounded local read/query services for diagnostics and
manual map/environment requests. These accept fixed enums and validated limits;
they do not accept SQL, SQF, source code, file paths, or operating-system
commands.

OpenAI can never execute arbitrary SQF, C++, PowerShell, SQL, or Windows
commands.

## Installation

A matching build artifact has this shape:

```text
app/
  ArmA AI Bridge.exe
  ArmA AI Bridge.dll
  Microsoft.Data.Sqlite, NAudio, SQLite and runtime dependencies

mod/
  @Arma_AI_Bridge/
    addons/arma_ai_bridge_client.pbo
    arma_ai_bridge_x64.dll
    mod.cpp
```

1. Copy the complete contents of `app/` to a writable Windows folder.
2. Copy `mod/@Arma_AI_Bridge` into the Arma 3 directory or another Launcher
   mod directory.
3. Add `@Arma_AI_Bridge` as a local mod in the Arma 3 Launcher.
4. Start `ArmA AI Bridge.exe`.
5. Start an Arma mission with the matching PBO and native DLL enabled.
6. Confirm **Arma connected** and a current session in the dashboard.

Do not mix the application, native DLL, and PBO from different commits.

## Configuration

Open the **API keys** tab and save:

1. an OpenAI API key for transcription and Responses;
2. an ElevenLabs API key for speech synthesis;
3. an ElevenLabs voice ID.

The same settings area contains the model, operator pre-prompt, response
profile, callsign-independent radio style, and speech terminator controls.
Global PTT and always-on microphone controls are in the Assistant tab.

Provider credentials are encrypted with Windows DPAPI for the current user.

## Privacy, security, and fair play

- The addon exports a client-perspective mission picture, not an unrestricted
  server world.
- Hidden opposing-side entities are not deliberately enumerated or inferred.
- Contact collection uses Arma own-side target knowledge and engine-estimated
  positions.
- Player profile names and UIDs are never collected.
- Raw engine IDs, source IDs, local database aliases, credentials, and
  canonical player coordinates do not enter ordinary OpenAI context.
- Raw state snapshots and complete SQLite tables are not sent to OpenAI.
- Mission and marker text are treated as untrusted labels, never instructions.
- Audio, transcripts, questions, answers, prompts, tactical context, provider
  payloads, API keys, and voice IDs are not written to the application log.
- Temporary WAV files are bounded and deleted after success, failure, or
  cancellation.
- ElevenLabs receives only speech text. OpenAI receives audio only for an
  explicit PTT/voice-activated utterance and receives only the minimized
  question-relevant tactical context for reasoning.
- `store: false` is used for Responses requests; it is not a provider-wide Zero
  Data Retention agreement.

See [`privacy-and-fair-play.md`](docs/papa-bear-v1/privacy-and-fair-play.md)
for the exact boundaries.

## Deliberate non-goals

Release 0.8 does not include:

- ACE integration or ballistic/firing-solution calculations;
- support execution, waypoint assignment, route planning, landing-zone
  execution, or arbitrary SQF;
- complete-map or runtime-object indexing;
- hidden enemy state or unrestricted `allMissionObjects` enumeration;
- observation of empty vehicles, crates, fortifications, or tactical objects
  that are not already in an authorized source;
- voice streaming, wake words, microphone/output-device selection, or radio
  audio effects;
- multiplayer signatures, installer, automatic updater, or production
  hardening.

The native DLL remains transport-focused.

## Build and test on Windows

Prerequisites:

- Windows 10/11 x64;
- .NET 8 SDK;
- Visual Studio C++ x64 toolchain and CMake;
- Python 3;
- optional Arma 3 Tools for official Addon Builder output.

```powershell
Set-ExecutionPolicy -Scope Process Bypass
py -3 scripts/verify_repository.py
dotnet test tests/ArmaAiBridge.App.Tests/ArmaAiBridge.App.Tests.csproj -c Release
./scripts/build.ps1 -Configuration Release
```

The Windows GitHub Actions job performs repository verification, Release tests,
WPF `win-x64` publish, native x64 build, PBO packaging/verification, and one
matching development artifact upload.

## Acceptance status

- Releases 0.1–0.3 established the basic addon/native/app/text path.
- Milestones 1–3 and release 0.7 passed their recorded live acceptance.
- Release 0.8 deterministic and Windows CI coverage is green.
- Full live 0.8 acceptance, including the newest Bullseye, fallback,
  reacquisition, always-on microphone, and reset scenarios, remains a manual
  gate.

## Documentation

- [Papa Bear v1 index](docs/papa-bear-v1/README.md)
- [Arma data contract](docs/papa-bear-v1/arma-data-contract.md)
- [Voice architecture](docs/papa-bear-v1/voice-architecture.md)
- [World model](docs/papa-bear-v1/world-model.md)
- [Privacy and fair play](docs/papa-bear-v1/privacy-and-fair-play.md)
- [Complete changelog and version history](CHANGELOG.md)

## License

See [`LICENSE`](LICENSE).
