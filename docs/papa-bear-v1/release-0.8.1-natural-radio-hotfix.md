# Release 0.8.1 natural radio hotfix

This hotfix changes Papa Bear dialogue generation, local transmission
sequencing, speech pauses, receipt-confirmation state, repetition handling,
contact presentation, bounded dialogue topics and the State Mirror publication
cadence. The SQF addon changes only the existing publication gate from four to
eight seconds. It does not change section collectors, the native DLL, Named
Pipe schema, fair-play boundary, position calculations, mission-memory
database schema or model-facing tool set.

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
  `got it`, plus the continuation replies `proceed`, `continue` and `go ahead`,
  confirm receipt and close the state;
- `negative`, `no`, `did not copy` and `didn't copy` reject receipt and close
  the state with a concise clarification invitation;
- `repeat`, `repeat that`, `please repeat`, `say again`, `say that again`,
  `come again` and `can you repeat that` repeat the last relevant content with
  deterministic filler removal and a `say again` lead-in. Natural references
  such as `the last information` and `state the last information` are also
  repeat requests.

A repeat is handled locally and does not call OpenAI or re-read the game
snapshot. A spoken repeat still requires the normal completed-utterance
transcription so the application can know what the player said. If the prior
exchange requested receipt, the simplified repeat keeps that state open.

The receipt state is never a modal conversation lock. Any new substantive
question, report or otherwise unrecognized utterance implicitly leaves the
receipt exchange and continues through the normal snapshot and OpenAI path.
The state also clears when the player callsign changes, the five-minute window
expires, or the conversation is explicitly cleared. It contains completed
answer text only and is never written to the application log.

## Proactive-announcement presentation deduplication

Track identity and world-model state remain unchanged. Before speaking,
new and reacquired transitions are held locally for three seconds. Compatible
tracks are then clustered when their relationship matches, their observation
times are within 120 seconds and their positions are no more than 250 metres
apart. Exact matching rendered-position phrases remain compatible. A cluster
produces one counted announcement.

After a cluster is announced, another cluster with the same relationship and
contact-type composition within 250 metres is silent for 30 seconds. This
prevents incrementally discovered members from generating repeated
near-identical calls across consecutive snapshots. A different relationship
or classification, such as an unknown aircraft beside hostile infantry, still
produces its own call. The database retains every track and observation;
clustering and cooldown are presentation-only and reset with the session.

A request for an enemy's `last known position` selects the newest eligible
contact positions, including contacts whose status is still current. Papa Bear
reports a current position as current rather than incorrectly treating
`last-known` as a required database status.

## Hostile-strength interpretation and bounded topic continuity

`combatant`, enemy/hostile strength, contact-count and equivalent questions
select a deterministic hostile-strength projection. It contains current and
last-known accepted-contact counts, current identified composition, geographic
presentation-cluster count and an explicit warning that observations can
overlap and vehicle crews can be unknown. It never estimates unseen forces.

The local snapshot builder retains the `hostile-strength` topic for at most two
compatible estimate follow-ups. Therefore `Do you have an approximation?`
immediately after a combatant-count question resolves to hostile strength and
selects the same current projection. Any unrelated question clears the topic;
session change and AI Context reset clear it as well. This state does not block
normal questions and is not persisted.

## State Mirror load reduction

The SQF section caches continue sampling player state every second,
friendly/contact state every two seconds, loadout/tasks/markers every four
seconds and environment/time every eight seconds. The bounded
`state-snapshot-v2` envelope is now published every eight seconds instead of
four. SQLite therefore receives approximately seven or eight routine snapshot
transactions per minute rather than fifteen. The first useful cache gate,
30-second full reconciliation and all schema/privacy rules remain unchanged.

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
12. a substantive new question leaves a pending receipt state and reaches
    OpenAI, while `the last information` repeats locally;
13. distinct compatible tracks with the same rendered tactical position
    produce one counted announcement without absorbing a different contact
    classification;
14. three-second batching and the 250-metre/30-second presentation policy
    suppress incremental same-cluster chatter without deleting tracks;
15. combatant-count and immediate approximation follow-ups select the same
    hostile-strength estimate and unrelated questions clear the topic;
16. the SQF contract publishes envelopes every eight seconds while retaining
    the established 1/2/4/8-second section sampling;
17. existing position, privacy, memory, PTT, always-on microphone, partial
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
5. While a receipt request is open, ask a new substantive question such as
   `Where are the enemies right now?`. Verify Papa Bear answers it instead of
   repeating a confirmation instruction.
6. Request repetition using both `repeat` and `the last information`. Verify
   the relevant fact is retained but filler and
   sentence shape differ.
7. Cause two compatible vehicle tracks to resolve to the same rounded tactical
   position. Verify one counted vehicle-group call is emitted, while an
   aircraft with a different classification still gets its own call.
8. Reveal several infantry contacts in the same 250-metre area over consecutive
   snapshots. Verify one batched cluster call and no incremental repeat during
   the next 30 seconds. Verify a differently classified contact still announces.
9. Ask `How many combatants are we talking about?`, followed by `Do you have an
   approximation?`. Verify both answers use the current supported contact
   estimate, while a later unrelated question does not retain that topic.
10. Observe State Mirror diagnostics for at least one minute. Verify roughly
   one accepted sequence every eight seconds and no four-second duplicate.
11. Change groups while a receipt request is open. Verify the old exchange
   cannot be repeated under the new callsign.
12. Include ranges `1,000`, `25` and `3.5` in a test answer and verify speech
   says `one thousand`, `twenty-five` and `three point five`.
13. Disable ElevenLabs or force playback failure during the second call. Verify
   all visible text remains and the safe partial-success status is shown.
14. Inspect logs and confirm no transcript, answer, transmission text, prompt,
   snapshot, audio or provider body was written.

## Implementation validation status

Local Windows closeout passed the 176-file UTF-8 repository verifier, all 301
deterministic Release tests, WPF `win-x64` publish, native x64 rebuild and the
official 22-file Addon Builder PBO build. GitHub Actions and the live acceptance
steps above remain pending and must not be inferred from local automation.
