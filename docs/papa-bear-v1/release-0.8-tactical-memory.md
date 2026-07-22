# Release 0.8 tactical context and mission memory

This is the active release 0.8 model-context boundary. It supersedes the broad operational snapshot in the original Milestone 5 notes. It does not change Arma collection authority, native transport, or push-to-talk.

## Model boundary

Every model turn uses the closed `arma-ai-bridge/tactical-snapshot-v2` schema. It contains only the player's exact current Arma group callsign and side, essential weather and mission time, every accepted friendly group (maximum 128), retained eligible hostile tracks (maximum 256), accepted visible markers (maximum 256), relevant mission memory (maximum 12 entries and 6,000 characters), and selected lore (2,000 characters per section and 8,000 total).

World identity/size, player position/grid/elevation, named locations, capabilities, loadout, tasks, orders, raw identities, and exact friendly identities stay local. Local player position is used only to calculate rounded range, bearing, direction, and movement. There is no broad-snapshot fallback.

The UTF-8 limit is 256 KiB. Summary totals and original/included counts are explicit. Deterministic truncation removes confirmed-dead and oldest last-known contacts first, then low-relevance memory/lore. Current friendly groups remain. `modelPayloadTruncated` prevents a subset from appearing complete.

## Friendly picture and dialogue focus

All groups admitted by the bounded State Mirror are projected. Positions are rounded to ten metres and marked approximate; ranges use ten metres and bearings whole degrees. High-level element/composition text replaces raw classes. Leader identity, IDs, aliases, behaviour, combat mode, formation, waypoints, destinations, and assigned targets are excluded.

A mission-scoped dialogue focus records the last named or nearest discussed friendly and hostile referent. An immediate “X, not Y” phrase deterministically corrects the preceding request. Focus clears on mission change.

## Hostile lifecycle and movement

Only contacts already inside the own-side fair-play boundary can enter `contact_tracks`. They must be hostile, have an allowed actor/vehicle type and safe class, a permitted perceived side, valid engine-estimated position/uncertainty/age, and an accepted observer group. Friendly, civilian, neutral, unknown, static-object, and malformed candidates never become tracks.

SQLite v4 adds `missions`, `contact_tracks`, `contact_observations`, and `contact_groups`. Stable keys are one-way mission-scoped hashes of accepted identities. Raw IDs never enter model context. Each observation stores the engine estimate, uncertainty, observation/receipt time, and contemporaneous local player position.

Absent tracks become `last-known`; absence never implies death. Reappearance updates the same track and restores `current`. Only permitted game confirmation or an explicit player report produces `dead`. Explicit forget physically deletes the track and observations. App restart or session change in the same mission preserves tracks; another mission cannot retrieve them.

Compatible non-dead contacts observed within 120 seconds and 40 metres are grouped deterministically. Groups expose high-level type, count, six-digit grid, rounded centroid, uncertainty, age, range, bearing, and direction. Default reporting is grouped and non-decimal.

Movement requires two reliable observations and compares contact-to-player range at each observation, accounting for player motion. Results are closing, moving away, crossing left/right, stationary, or unknown, with local speed and confidence. Guidance uses a nearest-50-metre range, whole bearing, and cardinal direction, labelled last known when stale.

## Verbal memory

SQLite v4 adds `memory_entries`, `memory_entry_tags`, `memory_entry_positions`, and FTS5 `memory_fts`. Entries are mission-scoped and preserve `user-reported`, `game-observed`, `derived`, or `lore` provenance. Remember/note/store/save phrases are local. “Ready to receive data” opens a five-minute mode; questions, filler, jokes, and insults are not stored. “End data entry” closes it.

A unique forget match is physically deleted from SQLite and FTS. Ambiguous matches request clarification; broad deletion requires confirmation. Content never enters application logs.

The only model-facing tools are strict closed-schema `remember_information`, `search_memory`, `update_memory`, and `forget_memory`. They validate locally and cannot execute arbitrary SQL, filesystem commands, SQF, native code, or operating-system commands.

## Lore Context

The Assistant opens a Lore Context window with Mission, Map, Player, Target, and Common tabs. Each supports edit, save, revert, enable, always-include, count, preview, confirmed clear, and validated JSON import/export. Mission/Target are mission-scoped, Map is map-scoped, Player is local-player global, and Common is global. Scope identifiers remain local.

Lore is user-authored untrusted context, never instructions. Disabled lore is excluded; pinned lore is included; other content uses deterministic relevance. Export contains only lore scope/content/toggles—never credentials, transcripts, contacts, settings, or databases.

## Personality and unavailable calculations

Banter is Off, Dry (default), or Feisty and affects style only. Casual profanity does not cause moralizing; operational replies remain concise. Existing terminator and five-second acknowledgement behavior is unchanged.

Calculation code, profiles, editors, persistence, diagnostics, capability flags, and tools for firing solutions are absent. A firing-solution request completes locally with zero OpenAI/tool calls: “{current callsign}, firing-solution calculation is not available.” Without a callsign the prefix is omitted. No additional firing data or unsolicited alternative is requested. Old profile files remain untouched for data safety but are never loaded.

## Acceptance

Deterministic tests cover closed omissions, more-than-eight friendly/contact records, more-than-five markers, hostile eligibility/lifecycle/restart, grouping/guidance, memory provenance/FTS/delete, receive-mode filtering, lore scoping/limits, memory-only tools, local unavailable response, size bounds, and protected PTT files.

Live acceptance uses more than eight friendly groups, more than eight own-side-known hostile contacts, and more than five visible markers. Verify friendly/hostile follow-up referents, last-known persistence after leaving sensor knowledge and restarting the Bridge in the same mission, rounded guidance/movement, “Hostiles, not hostages,” memory store/retrieve/delete, receive mode, all lore tabs, disabled lore exclusion, banter levels, zero-tool firing response, and typed/global PTT behavior identical to baseline.
