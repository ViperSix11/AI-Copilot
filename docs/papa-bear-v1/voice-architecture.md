# Voice architecture

## Role of the voice layer

Voice is an input/output adapter around the same Papa Bear orchestrator used by typed chat. It does not own world state, tools, memory or action policy.

## Target pipeline

```text
Push-to-talk microphone audio
→ AssemblyAI streaming speech-to-text
→ final turn transcript
→ Papa Bear orchestrator and OpenAI tool loop
→ final radio response text
→ ElevenLabs streaming text-to-speech
→ radio audio effects and playback
```

The AssemblyAI tutorial combining streaming STT, OpenAI and ElevenLabs is a useful prototype pattern, but its linear receptionist design is not the application architecture. Game telemetry continues to update while transcription, reasoning or speech playback is active.

## Input behavior

- primary mode: push-to-talk;
- optional later mode: voice activation with explicit wake phrase;
- partial transcript shown in UI;
- final transcript emitted only after end-of-turn detection;
- custom vocabulary/key terms for callsigns, map names, ACE terminology, units and military phrases;
- quick cancel/retry after a bad transcript;
- typed input always remains available.

Suggested key terms include:

```text
Papa Bear
MEDEVAC
LZ
MRAD
MOA
windage
elevation
bearing
ATragMX
ACE
callsigns and current map locations
```

## Output behavior

- ElevenLabs voice ID stored encrypted with other credentials;
- streaming playback to reduce time-to-first-audio;
- optional radio band-pass, compression, squelch and start/end tones;
- spoken text and visual transcript generated from the same final response;
- numbers and bearings formatted for intelligible radio speech;
- ability to interrupt or cancel playback;
- urgent operation updates can be queued by priority.

## State machine

```text
idle
→ listening
→ transcribing
→ reasoning
→ waiting_for_tool
→ speaking
→ idle
```

Cancellation and error transitions exist from every active state. The UI must show the current state and last actionable error.

## Concurrency

- telemetry and operation monitoring never pause;
- only one user dialogue turn is committed at a time in v1;
- background operation events are queued and may interrupt only under configured priority rules;
- TTS playback must not block the Named Pipe or world-model update loop.

## Deployment choice

Preferred final implementation is C# in the existing .NET application using direct WebSocket/HTTP integrations. A Python voice worker is acceptable only as an isolated prototype or if SDK capability proves materially better; it must communicate with the C# orchestrator over a local typed protocol and must not maintain a second game-state model.

## Privacy

Microphone audio is transmitted only while push-to-talk is active. The UI must indicate transmission clearly. Audio and transcripts are not persisted by default. Provider retention behavior must be documented in settings before voice is enabled.
