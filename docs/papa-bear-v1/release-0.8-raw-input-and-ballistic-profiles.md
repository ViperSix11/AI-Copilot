# Release 0.8 Raw Input and ballistic-profile patch

## Root causes and boundaries

`RegisterHotKey -> WM_HOTKEY` produced no activation while Arma owned focus on
the accepted live system, so release polling could never start. Release 0.8 now
uses one application-owned Raw Input receiver. It registers Usage Page `0x01`,
Usage `0x06`, exactly `RIDEV_INPUTSINK`, and verifies the target HWND. It does
not use `RIDEV_EXINPUTSINK`, `RIDEV_NOLEGACY`, `RIDEV_NOHOTKEYS`, hooks,
injection, process access, input simulation or administrator privileges.

The current modded `.338 Lapua Magnum` state identified inventory correctly but
its ACE/config profile failed the closed support gate. A local manual profile
can now complete or override ballistic characteristics without changing the
current Arma weapon, muzzle, ammunition, zero, shooter position or weather.

## Profile contract

Profiles use `arma-ai-bridge/ballistic-profiles-v1` in an independent atomic
JSON file under local application state. IDs are GUIDs. Exact class matching is
case-sensitive; empty fields are wildcards. Numeric priority precedes match
specificity and a tie fails as `ambiguous_manual_ballistic_profile`. A validated
temporary forced profile overrides automatic matching only until reset or app
restart.

Field precedence is explicit manual, valid ACE configuration, valid Arma
configuration, documented local default, then missing. Current selected classes,
zero, position and frozen mission weather always remain live state. Supported
drag labels are G1, G2, G5, G6, G7 and G8; coefficient count must equal velocity
boundary count plus one. Invalid or incomplete profiles may be saved as drafts
but cannot calculate.

The local bounded point-mass coefficient solver is deterministic and exposes
only profile display name, solver model and a compact calculated result. It is
not asserted bit-equivalent to ACE. Full coefficients, tables, notes and
provenance remain local and never enter OpenAI context, history or logs. Live
ACE comparison remains an explicit draft-PR acceptance gate.

## Voice timing

Typed and spoken turns start one Responses request and one 5,000 ms local timer.
If final text completes first, no acknowledgement occurs and the answer is not
delayed. If the answer remains pending at the threshold, exactly one local
English acknowledgement may play. The timer never starts another model request
or adds acknowledgement text to history.

## Live checklist

Use matching app, native DLL and PBO. Record Windows, Arma, CBA and ACE versions,
both processes' elevation, display mode and binding. Verify Bridge-focused,
Arma-focused and minimized PTT in windowed, borderless and fullscreen modes;
verify a custom chord, a competing `RegisterHotKey`, short tap, 15-second stop,
normal typing and Alt+Tab. Then create a profile from the current `.338 LM`
weapon and calculate at bearing 045/range 800 using actual profile values.
Compare the same frozen inputs with installed ACE tools. Ask one sub-five-second
and one over-five-second turn to verify acknowledgement timing and no overlap.
