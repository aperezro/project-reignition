#!/usr/bin/env python3
"""Check IPA structure, payload integrity, iOS target, and managed native library."""
import hashlib
import json
import plistlib
from pathlib import Path
import sys
import zipfile


def main():
    path = Path(sys.argv[1])
    with zipfile.ZipFile(path) as archive:
        corrupt = archive.testzip()
        if corrupt:
            raise RuntimeError(f"Corrupt archive entry: {corrupt}")
        names = archive.namelist()
        if any(part.startswith("._") for name in names for part in name.split("/")):
            raise RuntimeError("Archive contains macOS AppleDouble metadata")
        roots = sorted({name.split("/", 2)[1] for name in names
                        if name.startswith("Payload/") and ".app/" in name})
        if len(roots) != 1:
            raise RuntimeError(f"Expected one application payload, found {roots}")
        root = "Payload/" + roots[0] + "/"
        info = plistlib.loads(archive.read(root + "Info.plist"))
        executable = root + info["CFBundleExecutable"]
        if executable not in names or archive.getinfo(executable).file_size == 0:
            raise RuntimeError("Missing application executable")
        if info.get("CFBundleSupportedPlatforms") != ["iPhoneOS"]:
            raise RuntimeError("This is not an iPhoneOS device build")
        packs = [name for name in names if name.startswith(root) and name.endswith(".pck")]
        if len(packs) != 1 or archive.getinfo(packs[0]).file_size < 1_000_000:
            raise RuntimeError("Missing game resource pack")
        native = [name for name in names if name.startswith(root + "Frameworks/")
                  and "Sonic" in name and "Project" in name
                  and not name.endswith(("/", ".plist", ".pdb"))]
        if not native:
            raise RuntimeError("Missing native AOT C# game framework")
        pack_digest = hashlib.sha256()
        with archive.open(packs[0]) as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                pack_digest.update(chunk)
        result = dict(ipa=str(path.resolve()), bytes=path.stat().st_size,
                      bundle_identifier=info["CFBundleIdentifier"],
                      version=info["CFBundleShortVersionString"],
                      minimum_ios=info["MinimumOSVersion"],
                      platforms=info["CFBundleSupportedPlatforms"],
                      device_families=info.get("UIDeviceFamily"),
                      supported_orientations=info.get("UISupportedInterfaceOrientations"),
                      provisioned=(root + "embedded.mobileprovision") in names,
                      packed_game_bytes=archive.getinfo(packs[0]).file_size,
                      packed_game_sha256=pack_digest.hexdigest(),
                      native_game_framework=native, zip_integrity="passed")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    result["sha256"] = digest.hexdigest()
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
