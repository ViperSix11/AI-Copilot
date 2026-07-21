# Privacy, security and fair-play boundaries

## Perspective-bound information

Papa Bear may know:

- player and own-group state;
- friendly-side state shared by the mission/HQ network;
- known enemy contacts with the same uncertainty and age available to the player's side;
- static map/config data;
- mission-declared objectives, assets and capabilities;
- ACE state legitimately available on the client or explicitly shared by mission scripts.

Papa Bear may not use unrestricted server object lists to reveal unknown enemies, mines, hidden objectives or private opposing-side state.

## Multiplayer policy

Initial support remains Editor, single-player and authorized local/private multiplayer. Public-server use requires explicit server/mod approval, signing and compatibility with BattlEye/server policy. The project will not implement injection, anti-cheat evasion or hidden extension loading.

## Model access

OpenAI receives only purpose-built context required for the current turn. The local system removes unnecessary identity fields and replaces stable game identifiers with scoped references where possible.

Do not log or transmit by default:

- API keys;
- Steam/player UID;
- local Windows paths or account names;
- raw microphone audio outside active push-to-talk;
- full conversation history beyond configured memory;
- entire raw world-model databases;
- unrestricted server state.

OpenAI requests should use `store: false`. This controls response-state storage but does not by itself guarantee Zero Data Retention; provider data controls must be documented accurately.

## Tool security

- strict JSON schemas;
- local range/category/action allowlists;
- timeouts and result limits;
- authorization and capability checks;
- idempotency for mutations;
- no arbitrary code or shell tools;
- cancellation and emergency action-disable control;
- redacted diagnostic logging.

## Prompt-injection resistance

Game text, mission descriptions, object names, transcripts and tool results are untrusted data. They cannot redefine system policy, enable hidden tools or bypass confirmation. Tool authorization is implemented in code, not prompt instructions.

## Data retention

Default local retention:

- map/equipment indexes: persistent by fingerprint;
- mission world model: current mission plus optional short diagnostic retention;
- operation audit: configurable and redacted;
- conversation: memory-only unless user explicitly enables storage;
- audio: not retained.

## Transparency

Papa Bear should disclose when:

- information is stale, estimated or unavailable;
- ACE is absent or a lower-fidelity fallback is active;
- target elevation is assumed;
- an action cannot be executed due to policy or mission capability;
- a cloud service is unavailable.
