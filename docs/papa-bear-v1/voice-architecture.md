# Voice architecture

## Active 0.9.1 pipeline

Voice is an adapter around the same turn service used by typed chat. It owns no
separate world model, memory, context policy or dialogue history.

```text
hold local/global push-to-talk
OR explicitly enable local voice-activated listening
-> one completed utterance (maximum 15 seconds)
-> bounded temporary WAV
-> OpenAI completed-utterance transcription
-> visible final transcript
-> retain the player message in current-mission memory
-> shared AssistantTurnService
-> minimal plain-English context seed
-> OpenAI Responses with validated local context/memory tools
-> selected records formatted locally as short English facts
-> visible final answer
-> local context-dependent one-or-more-transmission plan
-> ElevenLabs synthesis per transmission
-> bounded local pause and sequential Windows playback
```

The model does not receive a fixed broad tactical snapshot or direct database
export. It starts with the actual message, current callsign/side and available
information-area names, then requests narrow context when required. One bounded
max-token retry may repeat the same frozen provider input without creating a
second user turn or history entry.

## Shared turn behavior

- typed and spoken input use the same context broker, response profile,
  bounded recent conversation and mission-memory services;
- all typed/transcribed messages are retained locally before model
  interpretation; the model's structured interpretation is a separate record;
- optional acknowledgements are local, English-only, delayed, absent for a
  fast answer and absent from model history;
- exact `groupCallsign` comes from current Arma state; speech alone may
  deterministically pronounce digits without changing visible identity;
- response profiles and operator pre-prompts are local style/behavior inputs
  and cannot override immutable privacy, fair-play or tool rules;
- the model answer is normalized once; the local planner may split it into
  bounded calls without changing facts;
- every ElevenLabs payload converts supported numbers to English words;
- receipt-confirmation and repeat state are local, expire after five minutes
  and cannot block substantive questions or reports;
- a locally handled repeat does not call transcription or Responses again.

## State and failure behavior

```text
ready -> recording/listening -> transcribing -> thinking
      -> optional acknowledgement voice -> thinking
      -> final generating-voice -> speaking -> ready
```

The transcript becomes visible immediately after successful transcription. The
final answer becomes visible immediately after successful Responses. TTS or
playback failure is partial success: the visible conversation and assistant
history remain intact, and **Replay Last Answer** retries speech without
repeating transcription or reasoning.

Cancellation is available at every asynchronous stage and does not block State
Mirror ingestion.

## Input modes

### Push-to-talk

Local press-and-hold capture and configurable global PTT share the same
15-second cap. Global PTT registers the generic keyboard once with
`RegisterRawInputDevices` and `RIDEV_INPUTSINK`. `WM_INPUT` make/break events
drive a bounded in-memory chord matcher while leaving normal keyboard input
untouched. It uses no `RegisterHotKey`, polling, keyboard hook, injection,
device-name lookup or arbitrary key logging.

### Opt-in voice activation

**Mic always on** is persisted, explicitly opt-in and off by default. There is
no wake word. A deterministic local PCM detector requires two consecutive
100-millisecond buffers above RMS `0.025`, keeps at most 300 milliseconds of
pre-roll, rejects candidates with less than 300 milliseconds of voiced audio,
and completes after 900 milliseconds of silence or the 15-second hard limit.

Silence and rejected noise bursts are never written to WAV or sent to a
provider. Listening pauses during transcription, reasoning, acknowledgement,
speech generation and playback so Papa Bear cannot transcribe its own output.
A microphone failure disables the mode instead of entering a retry loop.

## Providers and privacy

- the DPAPI-protected OpenAI key is used for transcription and Responses;
- ElevenLabs is the only active speech-output provider;
- microphone audio is transmitted only for an explicit PTT utterance or
  explicitly enabled voice-activated utterance;
- questions, transcripts, answers, callsigns, pre-prompts, context, provider
  bodies, credentials and audio content are not logged;
- safe diagnostics contain state/count/timing data and provider token totals,
  never conversation content;
- temporary recordings are bounded and deleted on success, failure or cancel;
- Responses uses `store: false`, which is not a provider-wide Zero Data
  Retention promise.

## Deferred voice work

Wake words, streaming STT/TTS, microphone/output-device selection, adjustable
VAD sensitivity, barge-in and radio audio effects remain deferred.
