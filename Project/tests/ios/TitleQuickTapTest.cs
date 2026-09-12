using Godot;
using Project.Core;
using Project.Interface.Menus;
using Project.Interface.Touch;
using System;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary> Reproduces a native slow-frame tap against the real title scene. </summary>
public partial class TitleQuickTapTest : Node
{
	private int checks;
	private int confirmations;
	private Title title;
	private AnimationPlayer animator;
	private TouchControls controls;

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
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated save directory");
			Menu.menuMemory[Menu.MemoryKeys.ActiveMenu] = (int)Menu.MemoryKeys.Title;
			GetTree().CurrentScene = null;
			Check(GetTree().ChangeSceneToFile(TransitionManager.MenuScenePath) == Error.Ok, "real menu scene requested");
			await Until(() => GetTree().CurrentScene?.HasNode("Clip/Title") == true, "real title scene loaded");
			title = GetTree().CurrentScene.GetNode<Title>("Clip/Title");
			animator = title.GetNode<AnimationPlayer>("AnimationPlayer");
			animator.AnimationStarted += animation =>
			{
				if (animation == "confirm")
					confirmations++;
			};
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			await QuickTouch("sys_select", 80);
			await QuickTouch("sys_pause", 81);

			// Also preserve the built-in keyboard accept path when a key is released
			// before the next physics tick; touch Start drives the mapped sys_pause path.
			await PrepareTitle();
			int previous = confirmations;
			Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = true });
			Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, Pressed = false });
			Input.FlushBufferedEvents();
			Check(Input.IsActionJustPressed("ui_accept") && !Input.IsAnythingPressed(), "built-in accept tap is released before menu processing");
			await Until(() => confirmations == previous + 1, "released built-in accept reaches title confirmation");

			using FileAccess report = FileAccess.Open(ProjectSettings.GlobalizePath("res://../build/ios/title-quick-tap-result.txt"), FileAccess.ModeFlags.Write);
			report?.StoreString($"PASS: {checks} real title quick-tap checks\n");
			GD.Print("IOS_TITLE_QUICK_TAP_PASS: ", checks, " checks");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_TITLE_QUICK_TAP_FAIL: " + exception);
			GetTree().Quit(1);
		}
	}

	private async Task PrepareTitle()
	{
		controls.ReleaseAll();
		GetTree().CurrentScene.GetNode<MainMenu>("Clip/MainMenu").DisableProcessing();
		title.DisableProcessing();
		title.ShowMenu();
		await Until(() => title.Get("isProcessing").AsBool(), "title entrance enabled menu processing");
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Check(controls.Context == TouchControls.ControlContext.Menu, "title receives menu touch controls");
	}

	private async Task QuickTouch(string action, int finger)
	{
		await PrepareTitle();
		Vector2 position = controls.GetActionTouchPosition(action);
		Check(position.X >= 0, action + " button is visible");
		int previous = confirmations;
		GetViewport().PushInput(new InputEventScreenTouch { Index = finger, Position = position, Pressed = true }, true);
		GetViewport().PushInput(new InputEventScreenTouch { Index = finger, Position = position, Pressed = false }, true);
		Check(!Input.IsActionPressed(action) && !Input.IsAnythingPressed(), action + " tap is fully released before menu processing");
		Check(Input.IsActionJustPressed(action), action + " retains its quick-tap press edge");
		await Until(() => confirmations == previous + 1, action + " quick tap reaches actual title confirmation");
	}

	private async Task Until(Func<bool> condition, string description)
	{
		ulong deadline = Time.GetTicksMsec() + 10000;
		while (!condition())
		{
			if (Time.GetTicksMsec() > deadline)
				throw new TimeoutException(description);
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
		Check(true, description);
	}

	private void Check(bool condition, string description)
	{
		if (!condition)
			throw new InvalidOperationException(description);
		checks++;
		GD.Print("IOS_TITLE_QUICK_TAP_CHECK: ", description);
	}
}
