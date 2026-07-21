# Validated action gateway

## Purpose

Papa Bear may plan and communicate support, but only a local gateway may mutate game state. The gateway exposes typed operations declared by the current mission and rejects unsupported, unauthorized or infeasible requests.

## Separation of responsibilities

```text
OpenAI reasoning        interprets intent, asks questions, proposes a plan
Local planner           converts intent into a structured operation plan
Policy/validation       checks capability, side, resources and constraints
Arma action adapter     executes only approved typed commands
Operation monitor       observes progress and publishes events
Papa Bear dialogue      reports confirmation, delay, failure and alternatives
```

No tool accepts arbitrary SQF text.

## Capability registry

Mission authors register capabilities and providers. Examples:

- rotary transport;
- ground transport;
- MEDEVAC;
- resupply;
- reconnaissance;
- vehicle recovery;
- artillery or CAS when explicitly enabled;
- marker/task management.

Each capability declares required fields, constraints, available assets, concurrency, requester permissions and cancellation rules.

## Request lifecycle

```text
received
→ clarifying
→ evaluating
→ proposed
→ approved
→ asset_assigned
→ en_route
→ executing
→ completed
```

Exceptional states:

```text
rejected
delayed
blocked
aborting
aborted
failed
```

Every transition has a reason, timestamp, actor and idempotency key.

## Example extraction request

Required planning data:

- requester/group;
- pickup point;
- destination;
- passenger count and casualty priority;
- candidate assets and readiness;
- landing-zone suitability;
- known threats and route risk;
- fuel, crew and capacity;
- mission constraints.

The system may answer:

- approved with asset and ETA;
- unable with reason;
- alternative asset/method;
- alternate pickup or destination;
- request for missing confirmation.

## Confirmation policy

Read-only information tools do not need confirmation. Actions are classified:

- `automatic`: reversible, low-impact mission-defined actions;
- `confirm`: player confirmation required before execution;
- `restricted`: mission-author or server authorization required;
- `disabled`: never exposed.

The policy is local and cannot be overridden by prompt text.

## Monitoring

Active operations persist independently of a single OpenAI response. The operation monitor consumes Arma events and state changes, updates the state machine and can trigger proactive radio messages for meaningful changes such as ETA, landing, blocked route, damage or cancellation.

## Reliability

- commands are idempotent;
- timeouts do not imply failure without reconciliation;
- reconnect restores operation state from Arma/mission state;
- all mutations are audit-logged without storing API keys or conversation audio;
- a manual emergency stop disables all action tools while preserving read-only dialogue.
