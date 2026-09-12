#!/usr/bin/env python3
"""Create built-in Godot/Theora videos beside the original desktop MP4 files.

Usage: python3 scripts/ios/convert_videos.py
Requires ffmpeg (with libtheora/libvorbis) and ffprobe. Source videos and their
audio are preserved; silent source files remain silent. Output is capped at
1280x720 and 30 fps without upscaling. Existing newer outputs are verified and
reused unless --force is passed. The JSON inventory records every result.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


def executable(name: str) -> str:
    for prefix in ("/opt/homebrew/opt/ffmpeg-full/bin", "/usr/local/opt/ffmpeg-full/bin"):
        candidate = Path(prefix) / name
        if candidate.is_file():
            return str(candidate)
    found = shutil.which(name)
    if found:
        return found
    for prefix in ("/opt/homebrew/bin", "/usr/local/bin"):
        candidate = Path(prefix) / name
        if candidate.is_file():
            return str(candidate)
    raise RuntimeError(f"{name} is missing. Install FFmpeg, then rerun this script.")


def probe(ffprobe: str, path: Path) -> dict:
    result = subprocess.run(
        [ffprobe, "-v", "error", "-show_entries",
         "format=duration:stream=codec_type,codec_name,width,height,avg_frame_rate",
         "-of", "json", str(path)], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def video_stream(metadata: dict) -> dict:
    return next(stream for stream in metadata["streams"]
                if stream["codec_type"] == "video")


def convert(source: Path, root: Path, ffmpeg: str, ffprobe: str,
            quality: int, force: bool) -> dict:
    target = source.with_suffix(".ogv")
    original = probe(ffprobe, source)
    source_video = video_stream(original)
    numerator, denominator = source_video["avg_frame_rate"].split("/")
    fps = min(float(numerator) / float(denominator), 30)
    if force or not target.exists() or target.stat().st_mtime < source.stat().st_mtime:
        temporary = target.with_suffix(".encoding.tmp")
        filters = ("scale=w='min(1280,iw)':h='min(720,ih)':"
                   "force_original_aspect_ratio=decrease:force_divisible_by=2,"
                   f"fps={fps:.8f},format=yuv420p")
        command = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                   "-i", str(source), "-map", "0:v:0", "-map", "0:a:0?",
                   "-vf", filters, "-c:v", "libtheora", "-q:v", str(quality),
                   "-c:a", "libvorbis", "-q:a", "4", "-threads", "2",
                   "-f", "ogg", str(temporary)]
        try:
            subprocess.run(command, check=True, capture_output=True, text=True)
            output = probe(ffprobe, temporary)
            verify(original, output, source)
            os.replace(temporary, target)
        finally:
            temporary.unlink(missing_ok=True)
    else:
        output = probe(ffprobe, target)
        verify(original, output, source)
    print(f"Ready: {target.relative_to(root)}", flush=True)
    return {
        "source": str(source.relative_to(root)),
        "output": str(target.relative_to(root)),
        "source_bytes": source.stat().st_size,
        "output_bytes": target.stat().st_size,
        "source_sha256": sha256(source),
        "output_sha256": sha256(target),
        "duration_seconds": float(output["format"]["duration"]),
        "video": video_stream(output),
        "has_embedded_audio": any(stream["codec_type"] == "audio"
                                 for stream in output["streams"]),
    }


def verify(original: dict, output: dict, source: Path) -> None:
    video = video_stream(output)
    if video["codec_name"] != "theora" or video["width"] > 1280 or video["height"] > 720:
        raise RuntimeError(f"Invalid Theora output for {source}")
    source_duration = float(original["format"]["duration"])
    output_duration = float(output["format"]["duration"])
    if abs(source_duration - output_duration) > max(0.15, source_duration * 0.001):
        raise RuntimeError(f"Duration changed for {source}: {source_duration} -> {output_duration}")
    source_audio = any(stream["codec_type"] == "audio" for stream in original["streams"])
    output_audio = any(stream["codec_type"] == "audio" for stream in output["streams"])
    if source_audio != output_audio:
        raise RuntimeError(f"Audio stream was unexpectedly added or removed for {source}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path,
                        default=Path(__file__).resolve().parents[2] / "Project")
    parser.add_argument("--jobs", type=int, default=2)
    parser.add_argument("--quality", type=int, choices=range(0, 11), default=7)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--check", action="store_true",
                        help="Only check that every source has a nonempty .ogv; do not encode")
    parser.add_argument("--inventory", type=Path,
                        default=Path(__file__).resolve().parent / "video-inventory.json")
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    root = args.project.resolve()
    sources = sorted((root / "video").rglob("*.mp4"))
    if not sources:
        parser.error(f"No source MP4 videos found below {root / 'video'}")
    if args.check:
        missing = [source.with_suffix(".ogv") for source in sources
                   if not source.with_suffix(".ogv").is_file()
                   or source.with_suffix(".ogv").stat().st_size == 0]
        if missing:
            names = "\n".join(str(path.relative_to(root)) for path in missing)
            raise RuntimeError(f"{len(missing)} iOS videos are missing or empty:\n{names}\n"
                               "Run scripts/ios/convert_videos.py before building.")
        print(f"All {len(sources)} iOS videos are present and nonempty.")
        return 0
    ffmpeg, ffprobe = executable("ffmpeg"), executable("ffprobe")
    encoders = subprocess.run([ffmpeg, "-hide_banner", "-encoders"], check=True,
                              capture_output=True, text=True).stdout
    if "libtheora" not in encoders or "libvorbis" not in encoders:
        raise RuntimeError("FFmpeg needs the libtheora and libvorbis encoders. "
                           "On macOS, install the ffmpeg-full Homebrew package.")
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as executor:
        futures = [executor.submit(convert, source, root, ffmpeg, ffprobe,
                                   args.quality, args.force) for source in sources]
        for future in concurrent.futures.as_completed(futures):
            results.append(future.result())
    inventory = {"codec": "theora", "quality": args.quality,
                 "maximum_size": [1280, 720], "maximum_fps": 30,
                 "audio_policy": "Preserve embedded audio as Vorbis; do not add audio to silent videos.",
                 "files": sorted(results, key=lambda item: item["source"])}
    args.inventory.parent.mkdir(parents=True, exist_ok=True)
    args.inventory.write_text(json.dumps(inventory, indent=2) + "\n")
    print(f"Verified {len(results)} videos. Inventory: {args.inventory}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (RuntimeError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError) and error.stderr:
            print(error.stderr, file=sys.stderr)
        sys.exit(1)
