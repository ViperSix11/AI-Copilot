# ArmA AI Bridge

ArmA AI Bridge is a Windows-side tactical assistant foundation for **Arma 3**. Version `0.2.0` separates lightweight live telemetry from explicit, request-driven map analysis:

```text
Arma 3 client SQF addon
        <-> arma_ai_bridge_x64.dll
        <-> bidirectional Windows Named Pipe
        <-> ArmA AI Bridge WPF application
```

The client addon exports only information available from the local player perspective: player state, vehicle state, group/player-known contacts, vehicle sensor contacts and current map metadata. Expensive terrain and map-object analysis is no longer performed continuously. Instead, the Windows application sends a one-off `query_environment` command when a user or later the AI asks a spatial question.

## Version 0.2.0 scope

- Native Windows GUI in C# / WPF
- Persistent application and file logging
- OpenAI, ElevenLabs and AssemblyAI key fields
- API keys encrypted locally with Windows DPAPI
- Client-side Arma 3 addon without CBA dependency
- Native x64 Arma extension using the official `callExtension` interface
- Bidirectional Named Pipe communication
- Lightweight 4 Hz telemetry without fixed environment probes
- Dynamic circle and cone searches around the player
- Query categories: buildings, vegetation, roads, walls and rocks
- Query result correlation through a unique `requestId`
- Known-contact export based on Arma's own target knowledge
- No cloud AI requests, speech recognition or speech output yet

## Query model

Example request:

```json
{
  "schema": "arma-ai-bridge/command-v1",
  "requestId": "example-query-001",
  "command": "query_environment",
  "parameters": {
    "origin": "player",
    "shape": "cone",
    "direction": "view",
    "rangeMeters": 800,
    "angleDegrees": 40,
    "categories": ["building", "vegetation"],
    "maxResultsPerCategory": 25
  }
}
```

The Arma client executes the query against the currently loaded map and returns actual terrain-object positions, distances, bearings and aggregate analysis. The range is chosen per question; there are no fixed 150/350/650 metre snapshots.

## Safety and multiplayer scope

The addon is read-only. It does not inject input, control the player, reveal unknown units or export the complete server world state. The native DLL is executable code; build it from this repository and do not replace it with untrusted binaries.

For initial development, test in the Editor, single-player, or a local multiplayer environment. Client extensions may require server approval, signatures and/or BattlEye handling before use on third-party multiplayer servers.

## Prerequisites

- Windows 10 or 11 x64
- Visual Studio 2022 with `.NET desktop development` and `Desktop development with C++`
- .NET 8 SDK
- CMake 3.24+
- Arma 3 Tools for packing the PBO

## Build

Open PowerShell in the repository root:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
./scripts/build.ps1
```

This builds:

- `artifacts/app/ArmA AI Bridge.exe`
- `artifacts/mod/@Arma_AI_Bridge/arma_ai_bridge_x64.dll`
- `artifacts/mod/@Arma_AI_Bridge/addons/arma_ai_bridge_client.pbo` when Addon Builder is available

To pack only the PBO:

```powershell
./scripts/package-mod.ps1
```

## Test without Arma

Start `ArmA AI Bridge.exe`, wait for `Listening`, and run:

```powershell
./scripts/send-test-telemetry.ps1
```

This validates the inbound pipe and telemetry parser. Dynamic map queries require the native DLL and SQF addon running inside Arma 3.

## Install the development mod

Copy the generated `@Arma_AI_Bridge` folder into the Arma 3 installation directory and enable it in the launcher:

```text
@Arma_AI_Bridge/
├── addons/
│   └── arma_ai_bridge_client.pbo
├── arma_ai_bridge_x64.dll
└── mod.cpp
```

Start the Windows application before entering a mission. The addon reconnects automatically.

## Telemetry and query cadence

- Core player snapshot: 4 Hz
- Known contacts and sensor contacts: 1 Hz, cached between snapshots
- Command polling: 10 Hz
- Terrain analysis: only on an explicit query
- Query range safety limit: 1,500 metres per request
- Maximum returned objects: 50 per category

Larger future searches will be tiled and cached rather than executed as one unbounded scan.

## Data storage

Settings and logs are stored under:

```text
%LOCALAPPDATA%\ArmA AI Bridge\
├── settings.json
└── logs\arma-ai-bridge-YYYYMMDD.log
```

API keys are DPAPI-encrypted for the current Windows user. Plain API keys are never written to logs.

## Next milestone

After the live Arma query path is verified, version `0.3.0` will add:

1. OpenAI Realtime input and reasoning
2. `query_environment` as an OpenAI tool
3. Push-to-talk and interruption handling
4. ElevenLabs streaming speech output
