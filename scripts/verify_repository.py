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
    "arma3/@Arma_AI_Bridge/mod.cpp",
    "arma3/addon-source/arma_ai_bridge_client/config.cpp",
    "native/ArmaAiBridge/ArmaAiBridge.cpp",
    "native/ArmaAiBridge/CMakeLists.txt",
    "schemas/telemetry-v1.schema.json",
    "samples/telemetry-v1.json",
    "src/ArmaAiBridge.App/ArmaAiBridge.App.csproj",
    "src/ArmaAiBridge.App/GlobalUsings.cs",
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
        sample = json.loads(texts[ROOT / "samples/telemetry-v1.json"])
        schema = json.loads(texts[ROOT / "schemas/telemetry-v1.schema.json"])
        schema_id = schema["properties"]["schema"]["const"]
        if sample.get("schema") != schema_id:
            errors.append("Sample telemetry schema identifier does not match the JSON schema")
    except (KeyError, TypeError) as exc:
        errors.append(f"Telemetry schema structure is incomplete: {exc}")

    try:
        cs_pipe = extract(r'public const string PipeName\s*=\s*"([^"]+)"', texts[ROOT / "src/ArmaAiBridge.App/Services/TelemetryPipeServer.cs"], "C# pipe name")
        cpp_pipe = extract(r'PipeName\[\]\s*=\s*LR"\(\\\\\.\\pipe\\([^\)]+)\)"', texts[ROOT / "native/ArmaAiBridge/ArmaAiBridge.cpp"], "C++ pipe name")
        test_pipe = extract(r'NamedPipeClientStream\]::new\(\s*"\."\s*,\s*"([^"]+)"', texts[ROOT / "scripts/send-test-telemetry.ps1"], "PowerShell test pipe name")
        if len({cs_pipe, cpp_pipe, test_pipe}) != 1:
            errors.append(f"Pipe names differ: C#={cs_pipe}, C++={cpp_pipe}, test={test_pipe}")
    except AssertionError as exc:
        errors.append(str(exc))

    cmake = texts.get(ROOT / "native/ArmaAiBridge/CMakeLists.txt", "")
    if "ArmaAiBridge.cpp" not in cmake:
        errors.append("CMake does not reference ArmaAiBridge.cpp")
    if 'OUTPUT_NAME "arma_ai_bridge_x64"' not in cmake:
        errors.append("CMake output name does not match the SQF extension name")

    workflow = texts.get(ROOT / ".github/workflows/build.yml", "")
    for required in ("src/ArmaAiBridge.App/ArmaAiBridge.App.csproj", "native/ArmaAiBridge", "@Arma_AI_Bridge", "arma_ai_bridge_x64.dll"):
        if required not in workflow:
            errors.append(f"Workflow is missing current path/name: {required}")

    config = texts.get(ROOT / "arma3/addon-source/arma_ai_bridge_client/config.cpp", "")
    if "class AAB" not in config or "\\arma_ai_bridge_client\\functions" not in config:
        errors.append("Arma CfgFunctions namespace or PBO path is inconsistent")

    for sqf_name in ("fn_postInit.sqf", "fn_sendTelemetry.sqf"):
        sqf = texts.get(ROOT / f"arma3/addon-source/arma_ai_bridge_client/functions/{sqf_name}", "")
        if '"arma_ai_bridge" callExtension' not in sqf:
            errors.append(f"{sqf_name} does not call the expected extension")

    if errors:
        print("Repository verification failed:")
        for error in errors:
            print(f" - {error}")
        return 1

    print(f"Repository verification passed: {len(files)} UTF-8 files checked.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
