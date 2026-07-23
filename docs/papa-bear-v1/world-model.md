# Local world model

## Purpose

The local world model is the current product's source of truth for authorized
mission information. It ingests versioned Arma messages, normalizes stable
mission-scoped identities where available, preserves provenance and age, and
serves bounded context to deterministic services and OpenAI.

Raw telemetry is evidence, not provider context. The model never receives a
direct database export.

## Information layers

The 0.9.1 baseline maintains four related layers:

1. **Current picture** — the latest accepted `state-snapshot-v2`, published
   every four seconds with independently sampled sections.
2. **Developing picture** — bounded consecutive snapshots used for new or
   reacquired contact development before a detailed proactive report.
3. **Mission memory** — mission/session-scoped SQLite state containing contact
   tracks and observations, player-message journal entries, structured
   reports/corrections/retractions, lore and explicit reported locations.
4. **Conversation context** — recent typed/transcribed user and assistant turns
   retained locally and retrieved only when relevant.

Official named locations are a separate in-memory gazetteer assembled from the
active world's allowed `CfgWorlds` name classes. A separate local
map-intelligence store can hold explicitly authored information, but model
access is permission-gated and bounded. Neither is a complete static map index.

## Current state domains

- player side and current Arma group callsign;
- locally retained player position/grid for authorized deterministic use;
- overcast and mission time/astronomy;
- player loadout summary;
- own-side groups, units and crewed vehicles;
- side-known hostile and unidentified contacts;
- mission tasks and locally visible mission references;
- read-only mission-declared assets and capabilities;
- official named locations.

The canonical player position is deliberately withheld from ordinary OpenAI
context. Temperature, wind, ACE data, trajectory data and unrestricted object
state are not collected.

## Provenance and state semantics

Stored or derived facts carry the fields needed to preserve:

```text
mission/session scope
privacy-safe entity or track identity
source/provenance class
observed game time and received UTC time
current, recent, stale, last-known or historical state
deterministic confidence
position uncertainty where applicable
corroboration, contradiction and reporter callsigns where available
```

Current state, a player report and a local derivation are not interchangeable.
Corroboration may raise confidence; contradiction remains visible until later
evidence resolves it. A derived relationship retains the authority limits of
its inputs.

## Identity and privacy

Mission-scoped source identifiers are used only for local joins. The desktop
hashes or replaces them with local aliases before diagnostics or retrieval.
Player profile names and UIDs are not collected. Raw netIds, source IDs,
database aliases and mission/session source identifiers never enter ordinary
OpenAI context.

The current `groupCallsign` comes from `groupId (group player)` on every player
collection cycle. It is not permanently cached across group or session changes.

## Friendly-force and known-contact policy

Friendly-force state is limited to the player's current side and the existing
mission/network perspective. It includes groups, units, crewed vehicles and
bounded equipment summaries needed by read-only resource questions.

Hostile and unidentified contacts come only from own-side
`targets`/`targetKnowledge` and the engine-estimated knowledge available to
eligible friendly representatives. The model retains estimated position,
uncertainty, age, lifecycle and privacy-safe reporter callsigns. It never
hydrates a contact from hostile `getPos*`, unrestricted server truth,
waypoints, orders, targets or inventory.

## Ingestion and reconciliation

The ingestion path:

1. validates message schema, protocol/session and sequence;
2. hashes transport identities and normalizes units;
3. atomically replaces current sections in SQLite;
4. preserves the last good value when a section fails or becomes unavailable;
5. applies periodic full reconciliation and mission/session reset;
6. updates contact tracks and observation history without collapsing distinct
   evidence;
7. emits bounded change events for the developing picture.

A successfully sampled empty section authoritatively clears that section.
Out-of-order snapshots are rejected.

## Retrieval for reasoning

OpenAI starts with a small plain-English seed containing the current player
message or event summary, callsign/side and available information-area names.
It may inspect the catalogue and request narrow context categories. Local code
validates every request, applies scope/result limits and privacy projection,
then formats the selected records as short English facts.

The provider does not receive SQL rows, schemas, aliases, raw timestamps,
serialized query envelopes, the entire current picture, the complete
gazetteer, or complete mission memory. Exhausted retrieval ends with a
tool-free synthesis request so a usable reply is not discarded.
