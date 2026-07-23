# Release 0.8.1 natural radio hotfix

This hotfix changes only Papa Bear dialogue generation, local transmission
sequencing, speech pauses, receipt-confirmation state and repetition handling.
It does not change the SQF addon, native DLL, Named Pipe protocol, canonical
State Mirror, tactical snapshot, fair-play boundary, position calculations,
mission memory schema or model-facing tool set.

## Dialogue contract

OpenAI returns one complete, concise factual answer as ordinary prose. It does
not emit stage directions, transmission delimiters, artificial pauses,
stand-by filler, copy requests or terminators. The local radio layer decides
whether the answer remains one transmission or is presented as consecutive
calls.

The model varies sentence openings and cadence, uses short urgent wording for
combat information, uses restrained conversational wording while calm, and
acknowledges corrections naturally. All tactical facts and position phrases
remain subject to the existing release 0.8 evidence and hierarchical
position-reporting rules.

## Local transmission planner

The planner receives only the current question, completed answer, current Arma
group callsign, response profile and whether the existing five-second
acknowledgement already played. It adds no tactical facts.

An answer shorter than 120 characters or without a safe sentence/clause
boundary remains one transmission. Longer answers use these bounded,
context-dependent probabilities:

- ordinary multi-part answer: 25 percent split chance;
- urgent operational answer: 45 percent split chance;
- complex calm answer: 60 percent split chance;
- direct callsign address: 75 percent for urgent information, 45 percent for
  complex calm information, and 30 percent for ordinary information;
- preparatory call after choosing a split: 10 percent in urgent conditions or
  35 percent while calm;
- receipt request: 40 percent for urgent operational information, 30 percent
  for complex operational information, and 12 percent for other operational
  information.

No receipt request is added to a response that already asks a question or uses
an `out` or custom terminator. If the five-second acknowledgement already
played, another preparatory call is forbidden. A plan contains at most one
preparatory call and two balanced content calls.

Urgent calls pause for 250–400 milliseconds. Calm calls pause for 500–850
milliseconds. There is no pause before the first call and no delay for a
single transmission. Callsign use remains exact and privacy-safe; a
preparatory call that uses it removes the duplicate prefix from the immediately
following content call.

Random selection is local. Tests inject deterministic choices. Safe logs
contain only the variation identifier, transmission count and whether receipt
confirmation was requested; they never contain answer or speech content.

## Receipt and repetition state

When Papa Bear asks `Do you copy?`, `Confirm you received that.` or `Copy?`, the
exchange remains locally open for five minutes.

- `copy`, `copied`, `roger`, `affirmative`, `yes`, `received`, `I copy` and
  `got it` confirm receipt and close the state;
- `negative`, `no`, `did not copy` and `didn't copy` reject receipt and close
  the state with a concise clarification invitation;
- `repeat`, `repeat that`, `please repeat`, `say again`, `say that again`,
  `come again` and `can you repeat that` repeat the last relevant content with
  deterministic filler removal and a `say again` lead-in.

A repeat is handled locally and does not call OpenAI or re-read the game
snapshot. A spoken repeat still requires the normal completed-utterance
transcription so the application can know what the player said. If the prior
exchange requested receipt, the simplified repeat keeps that state open.

The state clears when the player callsign changes, the five-minute window
expires, or the conversation is explicitly cleared. It contains completed
answer text only and is never written to the application log.

## Speech output

Each visible transmission is synthesized and played separately in order.
Transcript and all visible text remain committed before speech begins. A
synthesis or playback failure stops the remaining speech calls but preserves
the complete visible exchange and the existing partial-success/retry behavior.

Before every ElevenLabs call, the deterministic speech formatter converts
callsign digits, integers, grouped thousands, signed values and decimals to
English words. A final digit fallback guarantees that no ASCII digit reaches
ElevenLabs. Visible Arma callsigns remain unchanged.

## Deterministic acceptance

Tests must prove:

1. a short calm answer remains one transmission;
2. the same eligible operational answer can split or remain single according
   to an injected random choice;
3. direct callsign address is exact when selected and absent in a
   non-addressing variation;
4. an existing delayed acknowledgement suppresses a second preparatory call;
5. urgent and calm pauses remain within their exact bounds;
6. copy and negative vocabulary closes an open confirmation;
7. repeat produces simpler wording, retains material position content and does
   not call OpenAI again;
8. a callsign change cannot retain an earlier exchange;
9. multiple calls synthesize and play sequentially with one bounded pause;
10. a later-call speech failure preserves all visible text;
11. ElevenLabs input contains no digits, including `1,000`, `25` and `3.5`;
12. existing position, privacy, memory, PTT, always-on microphone, partial
    success and tool-loop tests remain green.

## Live acceptance

Use a current Arma session with a dynamic group callsign.

1. Ask a short calm question several times. Verify answers remain efficient
   and do not always split or ask for confirmation.
2. Create a longer two-part hostile update. Across repeated materially
   equivalent turns, verify both single and split delivery occur and every
   position phrase still follows Bullseye, named-reference,
   stationary-friendly and grid fallback rules.
3. Verify a split urgent call uses a visibly shorter pause than a split calm
   call and never overlaps audio.
4. When Papa Bear asks for receipt, answer `copy`, `yes`, `negative`, `no` and
   `repeat` in separate trials. Verify the state resolves or repeats as
   documented without another Responses request for the follow-up.
5. Request repetition and verify the relevant fact is retained but filler and
   sentence shape differ.
6. Change groups while a receipt request is open. Verify the old exchange
   cannot be repeated under the new callsign.
7. Include ranges `1,000`, `25` and `3.5` in a test answer and verify speech
   says `one thousand`, `twenty-five` and `three point five`.
8. Disable ElevenLabs or force playback failure during the second call. Verify
   all visible text remains and the safe partial-success status is shown.
9. Inspect logs and confirm no transcript, answer, transmission text, prompt,
   snapshot, audio or provider body was written.

## Implementation validation status

Local Windows closeout passed the 175-file UTF-8 repository verifier, all 292
deterministic Release tests, WPF `win-x64` publish, native x64 rebuild and the
official 22-file Addon Builder PBO build. GitHub Actions and the live acceptance
steps above remain pending and must not be inferred from local automation.
