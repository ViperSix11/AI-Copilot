# Changelog and version history

This file records notable product, protocol, privacy, build, and user-visible
changes for ArmA AI Bridge.

The repository did not create Git tags for releases 0.1 through 0.8. The
version boundaries below are reconstructed from the assembly version, merged
pull requests, first-parent history, and the authoritative documents under
`docs/papa-bear-v1/`.

## 0.8.1 — Natural Dynamic Radio Hotfix — Unreleased

### Added

- Context-dependent probabilistic single or multi-transmission delivery.
- Bounded urgent and calm pauses between separately synthesized radio calls.
- Local five-minute receipt-confirmation state accepting affirmative,
  negative and repeat vocabulary.
- Deterministic simplified repeat handling without another OpenAI Responses
  request.
- Safe radio variation/count/confirmation diagnostics without content logs.

### Changed

- OpenAI now produces complete factual prose while the local radio layer owns
  stand-by phrases, pauses, copy requests and transmission sequencing.
- Combat responses are prompted shorter and calmer situations allow restrained
  conversational cadence.
- Every ElevenLabs payload converts grouped numbers, integers and decimals to
  English words and applies a final no-digit safeguard.
- Pending receipt checks no longer block substantive questions or reports;
  natural repeat references and continuation replies are handled locally.
- Compatible contact tracks that render to the same rounded tactical position
  are announced once as a counted group instead of repeated verbatim calls.
- Enemy `last known position` questions now retrieve the newest eligible
  contact locations even when those contacts are still current.
- New-contact radio calls use a three-second batch, 250-metre presentation
  clusters and a 30-second similar-cluster cooldown while preserving every
  underlying contact record.
- Hostile-strength questions and immediate estimation follow-ups receive a
  deterministic count/composition/cluster projection through a bounded
  two-follow-up local topic.
- `state-snapshot-v2` publication changed from four to eight seconds, reducing
  routine SQLite snapshot transactions while retaining section sampling
  cadence and 30-second full reconciliation.

### Boundaries

- No native DLL, Named Pipe schema, position calculation, mission-memory schema
  or model-tool changes. SQF changes only the existing snapshot publication
  gate; individual collectors and the fair-play boundary are unchanged.
- Full live acceptance remains required as documented in
  `docs/papa-bear-v1/release-0.8.1-natural-radio-hotfix.md`.

### Local validation

- Repository verifier: 176 UTF-8 files.
- Deterministic Release suite: 301 tests.
- WPF `win-x64`, native x64 and 22-file Addon Builder PBO builds passed.
- GitHub Actions and live Arma acceptance remain pending.

## 0.8.0 — Unified State Mirror & Interpreter — 2026-07-23

Release PR: [#12](https://github.com/ViperSix11/AI-Copilot/pull/12)

### Added

- `state-snapshot-v2`, a four-second bounded state envelope with independent
  section sampling and periodic full reconciliation.
- Transactional mission/session-scoped SQLite State Mirror, schema migrations,
  readiness/freshness diagnostics, and atomic section replacement.
- Canonical dynamic Arma group callsign collection and deterministic spoken
  formatting.
- Bounded official named-location gazetteer and local named-place lookup.
- Closed `tactical-snapshot-v2` plus deterministic human-readable evidence
  projection.
- AI Context diagnostics for all candidates, selected evidence, fused
  interpretation, and exact transmitted tactical context.
- Mission memory for explicit player facts, corrections, retractions, lore,
  and structured six-digit reported-grid anchors.
- Local grid-to-grid distance/cardinal calculations without canonical player
  position fallback.
- Retained hostile/unidentified contact tracks, observations, lifecycle,
  reporter callsigns, uncertainty, grouping, and session restart behavior.
- Deduplicated proactive new/reacquired contact announcements with best-effort
  speech.
- Confirmed AI Context reset that preserves encrypted provider and response
  settings.
- Hierarchical contact/friendly/objective position reporting: nearest
  Bullseye, nearby named/stationary-friendly reference, then grid.
- Tactical range rounding, eight-point direction, mixed contact grouping, and a
  30-second genuine-reacquisition threshold.
- Configurable Windows Raw Input global PTT and opt-in always-on microphone
  mode with local voice activity detection.
- Adjustable operator pre-prompt, response profiles, conditional
  acknowledgements, speech-safe numbers/units/callsigns, and high-contrast
  tactical UI.

### Changed

- Replaced broad/raw telemetry forwarding with purpose-specific, locally
  interpreted evidence.
- Standard model turns now expose only closed mission-memory tools when locally
  eligible.
- Made the current Arma group callsign the canonical address for
  acknowledgements and final answers.
- Limited model-facing weather to overcast and a deterministic condition.
- Made transcript and assistant text visible before speech synthesis/playback.
- Made contact/location answers use operational wording rather than database,
  provenance, telemetry, or raw-coordinate language.
- Development packaging now includes matching WPF app, native DLL, `mod.cpp`,
  and PBO.

### Fixed

- Responses API parsing, incomplete-response retry, encrypted reasoning replay,
  and the multi-round local tool loop.
- State astronomy failure handling and section-level failure isolation.
- Player position leakage through the player’s friendly group and
  player-relative contact/location fields.
- Missing dynamic callsign updates across session/group changes.
- Repeated “missing bearing/range” clarification loops.
- Raw Input PTT test mode and background-key handling.
- Successful transcript/answer loss when TTS or Windows playback failed.
- Duplicate per-soldier contact announcements.
- False reacquisition after a brief one-cycle last-known transition.
- Grid-first contact reports when a known Bullseye or named reference existed.
- Blank-text Bullseye recognition by deriving a safe local label before raw
  marker identity disposal.

### Removed

- Experimental ACE integration and all ballistic/firing-solution code.
- Temperature and wind collection, persistence, diagnostics, prompting, and
  model context.
- Model-facing raw coordinate objects and ordinary canonical player-position
  disclosure.
- RegisterHotKey-based global PTT in favor of non-suppressing Raw Input.

### Privacy and fair-play boundary

- No unrestricted world enumeration, hidden hostile truth, raw mission object
  dump, or anti-cheat bypass.
- Player names, profile names, UIDs, raw engine IDs, and raw marker identifiers
  remain outside OpenAI context.
- Bullseye authorization is client-perspective: locally present
  positive-alpha markers and local channel metadata, never marker color/name
  as an authority grant.

### Validation at release closeout

- Repository verifier: 172 UTF-8 files at release closeout.
- Deterministic Release suite: 271 tests at release closeout.
- WPF `win-x64`, native x64, and 22-file Addon Builder PBO builds passed.
- GitHub Actions Windows build passed for the release-closeout commit.
- Full live 0.8 acceptance remains the explicit manual gate recorded in
  `docs/papa-bear-v1/release-0.8-tactical-memory.md`.

### Internal 0.8 patch chronology

| Date | Commit | Change |
| --- | --- | --- |
| 2026-07-22 | `e2efe57` | Implement Milestone 5 contextual interpreter |
| 2026-07-22 | `21c68c5` | Complete release 0.8 Unified State Mirror |
| 2026-07-22 | `144ad0b` | Fix state-snapshot astronomy failure |
| 2026-07-22 | `308cf7d` | Improve Papa Bear tactical UI readability |
| 2026-07-22 | `8cda059` | Fix Responses parsing and canonical state context |
| 2026-07-22 | `c897d71` | Use fixed operational context and Arma callsigns |
| 2026-07-22 | `64891c3` | Add release 0.8 radio and experimental ballistics |
| 2026-07-22 | `2db118d` | Trigger release 0.8 validation |
| 2026-07-22 | `bf47530` | Complete input and experimental ballistic safeguards |
| 2026-07-22 | `6a5c270` | Remove ballistics and repair PTT test mode |
| 2026-07-23 | `24facc3` | Add tactical snapshot and mission memory |
| 2026-07-23 | `64b58d5` | Add tactical evidence and contact reporting |
| 2026-07-23 | `df71953` | Group compatible contact announcements |
| 2026-07-23 | `74bae4e` | Add confirmed AI Context reset |
| 2026-07-23 | `a6cd5f9` | Add opt-in always-on microphone mode |
| 2026-07-23 | `b7ecd5a` | Add hierarchical tactical position reports |

## 0.7.0 — Voice Position MVP — 2026-07-22

Release PR: [#11](https://github.com/ViperSix11/AI-Copilot/pull/11)

- Added one shared typed/spoken assistant-turn path.
- Added 15-second local press-and-hold microphone capture.
- Added OpenAI completed-utterance transcription and removed the separate
  AssemblyAI dependency.
- Added ElevenLabs-only speech synthesis, Windows playback, cancellation, and
  replay.
- Surfaced transcript immediately after transcription and answer immediately
  after Responses.
- Preserved transcript and answer when TTS or playback failed; replay did not
  repeat transcription or reasoning.
- Added voice tests, API settings, clean provider boundaries, and matching
  Windows app/DLL/PBO packaging.
- Updated the application and displayed version to `0.7.0`.
- Passed the recorded live Voice Position MVP acceptance.

## 0.6.0 — Voice development candidate — 2026-07-22

- Introduced the first push-to-talk capture, transcription experiments,
  ElevenLabs synthesis, replay, and voice-stage diagnostics.
- Established the design rule that voice is an interface over the existing
  assistant path, not a separate agent.
- This development version was promoted and corrected in 0.7.0 rather than
  released independently.

## 0.5.0 — Friendly Force Picture — 2026-07-22

Milestone PR: [#8](https://github.com/ViperSix11/AI-Copilot/pull/8)

- Added explicit mission/session ID and protocol feature handshake.
- Added read-only own-side groups, units, crewed vehicles, support assets, and
  mission capability registry.
- Added delta/reconciliation handling and friendly-force diagnostics.
- Added bounded local friendly-force, asset, and capability query services.
- Preserved perspective-bound contact visibility and transport-only native DLL.
- Passed the recorded Milestone 3 live acceptance for session,
  friendly-force, and existing tool behavior.

## 0.4.0 — Provenance-aware world model — 2026-07-21

Milestone PR: [#7](https://github.com/ViperSix11/AI-Copilot/pull/7)

- Added `TelemetryIngestService` and `WorldStateStore`.
- Added stable identities where the existing telemetry allowed them.
- Added timestamps, freshness, confidence, session/map reset, and
  purpose-specific snapshots.
- Added read-only World State diagnostics.
- Replaced direct raw telemetry forwarding with minimized local snapshots.
- Preserved the manual environment query and stateless OpenAI loop.
- Passed the recorded Milestone 2 live acceptance.

## 0.3.0 — OpenAI text assistant and stable tool loop — 2026-07-21

Feature PRs:
[#4](https://github.com/ViperSix11/AI-Copilot/pull/4),
[#6](https://github.com/ViperSix11/AI-Copilot/pull/6)

- Added the WPF typed assistant and OpenAI Responses integration.
- Added validated local read-tool routing and the stateless multi-round tool
  loop.
- Fixed direct text-answer completion, function-output replay, bounded tool
  rounds, cancellation, and diagnostic handling.
- Added encrypted API settings and no-content logging boundaries.

## 0.2.0 — Duplex environment query bridge — 2026-07-21

Feature PR: [#3](https://github.com/ViperSix11/AI-Copilot/pull/3)

- Extended the Named Pipe to duplex request/response transport.
- Added bounded manual environment queries.
- Added correlation, timeout, validation, and Windows integration tests.
- Kept the native extension transport-focused.

## 0.1.0 — Foundation and one-way telemetry — 2026-07-21

Foundation PRs:
[#1](https://github.com/ViperSix11/AI-Copilot/pull/1),
[#2](https://github.com/ViperSix11/AI-Copilot/pull/2)

- Created the C#/.NET 8 WPF application, C++ x64 Arma extension, SQF client
  addon, Named Pipe protocol, schemas, samples, tests, and Windows build
  scripts.
- Added telemetry dashboard, encrypted settings, log viewer, and initial
  player/environment telemetry.
- Renamed and verified the project as ArmA AI Bridge.
- Established CI, repository consistency checks, packaging, privacy rules, and
  the initial manual Arma test plan.

## Pull-request chronology

| PR | Date | Outcome | Purpose |
| --- | --- | --- | --- |
| [#1](https://github.com/ViperSix11/AI-Copilot/pull/1) | 2026-07-21 | Merged | Verified ArmA AI Bridge rename |
| [#2](https://github.com/ViperSix11/AI-Copilot/pull/2) | 2026-07-21 | Merged | Repository verification gate |
| [#3](https://github.com/ViperSix11/AI-Copilot/pull/3) | 2026-07-21 | Merged | Dynamic environment query |
| [#4](https://github.com/ViperSix11/AI-Copilot/pull/4) | 2026-07-21 | Merged | OpenAI text assistant |
| [#5](https://github.com/ViperSix11/AI-Copilot/pull/5) | 2026-07-21 | Merged | Papa Bear v1 architecture |
| [#6](https://github.com/ViperSix11/AI-Copilot/pull/6) | 2026-07-21 | Merged | Stateless tool-loop stabilization |
| [#7](https://github.com/ViperSix11/AI-Copilot/pull/7) | 2026-07-21 | Merged | Provenance-aware world model |
| [#8](https://github.com/ViperSix11/AI-Copilot/pull/8) | 2026-07-22 | Merged | Friendly Force Picture |
| [#9](https://github.com/ViperSix11/AI-Copilot/pull/9) | 2026-07-22 | Closed, unmerged | Superseded full static-map indexing draft |
| [#10](https://github.com/ViperSix11/AI-Copilot/pull/10) | 2026-07-22 | Closed, unmerged | Superseded observation-memory draft |
| [#11](https://github.com/ViperSix11/AI-Copilot/pull/11) | 2026-07-22 | Merged | Release 0.7 Voice Position MVP |
| [#12](https://github.com/ViperSix11/AI-Copilot/pull/12) | 2026-07-23 | Release closeout | Release 0.8 Unified State Mirror & Interpreter |

## Versioning notes

- Application/product versions use semantic `major.minor.patch` form.
- Arma wire schemas, SQLite schema versions, protocol features, and product
  versions are independent and must not be inferred from one another.
- Matching application, native DLL, and PBO artifacts must always be deployed
  together.
- Superseded or closed drafts are recorded above but are not part of a released
  version.
