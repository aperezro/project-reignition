using Godot;
using Project.Core;
using Project.Gameplay;
using Project.Interface;
using Project.Interface.Menus;
using Project.Interface.Touch;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary>Runs the actual tutorial results, EXP, story event and reward screens using screen touches.</summary>
public partial class PostLevelTouchTest : Node
{
	private int checks;
	private TouchControls controls;
	private string outputDirectory;
	private static bool Processing(object menu) => (bool)menu.GetType()
		.GetField("isProcessing", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(menu);

	public override void _Ready()
	{
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ios-smoke-test") < 0)
		{
			GetTree().Quit(2);
			return;
		}
		Callable.From(Run).CallDeferred();
	}

	private async void Run()
	{
		try
		{
			outputDirectory = ProjectSettings.GlobalizePath("res://../build/ios");
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated save directory");
			SaveManager.ActiveSaveSlotIndex = -1;
			SaveManager.MenuData = SaveManager.GameData.CreateDefaultData();
			Menu.menuMemory[Menu.MemoryKeys.ActiveMenu] = (int)Menu.MemoryKeys.LevelSelect;
			DebugManager.Instance.Call("ToggleCountdown", false);
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			GetTree().CurrentScene = null;
			Check(GetTree().ChangeSceneToFile("res://area/0 lost prologue/act/map/Tutorial.tscn") == Error.Ok,
				"real tutorial scene requested");
			await Until(() => IsInstanceValid(StageSettings.Player) && StageSettings.Instance.IsLevelIngame &&
				!StageSettings.Player.IsCountdown && !TransitionManager.IsTransitionActive && PauseMenu.AllowInputs,
				90, "tutorial ready for gameplay");
			await Frames(3);
			LevelResult result = GetTree().CurrentScene.FindChildren("*", "", true, false).OfType<LevelResult>().Single();
			ExperienceResult experience = GetTree().CurrentScene.FindChildren("*", "", true, false).OfType<ExperienceResult>().Single();
			AnimationPlayer resultAnimator = result.GetNode<AnimationPlayer>("AnimationPlayer");
			NotificationManager notifications = NotificationManager.Instance;
			notifications.UpdateCounters();
			// Exercise each notification category in addition to the tutorial's real world/mission unlocks.
			foreach (NotificationManager.NotificationType type in Enum.GetValues<NotificationManager.NotificationType>())
				notifications.AddNotification(type, type switch
				{
					NotificationManager.NotificationType.WorldRing => "unlock_ring_sand_oasis",
					NotificationManager.NotificationType.TimeAttack => "unlock_time_attack",
					_ => "unlock_" + type.ToString().ToLower()
				});
			StageSettings.Instance.CurrentEXP = 15000;
			Vector2 jump = controls.GetActionTouchPosition("button_jump");
			Touch(60, jump, true);
			Check(Input.IsActionPressed("button_jump"), "jump held as player completes level");
			StageSettings.Instance.FinishLevel(true);
			await Frames(3);
			Check(!Input.IsActionPressed("button_jump"), "level completion releases held jump");
			Touch(60, jump, false);
			AssertConfirm("level results");
			await Until(() => Processing(result), 15, "results accept input");
			await Until(() => !resultAnimator.IsPlaying() || resultAnimator.CurrentAnimationPosition > 1.05,
				15, "results animation can be skipped");
			await TapConfirm();
			await Until(() => !resultAnimator.IsPlaying(), 15, "result tally finishes");
			await TapConfirm();
			await Until(() => Processing(experience), 10, "real EXP screen accepts input");
			await Frames(3);
			AssertConfirm("EXP results");
			await TapConfirm();
			await TapConfirm();
			await Until(() => GetTree().CurrentScene is EventPlayer, 30, "tutorial post-level story event loads");
			await Until(() => !TransitionManager.IsTransitionActive, 10, "story event fade finishes");
			await Frames(3);
			Check(controls.Context == TouchControls.ControlContext.Cutscene, "playing story uses cutscene controls");
			Vector2 skip = controls.GetActionTouchPosition("sys_pause");
			Touch(61, skip, true);
			await Until(() => notifications.GetNode<Control>("Background").Visible && Processing(notifications),
				10, "skipped story shows actual queued rewards");
			Touch(61, skip, false);
			await Frames(3);
			Check(DebugManager.Instance.IsCutsceneActive && GetTree().CurrentScene is EventPlayer,
				"finished story stays loaded behind rewards (regression precondition)");
			await Screenshot("post-level-rewards.png");
			int rewards = 0;
			while (GetTree().CurrentScene is EventPlayer)
			{
				await Until(() => Processing(notifications) || GetTree().CurrentScene is not EventPlayer,
					10, "next reward or menu becomes ready");
				if (GetTree().CurrentScene is not EventPlayer)
					break;
				AssertConfirm("reward " + (++rewards));
				Check(!Input.IsActionPressed("sys_pause") && !Input.IsActionPressed("button_jump"),
					"reward has no held skip/jump actions");
				await TapConfirm();
				await Until(() => !Processing(notifications), 3, "Confirm advances reward");
				if (rewards > 20)
					throw new InvalidOperationException("reward flow did not finish");
			}
			Check(rewards >= 7, "all reward categories were dismissed by touch");
			await Until(() => !TransitionManager.IsTransitionActive, 10, "return-to-menu fade finishes");
			await Frames(3);
			AssertConfirm("returned menu");
			Check(!DebugManager.Instance.IsCutsceneActive, "completed cutscene context clears after rewards");
			Check(SaveManager.ActiveGameData.exp >= 15000, "EXP was granted by real result flow");
			using FileAccess report = FileAccess.Open(outputDirectory.PathJoin("post-level-touch-result.txt"), FileAccess.ModeFlags.Write);
			report?.StoreString($"PASS: {checks} checks; {rewards} rewards dismissed\n");
			GD.Print("IOS_POST_LEVEL_TOUCH_PASS: ", checks, " checks; ", rewards, " rewards dismissed");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_POST_LEVEL_TOUCH_FAIL: " + exception);
			GetTree().Quit(1);
		}
	}

	private void AssertConfirm(string screen)
	{
		Check(controls.Visible && controls.Context == TouchControls.ControlContext.Menu,
			screen + " keeps menu touch controls visible");
		Vector2 confirm = controls.GetActionTouchPosition("sys_select");
		Check(confirm.X >= 0 && controls.SafeRect.HasPoint(confirm), screen + " Confirm is reachable in safe area");
	}

	private async Task TapConfirm()
	{
		await Frames(2);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Vector2 position = controls.GetActionTouchPosition("sys_select");
		Check(position.X >= 0, "Confirm exists before tap");
		// A physical device can deliver a complete quick tap before the next game tick.
		Touch(62, position, true);
		Touch(62, position, false);
		Check(Input.IsActionJustPressed("sys_select") && Input.IsActionJustPressed("ui_select") &&
			!Input.IsActionPressed("sys_select"), "released Confirm tap retains both menu input edges");
		await Frames(2);
	}
	private void Touch(int finger, Vector2 position, bool pressed) => GetViewport().PushInput(
		new InputEventScreenTouch { Index = finger, Position = position, Pressed = pressed }, true);
	private async Task Frames(int count)
	{
		for (int i = 0; i < count; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}
	private async Task Until(Func<bool> condition, double seconds, string description)
	{
		ulong deadline = Time.GetTicksMsec() + (ulong)(seconds * 1000);
		while (!condition())
		{
			if (Time.GetTicksMsec() > deadline)
				throw new TimeoutException(description + "; context=" + controls.Context +
					" transition=" + TransitionManager.IsTransitionActive);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		Check(true, description);
	}
	private async Task Screenshot(string name)
	{
		if (DisplayServer.GetName() == "headless")
			return;
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		using Image image = GetViewport().GetTexture().GetImage();
		Check(image.SavePng(outputDirectory.PathJoin(name)) == Error.Ok, "reward screenshot saved");
	}
	private void Check(bool condition, string description)
	{
		if (!condition)
			throw new InvalidOperationException(description);
		checks++;
		GD.Print("IOS_POST_LEVEL_TOUCH_CHECK: ", description);
	}
}
