# Voice architecture

## Active release 0.8 pipeline

Voice is an adapter around the same turn service used by typed chat. It owns no
world state, interpreter, conversation history or tool policy.

```text
hold local button or background Raw Input push-to-talk (maximum 15 seconds)
-> bounded WAV
-> OpenAI completed-utterance transcription
-> visible final transcript
-> shared AssistantTurnService
-> frozen fixed compact operational snapshot when operational
-> start one OpenAI Responses/tool loop and 5,000-millisecond timer concurrently
-> conditional local English acknowledgement if final text remains pending
-> cached acknowledgement ElevenLabs speech, cancellable before playback
-> locally normalized final visible answer
-> final ElevenLabs speech after acknowledgement playback
-> Windows playback
```

An ordinary operational question has no tool round because all fixed compact
domains are in the initial snapshot. Arbitrary terrain-object enumeration is
not model-facing; explicit firing-solution intent adds only strict
`calculate_firing_solution`. Transcription is a separate OpenAI request. One
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
- the model answer is normalized once; the turn service ensures the exact
  current callsign is present in the visible final response without reusing an
  older locally-added address from history; ElevenLabs/replay may use only the
  speech-formatted current callsign, numbers and units;
- Over/Out suffixes are removed and the configured terminator is appended once;
- replay synthesizes or replays the last final answer and never repeats STT,
  acknowledgement or Responses.

## State and failure behavior

```text
ready -> recording -> transcribing -> thinking
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
- microphone data is transmitted only for explicit transcription or hold-to-talk;
- questions, transcripts, answers, callsigns, style text, prompts, snapshots,
  provider bodies, credentials and audio content are not logged;
- safe logs contain only snapshot bytes/counts, bounded history counts, selected
  tool count, provider token totals, acknowledgement eligibility/emission/
  threshold/variation, answer latency and bounded retry metadata;
- temporary recordings are bounded and deleted on success, failure or cancel;
- Responses uses `store: false`, which is not a Zero Data Retention promise.

## Deferred voice work

Always-on listening, wake words, VAD, streaming STT/TTS, device selection and
audio effects are not release 0.8 dependencies. Global PTT registers the generic
keyboard once with `RegisterRawInputDevices`, exactly `RIDEV_INPUTSINK`, and a
process-lifetime hidden `HwndSource`. `WM_INPUT` make/break events drive a bounded
in-memory chord matcher while leaving normal keyboard input untouched. It uses
no `RegisterHotKey`, polling, keyboard hook, injection, device-name lookup or
arbitrary key logging.
