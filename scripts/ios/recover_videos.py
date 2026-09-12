#!/usr/bin/env python3
"""Recover original video assets from an existing, unencrypted local game PCK.

Usage:
    python3 scripts/ios/recover_videos.py /path/to/installed-game.pck

This extracts only video/*.mp4 files, verifies their PCK checksums, and never
overwrites a different existing source file. Supports Godot PCK versions 3/4.
"""

import argparse
import hashlib
import json
from pathlib import Path
import struct


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pack", type=Path)
    parser.add_argument("--project", type=Path,
                        default=Path(__file__).resolve().parents[2] / "Project")
    parser.add_argument("--inventory", type=Path,
                        default=Path(__file__).resolve().parents[2] / ".tools" / "video-extraction.json")
    args = parser.parse_args()
    project = args.project.resolve()
    records = []
    with args.pack.open("rb") as pack:
        magic, version, major, minor, patch, flags, base, directory = struct.unpack("<4sIIIIIQQ", pack.read(40))
        if magic != b"GDPC" or version not in (3, 4) or flags != 2:
            parser.error("Expected an unencrypted, standalone Godot v3/v4 PCK with relative file offsets")
        pack.seek(directory)
        count = struct.unpack("<I", pack.read(4))[0]
        entries = []
        for _ in range(count):
            name_length = struct.unpack("<I", pack.read(4))[0]
            name = pack.read(name_length).rstrip(b"\0").decode("utf-8")
            offset, size = struct.unpack("<QQ", pack.read(16))
            checksum = pack.read(16)
            file_flags = struct.unpack("<I", pack.read(4))[0]
            if name.startswith("video/") and name.lower().endswith(".mp4"):
                target = (project / name).resolve()
                if project not in target.parents or file_flags != 0:
                    parser.error(f"Unsupported video entry: {name}")
                entries.append((name, target, base + offset, size, checksum))
        for name, target, offset, size, checksum in entries:
            pack.seek(offset)
            data = pack.read(size)
            digest = hashlib.md5(data).digest()
            if len(data) != size or digest != checksum:
                parser.error(f"Checksum mismatch: {name}")
            if target.exists():
                if target.read_bytes() != data:
                    parser.error(f"Refusing to replace an existing different video: {target}")
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(data)
            records.append({"path": name, "bytes": size, "md5": digest.hex()})
    if not records:
        parser.error("No video assets found")
    args.inventory.parent.mkdir(parents=True, exist_ok=True)
    args.inventory.write_text(json.dumps({"source": str(args.pack.resolve()),
                                         "format": version, "files": records}, indent=2) + "\n")
    print(f"Recovered and checksum-verified {len(records)} original videos.")


if __name__ == "__main__":
    main()
