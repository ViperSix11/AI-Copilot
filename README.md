# ArmA AI Bridge

ArmA AI Bridge is a Windows-side tactical assistant foundation for **Arma 3**. Version `0.1.0` intentionally focuses on the local data path before any cloud AI is connected:

```text
Arma 3 client SQF addon
        -> arma_ai_bridge_x64.dll
        -> Windows Named Pipe
        -> ArmA AI Bridge WPF application
```

The client addon exports only information available from the local player perspective: player state, vehicle state, group/player-known contacts, vehicle sensor contacts, current map metadata and sampled terrain objects in the direction of view.

## Version 0.1.0 scope

- Native Windows GUI in C# / WPF
- Live connection status and raw telemetry monitor
- Persistent application and file logging
- OpenAI, ElevenLabs and AssemblyAI key fields
- API keys encrypted locally with Windows DPAPI
- Client-side Arma 3 addon without CBA dependency
- Native x64 Arma extension using the official `callExtension` interface
- Directional map/environment scan for questions such as:
  - Are there buildings in the forest ahead?
  - Is the terrain ahead densely wooded?
  - How far away is the nearest detected building in my viewing direction?
- Known-contact export based on Arma's own target knowledge
- No AI requests, speech recognition or speech output yet

## Safety and multiplayer scope

The addon is read-only. It does not inject input, control the player, reveal unknown units or export the complete server world state. The native DLL is executable code; build it from this repository and do not replace it with untrusted binaries.

For initial development, test in the Editor, single-player, or a local multiplayer environment. Client extensions may require server approval, signatures and/or BattlEye handling before use on third-party multiplayer servers.

## Prerequisites

- Windows 10 or 11 x64
- Visual Studio 2022 with:
  - `.NET desktop development`
  - `Desktop development with C++`
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

If Arma 3 Tools is installed in the default Steam location, the build script also tries to pack the addon PBO. Otherwise run:

```powershell
./scripts/package-mod.ps1 -AddonBuilderPath "C:\Path\To\AddonBuilder.exe"
```

## Run a bridge test without Arma

1. Start `ArmA AI Bridge.exe`.
2. Ensure the bridge listener says `Listening`.
3. Run:

```powershell
./scripts/send-test-telemetry.ps1
```

The dashboard should display the test map, position and environment data.

## Install the Arma 3 development mod

Copy the generated `@Arma_AI_Bridge` folder into the Arma 3 installation directory and enable it in the launcher. The expected layout is:

```text
@Arma_AI_Bridge/
├── addons/
│   └── arma_ai_bridge_client.pbo
├── arma_ai_bridge_x64.dll
└── mod.cpp
```

Start the Windows application before entering a mission. The Arma addon reconnects automatically when the pipe becomes available.

## Telemetry cadence

- Core player snapshot: 4 Hz
- Known contacts and sensor contacts: 1 Hz, cached between snapshots
- Directional terrain probes: every 2 seconds, cached between snapshots

This keeps map queries useful without scanning the entire world or performing heavy terrain searches every frame.

## Data storage

Settings and logs are stored under:

```text
%LOCALAPPDATA%\ArmA AI Bridge\
├── settings.json
└── logs\arma-ai-bridge-YYYYMMDD.log
```

API keys are DPAPI-encrypted for the current Windows user. Plain API keys are never written to logs.

## Next milestone

After the local bridge has been tested in Arma 3, version `0.2.0` will add:

1. OpenAI Realtime input and reasoning
2. Tool calls against the latest telemetry snapshot
3. ElevenLabs streaming speech output
4. Push-to-talk and interruption handling
