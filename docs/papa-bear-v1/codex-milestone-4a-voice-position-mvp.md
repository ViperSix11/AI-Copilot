# Codex Milestone 4A: Voice Position MVP

## Objective

Milestone 4A adds one bounded push-to-talk path to the Windows application. A
player holds the Assistant control, asks a short question such as “Papa Bear,
welche Position habe ich?”, and receives the same privacy-minimized,
world-state-grounded answer used by a typed turn, spoken through the default
Windows output device.

This is a voice transport and user-experience milestone, not a new reasoning,
world-model, or Arma-data milestone. It preserves the Milestones 1–3 stateless
OpenAI tool loop and current telemetry authority. The Arma addon and native DLL
do not change.

## Exact milestone boundary

In scope:

- press-and-hold microphone capture in the Assistant tab;
- a local microphone test with local playback;
- completed-utterance AssemblyAI transcription;
- one shared typed/spoken assistant-turn pipeline;
- complete-response ElevenLabs synthesis and default-device playback;
- replay and cancellation controls;
- explicit voice stage diagnostics in the Assistant UI;
- deterministic app tests and a packaged Windows development artifact.

Out of scope:

- always-on listening, wake words, VAD, global hotkeys, streaming STT/TTS, or
  barge-in;
- operational memory, SQLite, gazetteers, static map indexing, side-wide
  contacts, empty-object perception, player-report memory, or proactive alerts;
- new Arma telemetry, SQF collection, native transport behavior, or mission
  protocol messages;
- support execution, routes, waypoints, ACE, ballistics, or arbitrary code
  execution.

Draft PR #10 remains unmerged experimental work pending a post-MVP architecture
review. No code or schema from it is merged or cherry-picked into Milestone 4A.

## Existing data and shared reasoning path

The current `WorldStateStore` and `WorldSnapshotBuilder` already provide the
only game context required by this milestone. A current-situation snapshot
contains the loaded map name and grid metadata plus the player's current
privacy-safe ASL position, body heading, view heading, timestamps, freshness,
and confidence. It excludes profile names, UIDs, raw engine IDs, source mission
IDs, and full friendly-force data.

Both typed and spoken turns call one application service:

```text
SubmitUserTurnAsync(text, source, cancellationToken)
  -> build a fresh current-situation snapshot
  -> call the existing OpenAI Responses service
  -> run the same bounded, typed, stateless local tool loop when selected
  -> append one user turn and one Papa Bear answer to the conversation
```

`source` is local metadata (`typed` or `spoken`) used for UI behavior and
tests. It does not select a different prompt, tool set, world snapshot, history,
privacy policy, or reasoning service. Only a successfully completed spoken
turn proceeds to TTS. Typed turns remain text-only unless Replay Last Answer is
explicitly selected.

The position question must therefore be answered from the fresh snapshot, not
from transcript text, UI telemetry labels, a second OpenAI prompt, or a new
Arma query. The answer is expected to identify the map and report the available
grid and/or coordinate position; it must qualify stale or unavailable state.

## Microphone abstraction and lifecycle

`IMicrophoneCaptureService` owns Windows microphone capture. Production uses
the default Windows recording device at 16 kHz, 16-bit, mono PCM and writes a
valid WAV into an application-owned temporary file. The encoded maximum is
approximately 480 KiB of PCM plus a WAV header for 15 seconds. A hard byte cap
slightly above that value prevents an unbounded device callback from growing
the recording.

Lifecycle:

1. A primary-button press on **Hold to Talk** starts capture. Keyboard
   activation is not treated as a latched recording command.
2. Releasing the button anywhere in the application stops capture and produces
   exactly one immutable recording handle.
3. Capture stops automatically after 15 seconds if release is missed.
4. A second start is rejected while any capture or voice turn is active.
5. Cancellation stops the device and prevents later stages from starting.
6. The recording handle is disposed in every success, failure, cancellation,
   window-close, and test path. Disposal closes the WAV writer before deleting
   the temporary file.

There is no background capture. **Test Microphone** uses the same bounded
capture object and immediately plays it locally without contacting AssemblyAI,
OpenAI, or ElevenLabs.

## AssemblyAI completed-utterance contract

`ISpeechToTextService.TranscribeAsync(recording, apiKey, cancellationToken)`
returns one trimmed final transcript or a typed, actionable error. The MVP uses
AssemblyAI's asynchronous prerecorded-audio REST flow:

1. `POST https://api.assemblyai.com/v2/upload` with the DPAPI-decrypted key in
   `Authorization`, `Content-Type: application/octet-stream`, and the bounded
   WAV body. Read `upload_url`.
2. `POST https://api.assemblyai.com/v2/transcript` with the same authorization
   header and JSON `{ "audio_url": "...", "language_detection": true }`.
   Read the transcript `id`.
3. Poll `GET /v2/transcript/{id}` at a fixed one-second cadence until status is
   `completed` or `error`. Accept only the one final `text` value.

The HTTP client has a per-request timeout and the whole poll operation has a
60-second deadline independent of the caller token. `queued` and `processing`
continue polling. `error`, an empty final transcript, malformed JSON, 401/403,
429, provider 5xx, network failure, and poll timeout produce distinct
user-facing guidance without including the key, audio URL, transcript ID,
audio bytes, response body, or transcript. Cancellation is honored during
upload, creation, polling delay, and polling request.

The AssemblyAI key remains encrypted by Windows DPAPI in the existing settings
file and is decrypted only for an active call. Neither audio nor transcript is
written to application logs. **Test Transcription** records one bounded sample,
uses only this service, and displays its final transcript in the Assistant
conversation; it does not call OpenAI or TTS.

Official contract references:

- [AssemblyAI upload endpoint](https://www.assemblyai.com/docs/api-reference/files/upload)
- [AssemblyAI transcript retrieval and statuses](https://www.assemblyai.com/docs/api-reference/transcripts/get)
- [AssemblyAI authentication and errors](https://www.assemblyai.com/docs/api-reference/overview/)

## ElevenLabs complete-response contract

`ITextToSpeechService.SynthesizeAsync(text, apiKey, voiceId,
cancellationToken)` returns a bounded immutable audio payload. Production calls:

```text
POST https://api.elevenlabs.io/v1/text-to-speech/{escaped voiceId}
     ?output_format=mp3_44100_128
xi-api-key: <DPAPI-decrypted key>
Content-Type: application/json

{"text":"...","model_id":"eleven_multilingual_v2"}
```

The complete MP3 response is read with a 10 MiB cap before playback. This
service boundary deliberately returns complete audio so a later milestone can
replace the implementation with streaming without changing the assistant-turn
contract.

`IAudioPlaybackService` plays a WAV recording or synthesized MP3 on the default
Windows output device. It permits one playback at a time. Starting another
operation is rejected; Cancel stops playback and disposes decoder/device state.
No synthesized file is persisted. The last successful synthesized payload is
kept only in memory for **Replay Last Answer**, is replaced atomically by the
next successful answer, and is cleared on panel disposal.

Missing/invalid key, missing/invalid voice ID, 401/403, 404, 422, 429, provider
5xx, network timeout, oversized/empty audio, decoder failure, missing output
device, and playback failure surface actionable, secret-free messages. Text,
keys, voice IDs, response bodies, and audio content are not logged.

**Test Papa Bear Voice** bypasses microphone, STT, OpenAI, and Arma and speaks
exactly: “Papa Bear online. Radio check complete.”

Official contract references:

- [ElevenLabs create speech endpoint](https://elevenlabs.io/docs/api-reference/text-to-speech/convert)
- [ElevenLabs API authentication](https://elevenlabs.io/docs/api-reference/authentication)

## UI states and actions

The Assistant displays exactly one current stage:

```text
ready | recording | transcribing | thinking | generating-voice | speaking | failed
```

`failed` includes one actionable user message. Cancelled work returns to
`ready` with a short cancellation status; cancellation is not a failure.
Transitions are serialized, and stale async completions are ignored after
cancellation or disposal.

Actions:

- **Test Microphone**: hold to record a bounded local sample, then local
  playback only;
- **Test Transcription**: hold to record, then AssemblyAI only;
- **Test Papa Bear Voice**: synthesize and play the fixed phrase only;
- **Hold to Talk**: press/start and release/stop, then STT → shared assistant
  turn → TTS → playback;
- **Replay Last Answer**: play the most recent in-memory TTS payload;
- **Cancel**: stop whichever recording, HTTP call, OpenAI/tool call, synthesis,
  or playback is active.

The final transcript is appended once as the user's conversation turn and the
final answer once as Papa Bear's turn. Partial text is never shown. Neither is
written to Logs. Existing typed Ask and Clear Conversation behavior remains;
Clear also resets the shared OpenAI history.

## Cancellation, timeouts, and overlap policy

- microphone: hard stop at 15 seconds;
- AssemblyAI HTTP request: 30 seconds per request, 60 seconds total polling;
- existing OpenAI/tool deadlines remain unchanged;
- ElevenLabs request: 60 seconds;
- playback: duration-controlled by the decoded stream and caller cancellation;
- one capture/turn/test/playback operation at a time per Assistant panel;
- cancellation tokens are linked to panel disposal and operation-specific
  deadlines;
- cancellation between stages is checked before the next provider call.

No retry is automatic because upload, transcription, and synthesis may incur
provider cost. The user may explicitly retry.

## Privacy, logging, and prompt boundary

- Microphone data leaves the computer only during explicit **Test
  Transcription** or **Hold to Talk**, and only for AssemblyAI.
- A final transcript reaches OpenAI only during a real spoken assistant turn.
  It is subject to the same prompt/history handling as typed text.
- ElevenLabs receives only the final answer or the fixed voice-test phrase.
- Audio, transcripts, questions, answers, complete prompts, world snapshots,
  tool results, keys, authorization headers, provider response bodies, upload
  URLs, transcript IDs, and voice IDs are absent from application logs.
- Conversation text remains visible in the in-memory Assistant conversation.
- Temporary microphone WAV files are application-owned, bounded, never
  indexed or retained by the app, and deleted deterministically.
- The OpenAI boundary remains the current minimized world snapshot plus bounded
  local read-tool results. Voice does not broaden telemetry authority.

## Deterministic automated acceptance

The Windows test suite must prove:

1. capture starts once, stops once on release, and automatically stops at the
   15-second limit;
2. cancellation and disposal stop capture and delete every temporary WAV;
3. microphone test calls only capture and local playback;
4. transcription test calls AssemblyAI once and does not call OpenAI or TTS;
5. AssemblyAI upload/create/poll handles completed, error, malformed,
   credential, network, timeout, and cancellation responses deterministically;
6. one final transcript is submitted and appended exactly once;
7. typed and spoken text enter the same `SubmitUserTurnAsync` implementation,
   take a fresh current-situation snapshot, and use the same OpenAI/tool path;
8. a spoken position question supplies current map, grid, position, freshness,
   and confidence from the world model to OpenAI;
9. only the final OpenAI answer is sent to ElevenLabs, and the fixed voice test
   uses the exact required phrase;
10. ElevenLabs authentication, URL escaping, model/output format, bounded audio,
    credential/voice/network/provider failures, and cancellation are correct;
11. no overlapping capture, turn, synthesis, or playback is allowed;
12. Replay uses only the last in-memory successful payload and Cancel works in
    recording, transcribing, thinking, generating-voice, and speaking stages;
13. test logs contain none of the seeded keys, audio markers, transcript,
    question, answer, snapshot, tool result, upload URL, transcript ID, or voice
    ID;
14. all Milestones 1–3 ingestion, world-model, strict-tool, stateless-context,
    cancellation, and privacy tests remain green;
15. repository verification, Release tests, WPF win-x64 publish, native x64
    build, deterministic PBO packaging, and artifact-content verification pass.

Audio-device tests use fakes in CI. Live hardware/provider behavior is covered
only by the live gate below.

## Exact live Arma acceptance gate

First build version 0.6.0 with `scripts/build.ps1` and confirm the development
artifact contains the matching WPF application, `arma_ai_bridge_x64.dll`,
`mod.cpp`, and `addons/arma_ai_bridge_client.pbo`. Configure valid DPAPI-stored
OpenAI, AssemblyAI, and ElevenLabs keys plus a valid ElevenLabs voice ID. Then
perform exactly these eleven live checks:

1. Start any Arma mission with the existing mod and desktop app.
2. Verify current player telemetry and map grid are visible.
3. Run **Test Papa Bear Voice** and hear exactly “Papa Bear online. Radio check
   complete.” through the default Windows output device.
4. Run **Test Microphone**, record a short sample, release, and hear the captured
   sample locally.
5. Run **Test Transcription**, record a short phrase, release, and see the
   correct final text exactly once without an OpenAI answer or ElevenLabs audio.
6. Hold **Hold to Talk**, say “Papa Bear, welche Position habe ich?”, and
   release. Observe `recording → transcribing → thinking → generating-voice →
   speaking → ready`.
7. Verify the final transcript appears exactly once in the existing
   conversation.
8. Verify the displayed assistant answer contains the current map and correct
   grid or coordinates from the live World State snapshot.
9. Verify ElevenLabs speaks the complete displayed answer once, with no overlap.
10. Ask the same question by keyboard and confirm the reported position matches
    the same current telemetry.
11. Inspect application Logs and confirm no audio, transcript, question, answer,
    API key, authorization header, prompt, world snapshot, tool result, upload
    URL, transcript ID, or ElevenLabs voice ID appears.

During this gate also verify all existing Milestones 1–3 behavior remains
available. Credential, network, timeout, cancellation, replay, 15-second limit,
overlap, and temporary-file cleanup paths are deterministic automated
acceptance requirements; they may be repeated live as supplemental diagnostics
but do not alter the exact eleven-step gate above.

The draft PR must remain unmerged until all eleven live steps pass. PR #10
remains open and unmerged as experimental post-MVP architecture work.

## Documentation limits

Official provider documentation establishes the HTTP endpoints, authentication
headers, transcript statuses, and TTS request/output shapes used above. It does
not establish Papa Bear's 15-second capture cap, polling interval/deadline,
audio byte limits, UI stages, overlap policy, local retention policy, or live
acceptance wording. Those are explicit product policies tested by this
milestone.

The official provider documentation also cannot verify Windows microphone and
default-device behavior, actual German transcription quality, the configured
ElevenLabs voice, live provider account permissions/quotas, audio-driver
availability, or live Arma position-answer quality. Those requirements remain
subject to the exact live gate.
