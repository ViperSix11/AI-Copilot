# Voice architecture

## Active release 0.8 pipeline

Voice is an adapter around the same turn service used by typed chat. It owns no
world state, interpreter, conversation history or tool policy.

```text
hold local button or background Raw Input push-to-talk
OR explicitly enable local voice-activated listening
-> one completed utterance (maximum 15 seconds)
-> bounded WAV
-> OpenAI completed-utterance transcription
-> visible final transcript
-> shared AssistantTurnService
-> frozen fixed compact operational snapshot when operational
-> start one OpenAI Responses/tool loop and 5,000-millisecond timer concurrently
-> conditional local English acknowledgement if final text remains pending
-> cached acknowledgement ElevenLabs speech, cancellable before playback
-> locally normalized final visible answer
-> local context-dependent one-or-more-transmission plan
-> final ElevenLabs speech per transmission after acknowledgement playback
-> bounded local pause between calls
-> sequential Windows playback
```

An ordinary operational question has no tool round because all fixed compact
domains are in the initial snapshot. Arbitrary terrain-object enumeration and
firing-solution calculations are not model-facing. Transcription is a separate OpenAI request. One
bounded max-token retry may repeat the frozen Responses input without creating
a second turn, history entry or acknowledgement.

## Shared turn behavior

- typed and spoken input use the same frozen compact snapshot, response profile,
  bounded OpenAI history and strict exceptional-tool dispatcher;
- acknowledgements are local, English-only, delayed, absent for a fast answer,
  absent from model history and do not count as the final answer;
- exact `groupCallsign` comes from current Arma state; speech alone may
  deterministically pronounce digits without changing visible identity;
- acknowledgement audio is cached after first synthesis and final audio never
  overlaps acknowledgement playback;
- response profiles are local style data and cannot override immutable rules;
- the model answer is normalized once; the local planner may use only the exact
  current callsign and never an older address from history; it does not force
  direct address into every response; ElevenLabs/replay may use only the
  speech-formatted current callsign, numbers and units;
- Over/Out suffixes are removed and the configured terminator is appended once;
- replay synthesizes or replays the last final answer and never repeats STT,
  acknowledgement or Responses.
- receipt confirmation and repeat state are local, expire after five minutes,
  clear on callsign/conversation reset, and never become hidden model tools;
- a locally handled repeat does not call Responses again and removes filler
  while preserving the relevant completed information;
- every ElevenLabs payload is deterministically normalized to contain English
  number words rather than ASCII digits.

## State and failure behavior

```text
ready -> recording/listening -> transcribing -> thinking
      -> acknowledgement voice -> thinking
      -> final generating-voice -> speaking -> ready
```

The transcript becomes visible immediately after successful transcription. The
acknowledgement timer follows only a valid transcript or accepted typed turn.
The final answer becomes visible immediately after successful Responses. TTS or
playback failure is partial success: text remains usable and retryable.
Cancellation is available at every asynchronous stage and does not block bridge
ingestion.

## Providers and privacy

- one DPAPI-protected OpenAI key is used for transcription and Responses;
- ElevenLabs is the only active speech-output provider;
- microphone data is transmitted only for explicit transcription,
  push-to-talk, or explicitly enabled voice-activated listening;
- questions, transcripts, answers, callsigns, style text, prompts, snapshots,
  provider bodies, credentials and audio content are not logged;
- safe logs contain only snapshot bytes/counts, bounded history counts, selected
  tool count, provider token totals, acknowledgement eligibility/emission/
  threshold/variation, answer latency and bounded retry metadata;
- temporary recordings are bounded and deleted on success, failure or cancel;
- Responses uses `store: false`, which is not a Zero Data Retention promise.

## Opt-in voice-activated listening

**Mic always on** is a persisted, explicit opt-in and is off by default. Enabling
it displays a warning that there is no wake word. The default microphone remains
open while the Assistant is idle. A deterministic local PCM detector requires
two consecutive 100-millisecond buffers above an RMS threshold of `0.025`, keeps
at most 300 milliseconds of pre-roll, discards candidates with less than 300
milliseconds of voiced audio, and completes an utterance after 900 milliseconds
of silence or at the existing 15-second hard limit. Silence and rejected noise
bursts are never written to a WAV and never sent to a provider.

Only a completed utterance enters the existing transcription and shared
assistant-turn path. Listening stops before transcription and remains paused
through Responses, acknowledgement, ElevenLabs synthesis and Windows playback,
preventing Papa Bear's own speech from becoming a new turn. It resumes after
the operation and any queued contact announcement finish. Push-to-talk remains
configured but cannot start concurrently; disabling **Mic always on** restores
the existing manual and global PTT behavior. Unchecking the option cancels the
current always-on capture or turn. A microphone failure turns the option off
instead of retrying in a failure loop.

The UI always shows `Voice: listening` while the local detector owns the
microphone and shows a separate always-on status. Completed transcripts and
answers follow the same immediate visibility, retry and partial-success rules
as push-to-talk. No audio, voice-activity samples, transcript, answer or provider
body is written to application logs.

## Deferred voice work

Wake words, streaming STT/TTS, device selection, adjustable VAD sensitivity,
barge-in and audio effects remain deferred. Global PTT registers the generic
keyboard once with `RegisterRawInputDevices`, exactly `RIDEV_INPUTSINK`, and a
process-lifetime hidden `HwndSource`. `WM_INPUT` make/break events drive a bounded
in-memory chord matcher while leaving normal keyboard input untouched. It uses
no `RegisterHotKey`, polling, keyboard hook, injection, device-name lookup or
arbitrary key logging.
