# iOS video assets

The desktop game uses MP4 files through its FFmpeg extension, which has no iOS
library. The iOS loader instead uses adjacent `.ogv` files with Godot's built-in
Theora decoder. Both source and generated video files are ignored by Git.

If this checkout lacks the source videos, recover them from an existing local
installation of this game:

```sh
python3 scripts/ios/recover_videos.py "/Applications/Sonic and the Secret Rings Remake.app/Contents/Resources/Sonic and the Secret Rings Remake.pck"
```

The recovery script reads only `video/*.mp4` entries, checks each original PCK
MD5, and preserves any existing matching file. Its source inventory is saved
under `.tools/video-extraction.json`.

Install the full FFmpeg package, then convert the source files:

```sh
HOMEBREW_NO_AUTO_UPDATE=1 /opt/homebrew/bin/brew install ffmpeg-full
python3 scripts/ios/convert_videos.py --jobs 3
python3 scripts/ios/convert_videos.py --check
```

The regular Homebrew `ffmpeg` package currently omits the Theora encoder. The
converter automatically finds the keg-only `ffmpeg-full` installation without
changing the shell's PATH or relinking Homebrew packages.

Conversion caps footage at 1280×720 and 30 fps without upscaling smaller clips.
Theora quality is 7. Embedded audio is preserved as Vorbis quality 4; only the
SFF boot logo has embedded audio in the recovered game. All other videos stay
silent because the game plays separate music and localized dialog tracks.

`video-inventory.json` records source/output hashes, sizes, durations, codecs,
and audio presence for all 48 clips. Conversion verifies duration, codec, size
limits, and audio preservation before moving each completed file into place.
Rerunning it reuses existing newer outputs. Use `--force` when changing quality
or replacing conversion settings. `--check` only verifies that every source has
a nonempty output, so it does not need FFmpeg or repeat conversion.
