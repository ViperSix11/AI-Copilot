# Voice architecture

## Active release 0.8 pipeline

Voice is an adapter around the same turn service used by typed chat. It owns no
world state, interpreter, conversation history or tool policy.

```text
hold push-to-talk (maximum 15 seconds)
→ bounded WAV
→ OpenAI completed-utterance transcription
→ visible final transcript
→ shared AssistantTurnService
→ fresh minimized snapshot with interpreted location, bounded base state and
  deterministic question-relevant State Mirror sections
→ one OpenAI Responses/tool loop
→ locally normalized final visible answer
→ ElevenLabs synthesis of that exact text
→ Windows playback
```

An ordinary position question has no tool round because the deterministic
interpretation is in the initial snapshot. Transcription is a separate OpenAI
request; “one Responses request” refers to the reasoning stage and does not
misdescribe STT as part of Responses.

## Shared turn behavior

- typed and spoken input use the same current world snapshot, deterministic
  state selector and interpreters, response profile, OpenAI history and strict
  tool dispatcher;
- response profiles are local style data and cannot override immutable rules;
- the final assistant answer is normalized once before it enters assistant
  history, the UI, ElevenLabs or replay;
- Over/Out suffixes are removed and the configured terminator is appended once;
- replay synthesizes or replays the last normalized answer and never repeats STT
  or Responses.

## State and failure behavior

```text
ready → recording → transcribing → thinking
      → generating-voice → speaking → ready
```

The transcript becomes visible immediately after successful transcription. The
answer becomes visible immediately after successful Responses. TTS or playback
failure is partial success: text remains usable and retryable. Cancellation is
available at every asynchronous stage and does not block bridge ingestion.

## Providers and privacy

- one DPAPI-protected OpenAI key is used for transcription and Responses;
- ElevenLabs is the only active speech-output provider;
- microphone data is transmitted only for explicit transcription or hold-to-talk;
- questions, transcripts, answers, style text, prompts, snapshots, provider
  bodies, credentials and audio content are not logged;
- temporary recordings are bounded and deleted on success, failure or cancel;
- Responses uses `store: false`, which is not a Zero Data Retention promise.

## Deferred voice work

Always-on listening, wake words, VAD, streaming STT/TTS, device selection,
global hotkeys and audio effects are not release 0.8 dependencies.
