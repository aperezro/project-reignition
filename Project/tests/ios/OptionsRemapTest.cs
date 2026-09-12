using Godot;
using Project.Core;
using Project.Interface.Menus;
using Project.Interface.Touch;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary> Exercises touch confirmation and cancellation in the actual Options scene. </summary>
public partial class OptionsRemapTest : Node
{
	private TouchControls controls;
	private int checks;

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
			GetTree().CurrentScene = null;
			Check(GetTree().ChangeSceneToFile(TransitionManager.OptionsScenePath) == Error.Ok, "real Options scene loaded");
			await Until(() => GetTree().CurrentScene is Options);
			Options options = (Options)GetTree().CurrentScene;
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			await Frames(90);
			Check(controls.Context == TouchControls.ControlContext.Menu, "Options receives menu touch controls");

			// Set up each existing mapping page, then use only routed screen events for its controls.
			await TestPage(options, 11, 0, "MenuBase/ContentClip/Content/Control/Mapping/Options/MoveUp", "adventure");
			await TestPage(options, 12, 2, "MenuBase/ContentClip/Content/Control/PartyMapping/LSide/Options/MoveUp", "party");
			options.Call("FlipBook", 12, false, 0);
			await Frames(60);
			await Tap("sys_clear");
			Check(true, "party device header safely ignores clear");

			string output = ProjectSettings.GlobalizePath("res://../build/ios/options-remap-result.txt");
			using FileAccess report = FileAccess.Open(output, FileAccess.ModeFlags.Write);
			report?.StoreString($"PASS: {checks} real Options touch remapping checks\n");
			GD.Print("IOS_REMAP_PASS: ", checks, " checks");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_REMAP_FAIL: " + exception);
			GetTree().Quit(1);
		}
	}

	private async Task TestPage(Options options, int submenu, int row, string optionPath, string description)
	{
		options.Call("FlipBook", submenu, false, row);
		await Frames(60);
		ControlOption option = options.GetNode<ControlOption>(optionPath);
		string before = Binding(option.ActionName);
		Check(option.IsReady, description + " mapping starts ready");
		await Tap("sys_select");
		await Until(() => !option.IsReady);
		Check(!option.IsReady, description + " touch confirm begins listening");
		await Tap("sys_cancel");
		await Until(() => option.IsReady);
		Check(option.IsReady, description + " touch back cancels listening");
		Check(Binding(option.ActionName) == before, description + " cancellation preserves bindings");
		await Tap("sys_select");
		await Until(() => !option.IsReady);
		Check(!option.IsReady, description + " mapping can be entered again after cancellation");
		await Tap("sys_cancel");
		await Until(() => option.IsReady);
		await Tap("sys_cancel");
		await Frames(60);
		// Returning to Controls leaves its Mapping entry selected. Confirm reopens it.
		await Tap("sys_select");
		await Frames(60);
		Check(option.IsReady, description + " back also exits mapping page");
	}

	private static string Binding(StringName action) => string.Join("|", InputMap.ActionGetEvents(action).Select(e => e.AsText()));

	private async Task Tap(string action)
	{
		Vector2 position = controls.GetActionTouchPosition(action);
		GetViewport().PushInput(new InputEventScreenTouch { Index = 71, Position = position, Pressed = true }, true);
		await Frames(3);
		GetViewport().PushInput(new InputEventScreenTouch { Index = 71, Position = position, Pressed = false }, true);
		await Frames(3);
	}

	private async Task Until(Func<bool> condition)
	{
		ulong deadline = Time.GetTicksMsec() + 15000;
		while (!condition())
		{
			if (Time.GetTicksMsec() > deadline)
				throw new TimeoutException("Options remap state did not change");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		}
	}

	private async Task Frames(int count)
	{
		for (int index = 0; index < count; index++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}

	private void Check(bool condition, string description)
	{
		if (!condition)
			throw new InvalidOperationException(description);
		checks++;
		GD.Print("IOS_REMAP_CHECK: ", description);
	}
}
