# Touch controls

`TouchControls.tscn` is a persistent autoload. The overlay appears automatically on iOS and Android. On a desktop, pass `-- --touch-controls` to display it and use the mouse as a single test finger. The desktop flag removes mouse-button mappings for the current session so clicking the overlay cannot also trigger the original mouse jump binding.

- **Adventure:** left analog stick moves; drag the open right side to control the camera. Jump, Action, Attack, Brake, Light Dash, both Break skills, and both Step actions have separate buttons. Hold the stick while pressing actions with another finger.
- **Menus and pause:** the left stick navigates; Confirm selects; Back cancels; Start activates the separate start/ready action. Previous/Next, Sort, Clear, and Mods cover secondary menu commands. Resume closes pause.
- **Options:** menu controls stay active while the options book is open. Entering its control test switches to gameplay controls; Menu returns to the book.
- **Cutscenes:** hold the top-center Skip button to skip. Back exits a Special Book movie.
- **Results and rewards:** Confirm advances results and EXP, then dismisses skill, world, mission, page, party, World Ring, and Time Attack unlocks. Rewards keep menu controls even when the completed story event is still loaded behind them.
- **Party:** the stick, Primary, Secondary, and Pause control local player 1. Other local players require controllers.

Each finger keeps its original control until lifted, even when it drifts outside the button. Touch actions are released when focus is lost, the app is suspended, a scene/menu context changes, the viewport changes, or the controls are hidden. The safe area comes from the display's notch and home-indicator bounds transformed into the actual viewport coordinates, including Retina scaling and expanded iPhone/iPad viewports.

The overlay calls the existing Godot input actions, preserving the game's move buffering and skill behavior. `TouchControls.IsTouchActive` prevents saved mouse movement or gamepad gyro settings from adding unwanted movement while using touch. No saved bindings are changed by the overlay.

## Automated input test

After a Debug build, run Godot .NET from the repository root:

```sh
GODOT --headless --path Project res://interface/touch/TouchSelfTest.tscn -- --touch-controls-self-test
```

Replace `GODOT` with the executable path. A successful run prints `TOUCH SELF-TEST PASS` and exits with status 0. The test uses Godot's actual input state to check every adventure button, analog and diagonal movement, simultaneous fingers, camera separation, menu confirmation versus pause, party mappings, lifecycle cleanup, and iPhone/iPad safe-area layout. The blank scene avoids starting the game's asynchronous loading sequence during the input test.

`GetMovementTouchCenter()`, `GetMovementTouchRadius()`, and `GetActionTouchPosition(action)` provide geometry for the rendered gameplay smoke test under `res://tests/ios/`.

The post-level regression loads the actual tutorial, completes it while Jump is held, uses quick Confirm taps through results and EXP, skips Event2 using touch, dismisses every reward category, and verifies the return to the menu:

```sh
GODOT --path Project --rendering-method gl_compatibility --resolution 1280x720 --windowed res://tests/ios/PostLevelTouchTest.tscn -- --ios-smoke-test --touch-controls
```

It uses isolated test save data and writes `build/ios/post-level-touch-result.txt` and `post-level-rewards.png`. A successful run prints `IOS_POST_LEVEL_TOUCH_PASS`. The desktop Compatibility run can report existing tutorial collision-scale warnings and rendering cleanup errors; check the test's pass/fail marker separately. The iOS package continues to use the Mobile renderer.
