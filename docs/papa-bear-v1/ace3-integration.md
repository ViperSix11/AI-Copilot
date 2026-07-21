# ACE3 integration

## Objective

ACE3 is a specialized simulation source for medical state, weather, scopes, weapon/ammunition configuration and advanced ballistics. Papa Bear should consume ACE data rather than approximate it.

## Adapter boundary

All ACE access is isolated behind `IAceAdapter`. The rest of the application depends on stable internal DTOs, not ACE variable names.

```text
IAceAdapter
├── DetectCapabilities()
├── GetMedicalSummary(unit)
├── GetWeatherState(position)
├── GetWeaponProfile(player)
├── GetScopeProfile(player)
├── GetBallisticProfile(player)
└── SubscribeToAceEvents()
```

Prefer documented ACE frameworks and CBA events. Any unavoidable private/internal variable access must be:

- version-gated;
- covered by compatibility tests;
- isolated in one implementation file;
- allowed to fail gracefully without breaking base Arma support.

## Capability detection

At session start publish a matrix such as:

```text
ace.present
ace.version
ace.medical
ace.weather
ace.scopes
ace.advancedBallistics
ace.atragmx
ace.rangeCard
ace.windSimulation
```

Capabilities are not inferred solely from installed PBOs; active mission/settings state must also be considered.

## Medical summary

For own-side units, normalize only operationally useful information:

- conscious/unconscious;
- alive/dead;
- stable/unstable;
- transportable;
- broad bleeding/pain/shock status;
- treatment or medic requirement;
- estimated evacuation priority.

Detailed wound data may be retained locally but should not be sent to OpenAI unless required for the question or request.

## Weather and atmosphere

Capture the values used by ACE where available:

- temperature;
- humidity;
- air pressure;
- wind vector and speed;
- wind simulation state;
- altitude and geographic/map context;
- observation time.

ACE Weather extends the base weather model with temperature, humidity and pressure; Advanced Ballistics uses atmospheric density and wind effects. The adapter must report whether each value is measured from ACE or falls back to base Arma/default atmosphere.

## Weapon, ammunition and scope profiles

Normalize:

- weapon, muzzle, magazine and ammunition class;
- barrel length, twist and twist direction;
- muzzle velocity and temperature/barrel-length dependencies;
- projectile mass, diameter, ballistic coefficient and drag model;
- zero range, bore height and scope base angle;
- scope unit, click unit, click value and adjustment limits;
- currently dialed elevation and windage where exposed;
- range-card/ATrag profile identity and source.

## Version and fingerprinting

The ACE version, relevant config data and loaded compatibility mods contribute to the equipment/ballistics fingerprint. A profile cache is invalidated when the fingerprint changes.

## Failure behavior

- No ACE: base Arma data remains available and ACE-only features report unavailable.
- Partial ACE: publish exact capability gaps.
- Unsupported ACE version: disable affected feature, log a precise compatibility warning and continue safely.
- Never substitute an LLM estimate for missing ACE data.

## Research task before implementation

Codex must inspect the current ACE3 source and public framework documentation for the exact supported functions/events. The architecture does not authorize direct dependency on guessed variable names.
