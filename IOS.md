# Project Reignition for iOS

This port targets iPhone and iPad in landscape, using Godot 4.7 .NET and the Metal Mobile renderer. It includes on-screen controls, iOS-safe saves, mobile graphics defaults, and the complete original video set converted to Godot's built-in Theora format.

The game now fills both device shapes without stretching. Menus stay centered inside the safe area over a full-screen backdrop, and the iPad camera expands vertically to preserve the horizontal view. Pupil offsets survive headless export, reward prompts retain Confirm behind completed story events, and the spinning ball uses a mobile-supported shader fade with explicit visibility instead of the unsupported mesh-transparency property.

## Controls

Use the left stick to move and the open right side to look. Hold the stick while pressing Jump, Action, Attack, Brake, Step, Light Dash, Speed Break, or Time Break with another finger. The labels match the existing game actions and equipped skills.

In menus, use the stick to navigate, Confirm to select, Back to cancel, and Start for the separate ready/start action. The top-center button pauses/resumes gameplay or skips a movie. Party mode supports touch for local player 1; additional players need controllers. See [touch controls](Project/interface/touch/README.md).

## Build on this Mac

```sh
./scripts/ios/bootstrap.sh
python3 scripts/ios/convert_videos.py --check
./scripts/ios/test_runtime.sh
./scripts/ios/build.sh unsigned
```

The bootstrap installs Godot 4.7 .NET, its matching iOS template, and .NET 10.0.401 under `.tools/`. It leaves the system .NET installation unchanged. Godot 4.7's [macOS CLI resolver](https://github.com/godotengine/godot/blob/4.7-stable/modules/mono/editor/GodotTools/GodotTools/Build/DotNetFinder.cs) prioritizes a system path over `PATH`, so the bootstrap adjusts only the downloaded editor's resolver to select this local SDK. The original managed assembly is preserved beside the patched file.

Videos are generated local assets and excluded from Git, like the original MP4 files. To recreate them from the installed desktop game, follow [video recovery and conversion](scripts/ios/VIDEO_ASSETS.md). The build checks that all 48 converted videos exist before exporting.

Other build commands:

```sh
./scripts/ios/build.sh export       # Generate Xcode project and native C# libraries
./scripts/ios/build.sh signed       # Development archive and signed IPA
./scripts/ios/build.sh simulator    # Native iOS Simulator app
SKIP_EXPORT=1 ./scripts/ios/build.sh unsigned  # Reuse the latest complete Xcode export
```

`GODOT` can select a different compatible .NET editor. `IOS_BUILD_DIR` can select an output directory. Generated builds and logs live in `build/ios/` by default. `Project/export_presets.cfg` contains the bundle ID and development team; adjust these for a different Apple account.

### Simulator setup

The official Godot 4.7 .NET simulator archive contains only x86_64 code, despite its XCFramework metadata advertising arm64 too. The build script checks the actual library architecture. On this Apple Silicon Mac it requires a universal simulator runtime, such as iOS 16.4; the installed iOS 26.5 runtime is arm64-only and cannot install that app.

```sh
xcodebuild -downloadPlatform iOS -buildVersion 20E247
IOS_SIMULATOR_ID=$(xcrun simctl create 'Reignition iOS 16.4' \
  com.apple.CoreSimulator.SimDeviceType.iPhone-14-Pro \
  com.apple.CoreSimulator.SimRuntime.iOS-16-4)
xcrun simctl boot "$IOS_SIMULATOR_ID"
xcrun simctl install "$IOS_SIMULATOR_ID" \
  build/ios/DerivedData-Simulator/Build/Products/Release-iphonesimulator/Reignition.app
xcrun simctl launch "$IOS_SIMULATOR_ID" com.aroblesalago.projectreignition \
  --rendering-method gl_compatibility --rendering-driver opengl3
```

Godot's [simulator build disables Metal and Vulkan](https://github.com/godotengine/godot/blob/4.7-stable/platform/ios/detect.py). The simulator build therefore includes a separate `override.cfg` selecting Compatibility rendering. Both the project setting and renderer selection must agree so the iOS window creates its OpenGL context. This override is copied only into the simulator app; the iPhone/iPad IPA uses Metal Mobile.

An additional native arm64 simulator engine and app were built locally for iOS 26.5. The source revision, build, signing, and installation commands are recorded in `.tools/GODOT_SIMULATOR_BUILD.md`. Both simulator runtimes expose Apple's software OpenGL renderer, which can stall on particle shaders; simulator performance does not represent the Metal device build.

## Install for testing

The current Rampage update is version 1.0.6, build 2. The unsigned artifact is `build/ios/Project-Reignition-unsigned.ipa` (2,651,187,763 bytes), with SHA-256 `ab4381edd65f5d7f7881b8af86e4dfc3975e298538c56d73ef932894e5402966`. Archive integrity, the arm64 iPhoneOS executable, the native C# framework, both landscape orientations, and the bundled game pack were verified in `build/ios/rampage-unsigned-ipa-verification.json`.

The current signed copy is `build/ios/Project-Reignition-Alejandro17-Rampage.ipa` (2,649,387,426 bytes), signed for Alejandro17 with iOS App Signer and Xcode's Personal Team profile. Its SHA-256 is `e9bbbf6b2aad01367ffc90386e9f3f306a45000e88668d996a0310a9d82333ca`. The profile includes Alejandro17 and Ale iPad Pro, and expires **September 17, 2026 at 8:05 PM America/Chicago** (September 18 at 01:05 UTC). It needs renewed signing after that. Strict signature validation, entitlements, device registration, and ZIP integrity passed. The normal IPA contains no diagnostic scene override; its game pack and native C# library match the physical-device tests. The PCK SHA-256 is `b3f38824bac66ad3eb34f711598967ba8ac2aacf5cae6e372ea699a3087d812a`. See `build/ios/rampage-signed-ipa-verification.json` and `build/ios/rampage-final-signing-verification.json`.

Build 2 was installed and launched successfully on Alejandro17 without test arguments. An Xcode screenshot confirms normal Rampage gameplay with 15 rings and 4 of 20 enemies defeated. The existing `save00.dat` and `shared.dat` remained byte-identical across installation. See the [installation record](build/ios/rampage-installation.md), `build/ios/rampage-final-normal.png`, and `build/ios/rampage-save-verification.json`.

The same build 2 IPA is also installed on the connected 12.9-inch Ale iPad Pro. Normal launch succeeded, and a physical 2732×2048 screenshot confirms the Sand Oasis ready menu and touch controls. Both existing save files remained byte-identical through the update. See [iPad update verification](build/ios/rampage-ipad-installation.md).

This update fixes Sand Oasis Rampage's infinite loading and native crash by retaining the loaded scene with `GC.KeepAlive(scene)` until native scene instantiation finishes. Failed loads now show Retry and Main menu choices, and phase diagnostics identify whether loading, instantiation, or stage initialization failed. The native A19 iPhone build passed 63 checks: 20 for a cold Rampage load, 20 with Boot's common-resource preload, and 23 for failure recovery. Both successful Rampage runs verified the 20-enemy objective, finished the countdown, and moved Sonic using touch controls. Evidence is in `build/ios/rampage-fixed-native-results/`, `build/ios/rampage-preload-native-results/`, and `build/ios/loading-recovery-native-results/`; the technical diagnosis is in `build/ios/rampage-root-cause.md`.

The previous signed iPad artifact is `build/ios/Project-Reignition-iPad.ipa` (2.65 GB), with SHA-256 `d80cffeead13a8bbd399aa008766d3594b721bedd01471700dc5f6100ecfa1a6`. It was installed and launched successfully on the 12.9-inch Ale iPad Pro, and Xcode screenshots confirm normal tutorial gameplay. Its verification and installation records remain in `build/ios/ipad-ipa-verification.json`, `build/ios/ipad-final-signing-verification.json`, and `build/ios/ipad-installation.md`. That earlier artifact does not include the Rampage fix.

To repeat the tested Xcode/iOS App Signer workflow:

1. Sign into Xcode Settings → Apple Accounts. A free Apple account appears as a Personal Team. Open `build/ios/xcode/Reignition.xcodeproj`, select the connected target device, enable automatic signing for that team, and build to create a matching device profile.
2. In iOS App Signer, select `build/ios/Project-Reignition-unsigned.ipa`, the valid Apple Development certificate, and the Xcode profile for the exact bundle ID `com.aroblesalago.projectreignition`. Use **Choose Custom File** if the profile list is stale. Do not use **Re-Sign Only** for the unsigned input. Keep all frameworks included and allow development entitlements.
3. Save the signed IPA and install it from Xcode's Devices and Simulators window, or extract its `.app` and use `xcrun devicectl device install app --device 14ACD7CD-028F-532F-A973-5346CF61E42B <app-path>` for Alejandro17. Select the corresponding device identifier when installing on an iPad.
4. If iOS blocks the first launch, open Settings → General → VPN & Device Management on the target device and trust/verify your Apple account under Developer App.

The earlier Apple login rejection was resolved by refreshing the Xcode account. The original iPhone installation is recorded in `build/ios/alejandro17-installation.md`; the previous physical iPad gameplay results are in `build/ios/ipad-native-verification.md`.

Godot documents the native export workflow in [Exporting for iOS](https://docs.godotengine.org/en/4.7/tutorials/export/exporting_for_ios.html).

## Verification

The updated source passed 360 automated checks: 130 touch-control assertions, 49 rendered gameplay checks, 111 post-level reward checks, 35 layout checks, 16 options/remapping checks, and 19 title quick-tap checks. The same 49 gameplay checks also passed at both iPhone and iPad screen proportions under Metal Mobile. The .NET Debug build completed with zero warnings or errors, and native AOT compilation succeeded for the iPhone/iPad arm64 and both simulator architectures. All 48 converted videos were verified inside the final game pack; desktop MP4s and test scenes were excluded from that pack.

`scripts/ios/test_runtime.sh` runs a rendered Metal Mobile tutorial with isolated save data. It verifies loading, countdown, movement through gameplay physics, simultaneous touch jumping, releasing controls, landing, held-jump behavior, an equipped Spin Jump, interrupted/cancelled effect fades, pause/resume, and writable saves. The spinning ball must be visible during Spin Jump and hidden before jumping and after landing. Results and screenshots are saved in `build/ios/`.

`Project/tests/ios/PostLevelTouchTest.tscn` exercises the real tutorial results, EXP screen, post-level story event, and all 11 queued reward prompts using quick screen touches. `MobileLayoutTest.tscn` renders the actual menus at iPhone and iPad proportions and checks their centered safe-area bounds and full-screen coverage. `EyeOffsetsTest.gd` verifies that the final exported Sonic scene retains both pupil offsets and the script that applies them at runtime. Reports are in `build/ios/post-level-touch-result.txt`, `build/ios/layout-verification.md`, and `build/ios/eye-offsets-export-verification.log`.

The separate `TouchSelfTest.tscn` checks action mappings, analog strength, multitouch ownership, camera separation, cancellation on focus/suspend/context changes, and iPhone/iPad layouts. Run it after a Debug build using the command in the touch-controls README.

`Project/tests/ios/TitleQuickTapTest.tscn` exercises the actual title scene with a touch press and release delivered before its next update. It confirms that both Confirm and Start trigger the title transition, and checks keyboard/controller fallback behavior. This regression covers a bug found during native simulator testing where a quick tap could be missed by a check for currently held buttons.

The earlier port was installed and launched on an iPhone 17 Pro simulator running iOS 26.5. A real on-screen Confirm tap advanced the title to the main menu, and another opened save selection, using that earlier build’s game pack. These native checks used Compatibility rendering with GPU particles disabled only in a simulator test hook because Apple's software renderer stalls on their shaders. See `build/ios/native-simulator-verification.md` for the commands, evidence, screenshots, and exact limits. The device IPA retains Metal Mobile and its effects.

The latest build also passed all 49 gameplay checks on the physical 12.9-inch iPad Pro using its M1 Metal GPU. Native screenshots confirm the full 2732×2048 view and the corrected spin effect. See `build/ios/ipad-native-verification.md` and `build/ios/ipad-native-results/`. The source contains existing font-UID fallback warnings and shutdown resource-leak warnings; the physical test logged one nonfatal unsupported-mouse message. Full playthrough, network party play, battery usage, and sustained performance remain outside this smoke test.
