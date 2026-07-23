#!/usr/bin/env python3
"""Dependency-free consistency checks for the ArmA AI Bridge repository."""

from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SKIP_PARTS = {".git", "artifacts", "bin", "obj", "build", ".vs"}

FORBIDDEN_MARKERS = (
    "AI" + " Copilot",
    "AI" + "Copilot",
    "AI_" + "Copilot",
    "ai_" + "copilot",
)

EXPECTED_PATHS = (
    ".github/workflows/build.yml",
    "docs/papa-bear-v1/README.md",
    "docs/papa-bear-v1/world-model.md",
    "docs/papa-bear-v1/privacy-and-fair-play.md",
    "scripts/package_pbo.py",
    "arma3/@Arma_AI_Bridge/mod.cpp",
    "arma3/addon-source/arma_ai_bridge_client/config.cpp",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_pollCommands.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_executeQuery.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_queryEnvironment.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_sendQueryResult.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_collectFriendlyForces.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_collectMissionCapabilities.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_getStableEntityId.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_initialiseSession.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishMissionCapabilities.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishSessionHandshake.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishWorldEvent.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishMapGazetteer.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_publishStateSnapshot.sqf",
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_updateFriendlyForcePicture.sqf",
    "native/ArmaAiBridge/ArmaAiBridge.cpp",
    "native/ArmaAiBridge/CMakeLists.txt",
    "schemas/telemetry-v1.schema.json",
    "schemas/command-v1.schema.json",
    "schemas/query-result-v1.schema.json",
    "schemas/session-handshake-v1.schema.json",
    "schemas/friendly-force-snapshot-v1.schema.json",
    "schemas/friendly-force-delta-v1.schema.json",
    "schemas/mission-capabilities-v1.schema.json",
    "schemas/map-gazetteer-v1.schema.json",
    "schemas/state-snapshot-v2.schema.json",
    "samples/telemetry-v1.json",
    "samples/query-command-v1.json",
    "samples/query-result-v1.json",
    "tests/fixtures/session-handshake-v1.json",
    "tests/fixtures/friendly-force-snapshot-v1.json",
    "tests/fixtures/friendly-force-delta-v1.json",
    "tests/fixtures/mission-capabilities-v1.json",
    "tests/fixtures/map-gazetteer-v1.json",
    "tests/fixtures/state-snapshot-v2.json",
    "tests/fixtures/sqf-milestone-3-contract-v1.json",
    "tests/fixtures/sqf-milestone-5-context-v1.json",
    "tests/fixtures/sqf-milestone-5-state-mirror-v2.json",
    "src/ArmaAiBridge.App/ArmaAiBridge.App.csproj",
    "src/ArmaAiBridge.App/GlobalUsings.cs",
)

OBSOLETE_PATHS = (
    "arma3/addon-source/arma_ai_bridge_client/functions/fn_collectEnvironment.sqf",
)


def iter_repository_files() -> list[Path]:
    files: list[Path] = []
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(part in SKIP_PARTS for part in path.relative_to(ROOT).parts):
            continue
        files.append(path)
    return sorted(files)


def read_text(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except UnicodeDecodeError as exc:
        raise AssertionError(f"Not valid UTF-8: {path.relative_to(ROOT)}") from exc


def extract(pattern: str, text: str, description: str) -> str:
    match = re.search(pattern, text, flags=re.MULTILINE)
    if match is None:
        raise AssertionError(f"Could not find {description}")
    return match.group(1)


def main() -> int:
    errors: list[str] = []
    files = iter_repository_files()

    for relative in EXPECTED_PATHS:
        if not (ROOT / relative).is_file():
            errors.append(f"Missing expected path: {relative}")

    for relative in OBSOLETE_PATHS:
        if (ROOT / relative).exists():
            errors.append(f"Obsolete path still exists: {relative}")

    texts: dict[Path, str] = {}
    for path in files:
        try:
            texts[path] = read_text(path)
        except AssertionError as exc:
            errors.append(str(exc))

    for path, text in texts.items():
        for marker in FORBIDDEN_MARKERS:
            if marker.lower() in text.lower():
                errors.append(f"Obsolete product identifier in {path.relative_to(ROOT)}")

    for path, text in texts.items():
        suffix = path.suffix.lower()
        try:
            if suffix == ".json":
                json.loads(text)
            elif suffix in {".xaml", ".csproj", ".manifest"}:
                ET.fromstring(text)
        except (json.JSONDecodeError, ET.ParseError) as exc:
            errors.append(f"Invalid {suffix or 'structured'} file {path.relative_to(ROOT)}: {exc}")

    try:
        telemetry_sample = json.loads(texts[ROOT / "samples/telemetry-v1.json"])
        telemetry_schema = json.loads(texts[ROOT / "schemas/telemetry-v1.schema.json"])
        if telemetry_sample.get("schema") != telemetry_schema["properties"]["schema"]["const"]:
            errors.append("Sample telemetry schema identifier does not match the JSON schema")
        if "environment" in telemetry_sample:
            errors.append("Continuous telemetry sample still contains fixed environment data")
        if "environment" in telemetry_schema.get("required", []):
            errors.append("Continuous telemetry schema still requires fixed environment data")

        command_sample = json.loads(texts[ROOT / "samples/query-command-v1.json"])
        command_schema = json.loads(texts[ROOT / "schemas/command-v1.schema.json"])
        if command_sample.get("schema") != command_schema["properties"]["schema"]["const"]:
            errors.append("Sample command schema identifier does not match the JSON schema")

        result_sample = json.loads(texts[ROOT / "samples/query-result-v1.json"])
        result_schema = json.loads(texts[ROOT / "schemas/query-result-v1.schema.json"])
        if result_sample.get("schema") != result_schema["properties"]["schema"]["const"]:
            errors.append("Sample query result schema identifier does not match the JSON schema")
        if command_sample.get("requestId") != result_sample.get("requestId"):
            errors.append("Sample command and result requestId values do not correlate")

        for name in (
            "session-handshake-v1",
            "friendly-force-snapshot-v1",
            "friendly-force-delta-v1",
            "mission-capabilities-v1",
            "map-gazetteer-v1",
            "state-snapshot-v2",
        ):
            fixture = json.loads(texts[ROOT / f"tests/fixtures/{name}.json"])
            schema = json.loads(texts[ROOT / f"schemas/{name}.schema.json"])
            if fixture.get("schema") != schema["properties"]["schema"]["const"]:
                errors.append(f"{name} fixture discriminator does not match its JSON schema")
            missing = set(schema.get("required", [])) - set(fixture)
            if missing:
                errors.append(f"{name} fixture is missing required fields: {sorted(missing)}")
            if schema.get("additionalProperties") is not False:
                errors.append(f"{name} schema must reject additional top-level properties")
    except (KeyError, TypeError) as exc:
        errors.append(f"Message schema structure is incomplete: {exc}")

    try:
        cs_pipe = extract(
            r'public const string PipeName\s*=\s*"([^"]+)"',
            texts[ROOT / "src/ArmaAiBridge.App/Services/TelemetryPipeServer.cs"],
            "C# pipe name",
        )
        cpp_pipe = extract(
            r'PipeName\[\]\s*=\s*LR"\(\\\\\.\\pipe\\([^\)]+)\)"',
            texts[ROOT / "native/ArmaAiBridge/ArmaAiBridge.cpp"],
            "C++ pipe name",
        )
        test_pipe = extract(
            r'NamedPipeClientStream\]::new\(\s*"\."\s*,\s*"([^"]+)"',
            texts[ROOT / "scripts/send-test-telemetry.ps1"],
            "PowerShell test pipe name",
        )
        if len({cs_pipe, cpp_pipe, test_pipe}) != 1:
            errors.append(f"Pipe names differ: C#={cs_pipe}, C++={cpp_pipe}, test={test_pipe}")
    except AssertionError as exc:
        errors.append(str(exc))

    cmake = texts.get(ROOT / "native/ArmaAiBridge/CMakeLists.txt", "")
    if "ArmaAiBridge.cpp" not in cmake:
        errors.append("CMake does not reference ArmaAiBridge.cpp")
    if 'OUTPUT_NAME "arma_ai_bridge_x64"' not in cmake:
        errors.append("CMake output name does not match the SQF extension name")

    cpp = texts.get(ROOT / "native/ArmaAiBridge/ArmaAiBridge.cpp", "")
    for required in (
        "GENERIC_READ | GENERIC_WRITE",
        'command == "poll"',
        'command.rfind("query-result|"',
        'command.rfind("event|"',
    ):
        if required not in cpp:
            errors.append(f"Native duplex bridge is missing: {required}")

    pipe_server = texts.get(ROOT / "src/ArmaAiBridge.App/Services/TelemetryPipeServer.cs", "")
    for required in ("PipeDirection.InOut", "SendCommandAsync", "MessageReceived"):
        if required not in pipe_server:
            errors.append(f"C# duplex pipe server is missing: {required}")

    workflow = texts.get(ROOT / ".github/workflows/build.yml", "")
    for required in (
        "src/ArmaAiBridge.App/ArmaAiBridge.App.csproj",
        "native/ArmaAiBridge",
        "@Arma_AI_Bridge",
        "arma_ai_bridge_x64.dll",
        "arma_ai_bridge_client.pbo",
        "scripts/package-mod.ps1",
    ):
        if required not in workflow:
            errors.append(f"Workflow is missing current path/name: {required}")

    config = texts.get(ROOT / "arma3/addon-source/arma_ai_bridge_client/config.cpp", "")
    for required in (
        "class AAB",
        "\\arma_ai_bridge_client\\functions",
        "class pollCommands",
        "class queryEnvironment",
        "class initialiseSession",
        "class collectFriendlyForces",
        "class updateFriendlyForcePicture",
        "class collectMissionCapabilities",
        "class publishMapGazetteer",
        "class publishStateSnapshot",
    ):
        if required not in config:
            errors.append(f"Arma CfgFunctions is missing: {required}")

    collect_telemetry = texts.get(
        ROOT / "arma3/addon-source/arma_ai_bridge_client/functions/fn_collectTelemetry.sqf", ""
    )
    for obsolete in ("AAB_environmentCache", '"environment"'):
        if obsolete in collect_telemetry:
            errors.append(f"Continuous telemetry still references fixed environment data: {obsolete}")
    for private_value in ("getPlayerUID", '"uid"', "name _unit", "name player"):
        if private_value in collect_telemetry:
            errors.append(f"Continuous telemetry exposes a private identity field: {private_value}")
    for required in ('"missionId"', '"sessionId"', '"groupId"'):
        if required not in collect_telemetry:
            errors.append(f"Continuous telemetry is missing stable session identity: {required}")

    friendly_sqf = texts.get(
        ROOT / "arma3/addon-source/arma_ai_bridge_client/functions/fn_collectFriendlyForces.sqf", ""
    )
    for private_value in ("getPlayerUID", "profileName", "name player", "allPlayers"):
        if private_value in friendly_sqf:
            errors.append(f"Friendly-force collection exposes or broadens private data: {private_value}")
    for forbidden_action in ("setWaypoint", "doMove", "commandMove", "remoteExec", "compile"):
        if forbidden_action in friendly_sqf:
            errors.append(f"Read-only friendly-force collection contains an action primitive: {forbidden_action}")

    try:
        sqf_contract = json.loads(texts[ROOT / "tests/fixtures/sqf-milestone-3-contract-v1.json"])
        for rule in sqf_contract["files"]:
            relative = rule["path"]
            source = texts.get(ROOT / relative, "")
            for required in rule.get("requiredTokens", []):
                if required not in source:
                    errors.append(f"SQF contract {relative} is missing: {required}")
            for forbidden in rule.get("forbiddenTokens", []):
                if forbidden.lower() in source.lower():
                    errors.append(f"SQF contract {relative} contains forbidden token: {forbidden}")
    except (KeyError, TypeError) as exc:
        errors.append(f"SQF Milestone 3 contract fixture is incomplete: {exc}")

    try:
        sqf_contract = json.loads(texts[ROOT / "tests/fixtures/sqf-milestone-5-context-v1.json"])
        for rule in sqf_contract["files"]:
            relative = rule["path"]
            source = texts.get(ROOT / relative, "")
            for required in rule.get("requiredTokens", []):
                if required not in source:
                    errors.append(f"SQF contract {relative} is missing: {required}")
            for forbidden in rule.get("forbiddenTokens", []):
                if forbidden.lower() in source.lower():
                    errors.append(f"SQF contract {relative} contains forbidden token: {forbidden}")
    except (KeyError, TypeError) as exc:
        errors.append(f"SQF Milestone 5 contract fixture is incomplete: {exc}")

    try:
        sqf_contract = json.loads(texts[ROOT / "tests/fixtures/sqf-milestone-5-state-mirror-v2.json"])
        for rule in sqf_contract["files"]:
            relative = rule["path"]
            source = texts.get(ROOT / relative, "")
            for required in rule.get("requiredTokens", []):
                if required not in source:
                    errors.append(f"SQF contract {relative} is missing: {required}")
            for forbidden in rule.get("forbiddenTokens", []):
                if forbidden.lower() in source.lower():
                    errors.append(f"SQF contract {relative} contains forbidden token: {forbidden}")
    except (KeyError, TypeError) as exc:
        errors.append(f"SQF Milestone 5 State Mirror contract fixture is incomplete: {exc}")

    query_sqf = texts.get(
        ROOT / "arma3/addon-source/arma_ai_bridge_client/functions/fn_queryEnvironment.sqf", ""
    )
    for required in ("nearestTerrainObjects", '"circle"', '"cone"', "1500", "maxResultsPerCategory"):
        if required not in query_sqf:
            errors.append(f"Dynamic query implementation is missing: {required}")
    if "_probeDefinitions" in query_sqf or "[[150, 60], [350, 100], [650, 160]]" in query_sqf:
        errors.append("Fixed probe implementation is still present")

    if errors:
        print("Repository verification failed:")
        for error in errors:
            print(f" - {error}")
        return 1

    print(f"Repository verification passed: {len(files)} UTF-8 files checked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
