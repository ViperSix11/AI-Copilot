# Privacy, security and fair-play boundaries

## Perspective-bound information

Papa Bear may use measured player/current-vehicle state, mission-authorized
own-side state, contacts already available through the accepted perspective
model, read-only mission assets/capabilities, bounded manual environment-query
results and official active-world named-location configuration.

Official names are cartographic facts. They reveal no runtime units, objects or
enemy state. Release 0.8 reads only `CfgWorlds >> worldName >> Names`; it does
not enumerate terrain objects, mission objects, buildings, roads, vegetation,
vehicles or units for the gazetteer.

Papa Bear may not use unrestricted server lists or opposing-side truth to reveal
unknown enemies, mines, objectives or private state. Public-server use requires
server/mod approval; the project does not implement injection or anti-cheat
evasion.

## Model boundary

OpenAI receives only the current purpose-specific snapshot and bounded selected
tool results. The complete local gazetteer, raw pages and raw 4 Hz telemetry are
never sent. Position context contains the measured position plus no more than
three deterministic named references.

Profile names, UIDs, raw engine IDs and source mission/session identifiers stay
out of model context. Map configuration text and custom style text are untrusted
data and cannot redefine factual, fair-play or tool policy.

## Tool and prompt security

- strict local schemas, allowlists, numeric bounds, result limits and timeouts;
- no arbitrary SQF, C++, PowerShell, shell or operating-system commands;
- current tools remain read-only;
- immutable rules precede measured/derived facts, style profile and question;
- custom profile text is capped at 2,000 characters, sanitized and style-only;
- distances, bearings, directions, containment and freshness are local results,
  not model calculations.

## Retention and logging

- API keys are DPAPI-encrypted; response profiles are ordinary local settings;
- world state, conversation and the named-location gazetteer are memory-only for
  their current scopes;
- no SQLite map index, map fingerprint cache or operational-memory database is
  active in release 0.8;
- audio, questions, transcripts, answers, custom style, full prompts, snapshots,
  tool payloads, raw gazetteer pages, provider bodies and credentials are not
  logged;
- temporary WAV files are deleted after success, failure or cancellation;
- OpenAI Responses uses `store: false`; provider retention policy remains an
  external account/data-control consideration.

## Transparency

Papa Bear must preserve live versus last-known status, disclose unavailable or
stale information, fall back to world/grid when no valid name exists and never
invent a place, distance, bearing, coordinate or hidden contact.
