using Godot;
using Project.Core;
using Project.Interface.Touch;
using System;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary>Exercises real failed resource requests and PackedScene instantiation, then native touch recovery.</summary>
public partial class LoadingRecoveryTest : Node
{
	private int checks;
	private int transitions;
	private TransitionManager manager;
	private TouchControls controls;
	private string outputDirectory;
	private const string TestDirectory = "user://ios-smoke-test/loading-recovery";

	public override void _Ready()
	{
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ios-smoke-test") < 0)
		{
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
			outputDirectory = OS.HasFeature("ios") ? SaveManager.DataDirectory.PathJoin("results") :
				ProjectSettings.GlobalizePath("res://../build/ios");
			foreach (string argument in OS.GetCmdlineUserArgs())
				if (argument.StartsWith("--smoke-output="))
					outputDirectory = argument.Substring("--smoke-output=".Length);
			DirAccess.MakeDirRecursiveAbsolute(outputDirectory);
			DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(TestDirectory));
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated save directory");
			manager = TransitionManager.Instance;
			manager.TransitionStarted += () => transitions++;
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			GetTree().CurrentScene = null; // Survive the same unload that failed gameplay transitions perform.

			string missing = TestDirectory.PathJoin("retry-target.tscn");
			if (FileAccess.FileExists(missing))
				Check(DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(missing)) == Error.Ok, "removed previous generated retry fixture");
			GD.Print("IOS_LOADING_RECOVERY: intentionally request a missing test scene; engine errors are expected.");
			Load(missing);
			await Until(() => manager.IsLoadFailureVisible, "missing scene reaches failure prompt");
			await CheckPrompt("missing scene");
			int beforeRetry = transitions;
			TapPrompt(true);
			await Until(() => transitions == beforeRetry + 1 && manager.IsLoadFailureVisible, "Retry retries a still-missing resource and returns to prompt");

			using (PackedScene recovered = CreateScene("RecoveredScene"))
				Check(ResourceSaver.Save(recovered, missing) == Error.Ok, "created repaired retry target");
			TapPrompt(true);
			await Until(() => GetTree().CurrentScene?.Name == "RecoveredScene" && !TransitionManager.IsTransitionActive,
				"Retry loads repaired scene through real asynchronous transition");
			Check(!manager.IsLoadFailureVisible, "successful Retry dismisses failure prompt");

			string invalid = TestDirectory.PathJoin("invalid-owner.scn");
			using (PackedScene broken = CreateScene("InvalidOwnerScene"))
			{
				// A two-node fixture uses the same serialized owner-index path as native Rampage's failure.
				Godot.Collections.Dictionary data = broken.Get("_bundled").AsGodotDictionary();
				int[] nodes = data["nodes"].AsInt32Array();
				Check(data["node_count"].AsInt32() == 2 && nodes.Length >= 14 && nodes[8] == 0,
					"malformed-owner fixture starts from a valid two-node scene");
				nodes[8] = 12345; // Child owner outside the scene's two-node table, without internal path flags.
				data["nodes"] = nodes;
				broken.Set("_bundled", data);
				Check(ResourceSaver.Save(broken, invalid) == Error.Ok, "saved intentionally invalid PackedScene fixture");
			}
			GD.Print("IOS_LOADING_RECOVERY: intentionally instantiate an invalid owner index; engine errors are expected.");
			Load(invalid);
			await Until(() => manager.IsLoadFailureVisible, "failed PackedScene instantiation reaches failure prompt");
			await CheckPrompt("invalid PackedScene");
			TapPrompt(false);
			await Until(() => GetTree().CurrentScene?.SceneFilePath == TransitionManager.MenuScenePath &&
				!TransitionManager.IsTransitionActive, "Main menu touch recovers from invalid PackedScene");
			await Frames(3);
			Check(!manager.IsLoadFailureVisible && controls.Visible, "menu touch controls return after recovery");
			Check(FileAccess.FileExists(TransitionManager.LoadDiagnosticPath), "load diagnostic log persisted in sandbox");
			using FileAccess report = FileAccess.Open(outputDirectory.PathJoin("loading-recovery-result.txt"), FileAccess.ModeFlags.Write);
			report?.StoreString($"PASS: {checks} loading failure/recovery checks\n");
			GD.Print("IOS_LOADING_RECOVERY_PASS: ", checks, " checks");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_LOADING_RECOVERY_FAIL: " + exception);
			GetTree().Quit(1);
		}
	}

	private static PackedScene CreateScene(string name)
	{
		Node root = new() { Name = name };
		Node child = new() { Name = "Child" };
		root.AddChild(child);
		child.Owner = root;
		PackedScene packed = new();
		Error result = packed.Pack(root);
		root.Free();
		if (result != Error.Ok)
			throw new InvalidOperationException("Could not pack test fixture: " + result);
		return packed;
	}
	private static void Load(string path)
	{
		TransitionManager.QueueSceneChange(path);
		TransitionManager.StartTransition(new() { inSpeed = .05f, outSpeed = .05f, color = Colors.Black, loadAsynchronously = true });
	}
	private async Task CheckPrompt(string description)
	{
		await Frames(3);
		Check(!TransitionManager.IsLoadingLevel, description + " is no longer presented as an active resource load");
		Check(!controls.Visible, description + " hides unrelated game touch controls");
		Check(!GetTree().Paused, description + " recovery panel processes while unpaused");
		Check(GetViewport().GetVisibleRect().HasPoint(manager.GetLoadFailureTouchPosition(true)) &&
			GetViewport().GetVisibleRect().HasPoint(manager.GetLoadFailureTouchPosition(false)), description + " recovery buttons are reachable");
	}
	private void TapPrompt(bool retry)
	{
		Vector2 point = manager.GetLoadFailureTouchPosition(retry);
		Check(point.X >= 0, retry ? "Retry touch position exists" : "Main menu touch position exists");
		GetViewport().PushInput(new InputEventScreenTouch { Index = 95, Position = point, Pressed = true }, true);
		GetViewport().PushInput(new InputEventScreenTouch { Index = 95, Position = point, Pressed = false }, true);
	}
	private async Task Frames(int count)
	{
		for (int i = 0; i < count; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}
	private async Task Until(Func<bool> condition, string description)
	{
		ulong deadline = Time.GetTicksMsec() + 15000;
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
		GD.Print("IOS_LOADING_RECOVERY_CHECK: ", description);
	}
}
