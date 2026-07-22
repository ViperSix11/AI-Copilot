#!/usr/bin/env python3
"""Create and validate a deterministic, uncompressed Arma development PBO."""

from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

ENTRY_FIELDS = struct.Struct("<4sIIII")
MAX_FILE_BYTES = 256 * 1024 * 1024


@dataclass(frozen=True)
class Entry:
    name: str
    original_size: int
    timestamp: int
    data_size: int


def asciiz(value: str) -> bytes:
    encoded = value.encode("ascii")
    if b"\0" in encoded:
        raise ValueError("PBO strings cannot contain NUL bytes")
    return encoded + b"\0"


def read_asciiz(blob: bytes, offset: int) -> tuple[str, int]:
    end = blob.find(b"\0", offset)
    if end < 0:
        raise ValueError("unterminated PBO string")
    return blob[offset:end].decode("ascii"), end + 1


def source_files(source: Path) -> list[tuple[str, bytes]]:
    values: list[tuple[str, bytes]] = []
    seen: set[str] = set()
    for path in sorted(source.rglob("*"), key=lambda value: value.as_posix().lower()):
        if path.is_symlink():
            raise ValueError(f"symbolic links are not supported: {path}")
        if not path.is_file() or path.name.lower() == "$pboprefix$.txt":
            continue
        relative = path.relative_to(source).as_posix().replace("/", "\\")
        folded = relative.casefold()
        if folded in seen:
            raise ValueError(f"case-insensitive duplicate PBO path: {relative}")
        seen.add(folded)
        data = path.read_bytes()
        if len(data) > MAX_FILE_BYTES:
            raise ValueError(f"PBO source file exceeds {MAX_FILE_BYTES} bytes: {relative}")
        values.append((relative, data))
    if not values:
        raise ValueError("PBO source contains no files")
    return values


def pack(source: Path, output: Path, prefix: str) -> dict[str, object]:
    files = source_files(source)
    prefix = prefix.strip().strip("\\/")
    if not prefix:
        raise ValueError("PBO prefix cannot be empty")

    header = bytearray()
    header += b"\0" + ENTRY_FIELDS.pack(b"sreV", 0, 0, 0, 0)
    header += asciiz("prefix") + asciiz(prefix) + b"\0"
    for name, data in files:
        header += asciiz(name)
        header += ENTRY_FIELDS.pack(b"\0\0\0\0", len(data), 0, 0, len(data))
    header += b"\0" + ENTRY_FIELDS.pack(b"\0\0\0\0", 0, 0, 0, 0)

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix + ".tmp")
    with temporary.open("wb") as stream:
        stream.write(header)
        for _, data in files:
            stream.write(data)
    temporary.replace(output)
    return verify(output, prefix)


def verify(path: Path, expected_prefix: str | None = None) -> dict[str, object]:
    blob = path.read_bytes()
    offset = 0
    name, offset = read_asciiz(blob, offset)
    if name or offset + ENTRY_FIELDS.size > len(blob):
        raise ValueError("missing PBO properties entry")
    mime, original_size, reserved, timestamp, data_size = ENTRY_FIELDS.unpack_from(blob, offset)
    offset += ENTRY_FIELDS.size
    if mime != b"sreV" or any((original_size, reserved, timestamp, data_size)):
        raise ValueError("invalid PBO properties entry")

    properties: dict[str, str] = {}
    while True:
        key, offset = read_asciiz(blob, offset)
        if not key:
            break
        value, offset = read_asciiz(blob, offset)
        properties[key] = value
    if expected_prefix is not None and properties.get("prefix") != expected_prefix:
        raise ValueError("PBO prefix does not match the requested addon prefix")

    entries: list[Entry] = []
    while True:
        name, offset = read_asciiz(blob, offset)
        if offset + ENTRY_FIELDS.size > len(blob):
            raise ValueError("truncated PBO header")
        mime, original_size, reserved, timestamp, data_size = ENTRY_FIELDS.unpack_from(blob, offset)
        offset += ENTRY_FIELDS.size
        if not name:
            if mime != b"\0\0\0\0" or any((original_size, reserved, timestamp, data_size)):
                raise ValueError("invalid terminating PBO entry")
            break
        if mime != b"\0\0\0\0" or reserved != 0 or original_size not in (0, data_size):
            raise ValueError(f"unsupported PBO entry: {name}")
        if name.startswith("\\") or "/" in name or ".." in name.split("\\"):
            raise ValueError(f"unsafe PBO entry path: {name}")
        entries.append(Entry(name, original_size, timestamp, data_size))

    data_bytes = sum(entry.data_size for entry in entries)
    remaining = len(blob) - offset
    if remaining not in (data_bytes, data_bytes + 21):
        raise ValueError("PBO data length does not match its entries")
    if remaining == data_bytes + 21 and blob[offset + data_bytes] != 0:
        raise ValueError("invalid optional PBO checksum separator")
    if not entries:
        raise ValueError("PBO contains no files")
    return {
        "path": str(path.resolve()),
        "prefix": properties.get("prefix", ""),
        "fileCount": len(entries),
        "dataBytes": data_bytes,
        "hasChecksum": remaining == data_bytes + 21,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    pack_parser = subparsers.add_parser("pack")
    pack_parser.add_argument("source", type=Path)
    pack_parser.add_argument("output", type=Path)
    pack_parser.add_argument("--prefix", required=True)
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("path", type=Path)
    verify_parser.add_argument("--prefix")
    arguments = parser.parse_args()
    try:
        result = (
            pack(arguments.source.resolve(), arguments.output.resolve(), arguments.prefix)
            if arguments.command == "pack"
            else verify(arguments.path.resolve(), arguments.prefix)
        )
        print(json.dumps(result, sort_keys=True))
        return 0
    except (OSError, UnicodeError, ValueError, struct.error) as error:
        print(f"PBO packaging failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
