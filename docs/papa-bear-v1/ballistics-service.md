# Deterministic firing-solution service

## Rule

OpenAI may understand the request, select inputs and explain the result. It must never be the numerical ballistic solver. Every value presented as a firing solution comes from a deterministic, versioned service using the active weapon/ammunition/scope/environment profile.

Release 0.8 additionally supports local user-managed profiles under the closed
`arma-ai-bridge/ballistic-profiles-v1` schema. Exact class matches are
case-sensitive, blank match fields are wildcards, explicit priority precedes
specificity and equal candidates fail closed. Manual values override ballistic
characteristics only; current weapon/ammunition identity, zero, shooter position
and frozen weather remain authoritative live Arma state. Full coefficient and
velocity/temperature/barrel tables never enter OpenAI context or logs.

## User interaction

Minimum request:

```text
rangeMeters
bearingDegrees
```

Optional inputs:

- target elevation or inclination angle;
- target movement and lead request;
- manually measured wind;
- ammunition temperature;
- alternative ammunition/profile;
- requested unit: clicks, MRAD or MOA.

Papa Bear automatically retrieves the current player weapon, ammunition, optic, position, elevation and ACE atmosphere. Missing material inputs cause a clarification or an explicitly qualified fallback.

## Provider architecture

```text
IBallisticsProvider
├── AceBallisticsProvider       preferred when a supported public ACE path exists
├── LocalAceCompatibleProvider  deterministic local solver validated against ACE
└── BaseArmaProvider            explicitly lower-fidelity fallback
```

Provider selection and fidelity are reported in every solution. Implementation must first determine which ACE public functions/profile data can be reused safely. Do not bind the product to undocumented function names without a versioned adapter.

## Required inputs

- weapon/muzzle class;
- barrel length;
- twist rate and direction;
- magazine/ammunition class;
- projectile mass and diameter;
- muzzle velocity and applicable temperature/barrel corrections;
- ballistic coefficient and drag model;
- current zero range;
- bore height and scope base angle;
- scope click unit, click size, current dial and limits;
- shooter position/elevation;
- target range, bearing and elevation/inclination;
- temperature, humidity and pressure;
- wind vector/profile;
- map latitude/Earth-effect inputs when used by active ACE settings;
- enabled ACE ballistic feature flags.

## Required outputs

```text
elevationAngular
windageAngular
elevationClicks
windageClicks
directionOfWindage
timeOfFlightSeconds
impactVelocity
transonicStatus
spinDriftContribution
coriolisContribution
remainingScopeTravel
holdRecommendation
provider
profileFingerprint
assumptions
confidence
```

The service converts angular corrections to the actual optic's click unit. Rounding rules must be explicit and reversible.

## Target point from range and bearing

When the player provides range and bearing only, project a target coordinate from the current shooter position. Use indexed terrain elevation as a provisional target elevation and label the assumption. A target on a roof, tower or airborne platform requires an explicit height correction.

## Validation

A test matrix must cover representative ACE-supported combinations across:

- ranges before and after transonic transition;
- temperatures, humidity and pressure;
- headwind, tailwind and crosswind;
- uphill/downhill shots;
- left/right twist;
- multiple barrel lengths;
- MRAD and MOA optics;
- dial-limit overflow and holdover fallback.

Reference results should be compared with ACE Range Card/ATragMX behavior under identical mission conditions. Tolerances are defined per output and provider before release.

## Example response

> “Papa Bear copies. Seven-four-zero meters, bearing zero-six-two. Using the current rifle, loaded ammunition and ACE atmosphere: dial 63 clicks up and 11 clicks left. That is 6.3 mil elevation and 1.1 mil left windage. Target height is assumed at terrain level. Over.”
