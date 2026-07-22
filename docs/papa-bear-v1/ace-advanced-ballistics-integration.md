# ACE3 Advanced Ballistics integration (release 0.8 draft)

## Scope and decision

This patch explicitly activates the otherwise deferred ACE ballistic work for
release 0.8. Papa Bear keeps one model-facing `calculate_firing_solution` tool
and dispatches locally:

```text
ACE Advanced Ballistics absent or disabled -> VanillaBallisticSolver
ACE Advanced Ballistics enabled and supported -> ACE v3.21 runtime adapter
ACE enabled but unsupported -> precise fail-closed result
```

The selected ACE path is an isolated SQF adapter named for its supported
baseline. It calls the already loaded, non-mutating ACE ATragMX calculation
function with range-card storage disabled; that function uses the loaded ACE
extension's retardation and atmospheric commands. The desktop never attempts
to reproduce ACE from Vanilla
`airFriction`/`coefGravity`, and the native Named Pipe extension remains opaque
transport.

## Source audit and baseline

The implementation was designed against the official ACE3 `v3.21.0` tag
(released 1 April 2026), which requires CBA 3.18.5 or later and Arma 3 2.20 or
later. The audited sources are:

- `docs/wiki/framework/advanced-ballistics-framework.md` and the Advanced
  Ballistics feature documentation;
- `addons/advanced_ballistics` settings, configuration readers, fired handler,
  per-frame handler and terrain initialization;
- `extension/src/ballistics`, especially `bullet:new`, `bullet:simulate`,
  `bullet:delete`, atmosphere, drag, map and zeroing;
- ACE Scopes bore-height, current-zero and projectile-direction correction;
- Range Card and ATragMX calculation paths, including their working-memory and
  UI coupling;
- ACE3's GPL-2.0-or-later project licence and additional PBO redistribution
  permission.

### Public and stable interfaces

The ACE Advanced Ballistics framework documents the weapon/ammunition config
fields used by compatibility mods. `ace_scopes_fnc_getBoreHeight` is marked
public in ACE source. CBA setting values are mission-namespace settings and are
the authoritative activation/configuration state once settings initialize.

### Internal and version-sensitive interfaces

ACE marks the ammo/weapon config readers, fired/PFH handlers, ATragMX solver,
Range Card solver, ACE Scopes current-zero helper and zero-correction helper as
non-public. The `ace` extension's `ballistics:*` command protocol is also an
implementation detail, not a compatibility promise. Papa Bear therefore does
not claim general future-version compatibility with those interfaces.

ATragMX is not used as shared state: it owns mutable working memory, gun lists,
truing data and UI state. Range Card is not used as shared state either. Both
remain independent live comparison references.

## Exact runtime capability probe

The adapter reports five distinct states:

1. `ace_advanced_ballistics` absent from `CfgPatches`: `vanilla`;
2. PBO present and `ace_advanced_ballistics_enabled` false: `vanilla`;
3. enabled but ACE version is not `3.21.x`:
   `ace_advanced_ballistics_version_unsupported`;
4. enabled but required functions or a successful bounded extension probe are
   absent: `ace_advanced_ballistics_interface_unsupported`;
5. enabled, version-supported, interface-supported and the selected profile is
   valid: `ace3-advanced`.

The interface probe requires the current mission/session, the supported ACE
version, the ATragMX calculation function, required ACE weather/scope/profile
functions and a successful `ballistics:retard` status/payload check. Presence
of a PBO alone is never treated as activation or support. The result is cached
only within the current State Mirror session and is re-evaluated after reset.

The current shot receives a local profile fingerprint covering session,
weapon, muzzle, mode, magazine, ammunition, zero, ACE version and relevant CBA
settings. The bounded calculation command accepts this fingerprint, not model-
chosen class names. A mismatch before, during or after calculation returns
`weapon_changed_during_calculation`.

## Profile validation and multi-muzzle behavior

The selected `currentWeapon` and `currentMuzzle` choose the weapon/muzzle config.
For secondary muzzles the nested muzzle class is used. The adapter accepts only
the ACE runtime's supported `BulletBase` path and validates:

- positive finite caliber, length and mass where stability requires them;
- drag model in `1, 2, 5, 6, 7, 8`;
- exactly one positive finite ballistic coefficient and no velocity boundary;
- `ICAO` or `ASM` atmosphere;
- equal, bounded, finite barrel-length/muzzle-velocity tables;
- eleven bounded ammunition-temperature shifts when that influence is enabled;
- finite barrel twist/length and twist direction `-1`, `0` or `1`;
- all environment, zeroing, position and setting values before extension use.

Multi-segment coefficient profiles, ACE-enabled shells, powered/guided
projectiles, submunitions, artillery-computer profiles, custom ammo flight
handlers and incomplete mod profiles fail closed. The one-coefficient limit is
intentional because the audited non-mutating ACE calculation API accepts one
coefficient; silently choosing among segments would be inaccurate. Vanilla is
never selected while ACE Advanced Ballistics is enabled.

## Nominal muzzle velocity

The frozen initial speed starts with the same current magazine, weapon/muzzle
override and attached muzzle-device coefficients used by the existing profile.
When enabled, the adapter applies ACE's barrel-length interpolation and the
ammunition-temperature shift at shooter height. It deliberately does not sample
ACE muzzle-velocity variation. A non-zero configured standard deviation is
returned as bounded nominal-variation metadata, so equal frozen inputs produce
equal results while the player is told the solution is nominal.

## Trajectory, environment and zero reference

The adapter freezes shooter ASL position, target method, range, bearing, wind,
ACE map latitude, temperature, humidity, barometric pressure, current zero,
scope geometry and the current shot profile. It calls the version-gated ACE
ATragMX solver with range-card storage disabled and a fixed twenty simulation
steps per second; the audited solver itself bounds flight time at fifteen
seconds. The command adds an eight-second wall-clock/result-size boundary and
checks session, weapon, muzzle and magazine again before returning.

ACE Scopes' public bore-height helper and version-gated current-zero semantics
are used for the primary muzzle. The current correction includes the frozen ACE
scope elevation/windage adjustment and the same zero-reference temperature,
pressure and humidity used by ACE Scopes. Secondary muzzles use their current
engine zero because ACE Scopes deliberately ignores them. If an active optic or
scope configuration cannot be represented reliably, the adapter returns
`ace_scope_zero_unsupported` rather than inventing a correction.

The result distinguishes required vertical and horizontal correction, hold
direction, current zero, time of flight, impact speed, Vanilla versus ACE,
nominal velocity and variation metadata. It never returns optic clicks or a
real-world firing instruction.

## Resource ownership and cleanup

The selected strategy never invokes `ballistics:bullet:new`, so it allocates no
temporary ACE bullet ID and has no extension simulation state to leak on
success, failure, timeout or cancellation. Range-card storage is explicitly
false, and the adapter neither reads nor writes saved ATragMX working memory.
It creates no Arma projectile, consumes no ammunition, fires no event handler
and changes no Range Card, ATragMX or ACE profile state. A future switch to the
stateful bullet protocol would require one cleanup path with validated
`bullet:delete` on every exit before it could be supported.

## Licensing

The project calls the user's installed ACE runtime and reads public config. It
does not vendor, modify or redistribute ACE files and does not copy ACE solver
source. The design document cites the source behavior needed for compatibility.
This avoids creating a derivative copy of ACE in the Papa Bear repository;
ACE3's licence still governs the installed ACE distribution independently.

## Protocol and prompt boundary

`state-snapshot-v2` exposes only bounded capability/profile metadata needed for
local dispatch: mode, ACE presence/enabled/supported state, supported baseline,
profile fingerprint, nominal-variation metadata and safe reason. Full ACE
coefficient arrays, raw globals and extension payloads remain inside Arma. They
never enter diagnostics, logs, conversation history or the OpenAI snapshot.

The internal `calculate_ace_firing_solution` command accepts only bounded range,
bearing, one target-elevation method and the frozen opaque profile fingerprint.
Only its validated compact completed result reaches the existing model tool
continuation. No second Responses request is introduced.

## Automated acceptance

Deterministic tests use fake ACE adapters and checked-in SQF/schema fixtures.
They cover inactive/disabled/active dispatch, no Vanilla fallback while active,
secondary muzzle collection, malformed profile arrays/models/atmosphere,
nominal variation determinism, bounded/version-gated non-mutating invocation,
weapon/session mismatch, correction serialization, compact prompt boundary,
one Responses turn, Vanilla regressions and unchanged native source.

## Live acceptance (required before PR leaves draft)

Use matching application, native DLL and PBO on Stratis. Record Arma, CBA and
ACE versions plus weapon, muzzle, magazine, ammunition and all relevant ACE
settings. Verify disabled ACE selects Vanilla and enabled supported ACE selects
`ace3-advanced`, then test a supported Vanilla rifle profile, the Vector `.338
LM`, a muzzle device and an unsupported launcher at 100, 300, 600 and 1,000
metres where practical. Cover calm/crosswind, temperature, target above/below,
two zero settings, barrel-length influence and muzzle-velocity variation.

Compare against the matching ACE Range Card or ATragMX: vertical and horizontal
correction within 0.1 milliradian, time of flight within 1% or 0.02 seconds, and
impact velocity within 1%. Do not loosen a failed tolerance silently. Confirm no
projectile, sound, flash, ammunition use, fired handler, saved-profile mutation,
RPT error or leaked extension state. Repeat twenty calculations and exercise
cancellation/timeout while watching latency and memory. Until this live gate is
recorded, compatibility is implementation-complete but not live-accepted and
PR #12 remains draft.
