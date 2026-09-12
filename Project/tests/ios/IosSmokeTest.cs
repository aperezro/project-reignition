using Godot;
using Project.Core;
using Project.Gameplay;
using Project.Interface;
using Project.Interface.Touch;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary> Explicitly launched integration test; never attached to a shipping scene. </summary>
public partial class IosSmokeTest : Node
{
	private const string StagePath = "res://area/0 lost prologue/act/map/Classic01.tscn";
	private readonly List<string> checks = new();
	private TouchControls controls;
	private string outputDirectory;

	public override void _Ready()
	{
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ios-smoke-test") < 0)
		{
			GD.PushError("Launch this test scene with -- --ios-smoke-test --touch-controls.");
			GetTree().Quit(2);
			return;
		}
		ProcessMode = ProcessModeEnum.Always;
		Callable.From(Run).CallDeferred();
	}

	private async void Run()
	{
		try
		{
			outputDirectory = OS.HasFeature("ios")
				? SaveManager.DataDirectory.PathJoin("results")
				: ProjectSettings.GlobalizePath("res://../build/ios");
			foreach (string argument in OS.GetCmdlineUserArgs())
				if (argument.StartsWith("--smoke-output="))
					outputDirectory = argument.Substring("--smoke-output=".Length);
			DirAccess.MakeDirRecursiveAbsolute(outputDirectory);
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated save directory");
			Check(RenderingServer.GetCurrentRenderingMethod() == "mobile", "Mobile renderer active");
			SaveManager.ActiveSaveSlotIndex = -1;
			SaveManager.MenuData = SaveManager.GameData.CreateDefaultData();
			SaveManager.Config.mouseControlMode = SaveManager.MouseControlModeEnum.Absolute;
			SaveManager.Config.isGyroEnabled = true;
			DebugManager.Instance.Call("ToggleCountdown", false);
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			Check(controls.Visible, "touch overlay enabled");

			// Remain under /root while ChangeSceneToFile replaces the active scene.
			GetTree().CurrentScene = null;
			Error error = GetTree().ChangeSceneToFile(StagePath);
			Check(error == Error.Ok, "tutorial scene requested");
			await Until(() => IsInstanceValid(StageSettings.Player), 90, "player loaded");
			await Until(() => StageSettings.Instance.IsLevelIngame, 90, "level rendering initialized");
			await Until(() => Countdown.IsCountdownActive, 10, "countdown started");
			await Until(() => !Countdown.IsCountdownActive && !StageSettings.Player.IsCountdown &&
				!TransitionManager.IsTransitionActive && PauseMenu.AllowInputs, 15, "countdown completed");
			await Frames(3);
			await Screenshot("ios-smoke-ready.png");

			PlayerController player = StageSettings.Player;
			AnimationTree animationTree = player.Animator.GetNode<AnimationTree>("AnimationTree");
			await Frames(90);
			CheckGroundAnimation(player, animationTree, "before jumping");
			Vector3 initialPosition = player.GlobalPosition;
			Vector2 stick = controls.MoveCenter;
			Touch(41, stick, true);
			Drag(41, stick + Vector2.Up * controls.MoveRadius);
			await Frames(3);
			Check(Input.GetActionStrength("move_up") > .8f, "touch joystick presses movement action");
			Check(TouchControls.IsTouchActive, "touch input suppresses saved mouse and gyro control");
			await Until(() => player.GlobalPosition.DistanceTo(initialPosition) > .8f, 5, "player moves through gameplay physics");

			Vector2 jump = ButtonPosition("button_jump");
			Touch(42, jump, true);
			await Until(() => player.IsJumping || player.IsSpinJump || player.VerticalSpeed > 1f, 3, "second touch triggers jump while moving");
			Check(Input.IsActionPressed("move_up"), "movement remains held during multitouch jump");
			await Screenshot("ios-smoke-jump.png");
			Touch(42, jump, false);
			Touch(41, stick, false);
			await Frames(3);
			Check(!Input.IsActionPressed("move_up") && !Input.IsActionPressed("button_jump"), "touch releases clear movement and jump");
			await Until(() => player.IsOnGround && !player.IsJumping && !player.IsSpinJump, 10, "touch jump lands");
			await Frames(60);
			CheckGroundAnimation(player, animationTree, "after landing");
			await Screenshot("ios-smoke-landed.png");

			SaveManager.ActiveSkillRing.EquipSkill(SkillKey.SpinJump, 0, true);
			Check(SaveManager.ActiveSkillRing.IsSkillEquipped(SkillKey.SpinJump), "spin jump equipped in isolated test save");
			jump = ButtonPosition("button_jump");
			Touch(45, jump, true);
			await Until(() => !player.IsOnGround, 3, "held touch starts another jump");
			await Until(() => player.IsSpinJump, 3, "held touch enters actual spin jump state");
			await Frames(12);
			Check(player.Effect.spinFX.Visible && SpinAlpha(player) > .9f, "spinning ball appears during spin jump");
			await Screenshot("ios-smoke-spin-jump.png");
			await Until(() => player.IsOnGround && !player.IsJumping && !player.IsSpinJump, 10, "held jump lands");
			await Frames(90);
			CheckGroundAnimation(player, animationTree, "while jump remains held after landing");
			Touch(45, jump, false);
			await Frames(3);
			Check(!Input.IsActionPressed("button_jump"), "held jump releases without repeating");
			await Screenshot("ios-smoke-spin-landed.png");

			// Homing attacks may request the effect twice; a stale fade must not hide a new spin.
			player.Effect.StartSpinFX();
			player.Effect.StopSpinFX();
			player.Effect.StartSpinFX();
			await Frames(18);
			Check(player.Effect.spinFX.Visible && SpinAlpha(player) > .99f, "new spin replaces an interrupted fade");
			player.Effect.StartSpinFX();
			player.Effect.CanelSpinFX();
			await Frames(18);
			Check(!player.Effect.spinFX.Visible && Mathf.IsZeroApprox(SpinAlpha(player)), "cancelled spin cannot reappear from an old fade");

			await Tap("sys_pause", 43);
			await Until(() => GetTree().Paused, 4, "touch pauses gameplay");
			Vector3 pausedPosition = player.GlobalPosition;
			await Frames(30);
			Check(player.GlobalPosition.IsEqualApprox(pausedPosition), "player physics stays frozen while paused");
			await Screenshot("ios-smoke-paused.png");
			await Tap("sys_pause", 44);
			await Until(() => !GetTree().Paused, 4, "touch resumes gameplay");

			SaveManager.SaveConfig();
			SaveManager.SaveSharedData();
			SaveManager.SaveTimeAttackData();
			Check(FileAccess.FileExists(SaveManager.DataDirectory.PathJoin("config.cfg")), "configuration saves in sandbox");
			Check(FileAccess.FileExists(SaveManager.SaveDirectory.PathJoin("timeAttack.dat")), "time attack saves in sandbox");
			using (FileAccess report = FileAccess.Open(outputDirectory.PathJoin("ios-smoke-result.txt"), FileAccess.ModeFlags.Write))
				report.StoreString("PASS\n" + string.Join("\n", checks) + "\n");
			GD.Print("IOS_SMOKE_PASS: ", checks.Count, " checks; artifacts: ", outputDirectory);
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_SMOKE_FAIL: " + exception);
			if (!string.IsNullOrEmpty(outputDirectory))
			{
				using FileAccess report = FileAccess.Open(outputDirectory.PathJoin("ios-smoke-result.txt"), FileAccess.ModeFlags.Write);
				report?.StoreString("FAIL\n" + string.Join("\n", checks) + "\n" + exception);
			}
			GetTree().Quit(1);
		}
	}

	private Vector2 ButtonPosition(string action)
	{
		foreach (TouchControls.TouchButton button in controls.Buttons)
			if (Array.IndexOf(button.Actions, action) >= 0)
				return button.Center;
		throw new InvalidOperationException("No visible touch button for " + action);
	}

	private void CheckGroundAnimation(PlayerController player, AnimationTree animationTree, string description)
	{
		GD.Print("IOS_ANIMATION_STATE: ", description, "; state=", player.StateMachine.CurrentState.Name,
			"; ground=", animationTree.Get("parameters/ground_transition/current_state"),
			"; normal=", animationTree.Get("parameters/state_transition/current_state"),
			"; oneshot=", animationTree.Get("parameters/oneshot_trigger/active"));
		Check(player.IsOnGround && !player.IsJumping && !player.IsSpinJump, "player stays grounded " + description);
		Check(animationTree.Get("parameters/ground_transition/current_state").AsString() == "enabled",
			"ground animation selected " + description);
		Check(animationTree.Get("parameters/state_transition/current_state").AsString() == "normal",
			"normal animation selected " + description);
		Check(!animationTree.Get("parameters/oneshot_trigger/active").AsBool(),
			"countdown animation has stopped " + description);
		Check(!player.Effect.spinFX.Visible && Mathf.IsZeroApprox(SpinAlpha(player)),
			"spinning ball hidden " + description);
	}

	private static float SpinAlpha(PlayerController player) =>
		((ShaderMaterial)player.Effect.spinFX.MaterialOverride).GetShaderParameter("effect_alpha").AsSingle();

	private async Task Tap(string action, int finger)
	{
		await Frames(3);
		Vector2 position = ButtonPosition(action);
		Touch(finger, position, true);
		await Frames(3);
		Touch(finger, position, false);
	}

	private void Touch(int finger, Vector2 position, bool pressed) =>
		GetViewport().PushInput(new InputEventScreenTouch { Index = finger, Position = position, Pressed = pressed }, true);
	private void Drag(int finger, Vector2 position) =>
		GetViewport().PushInput(new InputEventScreenDrag { Index = finger, Position = position }, true);

	private async Task Frames(int count)
	{
		for (int index = 0; index < count; index++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}

	private async Task Until(Func<bool> condition, double seconds, string description)
	{
		ulong deadline = Time.GetTicksMsec() + (ulong)(seconds * 1000);
		while (!condition())
		{
			if (Time.GetTicksMsec() > deadline)
				throw new TimeoutException(description);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		Check(true, description);
	}

	private async Task Screenshot(string filename)
	{
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using Image image = GetViewport().GetTexture().GetImage();
		Check(image.SavePng(outputDirectory.PathJoin(filename)) == Error.Ok, "saved " + filename);
	}

	private void Check(bool condition, string description)
	{
		if (!condition)
			throw new InvalidOperationException(description);
		checks.Add(description);
		GD.Print("IOS_SMOKE_CHECK: ", description);
	}
}
