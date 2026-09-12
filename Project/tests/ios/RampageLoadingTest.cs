using Godot;
using Project.Core;
using Project.Gameplay;
using Project.Interface;
using Project.Interface.Menus;
using Project.Interface.Touch;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary>Loads Sand Oasis Rampage through the actual Ready menu and async transition.</summary>
public partial class RampageLoadingTest : Node
{
	private const string LevelPath = "res://resource/level data/sand oasis/Act3Rampage.tres";
	private const string StagePath = "res://area/1 sand oasis/act 3/map/RampageAct.tscn";
	private const string CommonPath = "res://object/CommonResources.tscn";
	private readonly List<string> timeline = new();
	private string outputDirectory;
	private string phase = "startup";
	private ulong startTime;
	private ulong nextDiagnostic;
	private bool loadRequested;
	private bool preloadRequested;
	private bool finished;
	private bool countdownObserved;
	private int checks;
	private TouchControls controls;

	public override void _Ready()
	{
		if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--ios-smoke-test") < 0)
		{
			GD.PushError("Launch with -- --ios-smoke-test --touch-controls.");
			GetTree().Quit(2);
			return;
		}
		ProcessMode = ProcessModeEnum.Always;
		startTime = Time.GetTicksMsec();
		outputDirectory = OS.HasFeature("ios") ? SaveManager.DataDirectory.PathJoin("results") :
			ProjectSettings.GlobalizePath("res://../build/ios");
		foreach (string argument in OS.GetCmdlineUserArgs())
			if (argument.StartsWith("--smoke-output="))
				outputDirectory = argument.Substring("--smoke-output=".Length);
		DirAccess.MakeDirRecursiveAbsolute(outputDirectory);
		Record("renderer=" + RenderingServer.GetCurrentRenderingMethod() + "; PID=" + OS.GetProcessId());
		Callable.From(Run).CallDeferred();
	}

	public override void _Process(double delta)
	{
		if (finished || string.IsNullOrEmpty(outputDirectory))
			return;
		countdownObserved |= Countdown.IsCountdownActive;
		ulong now = Time.GetTicksMsec();
		if (now >= nextDiagnostic)
		{
			nextDiagnostic = now + 1000;
			Record(Diagnostics());
		}
		if (now - startTime > 240000)
			Fail(new TimeoutException("overall loading test exceeded 240 seconds; " + Diagnostics()));
	}

	private async void Run()
	{
		try
		{
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated save directory");
			SaveManager.ActiveSaveSlotIndex = -1;
			SaveManager.MenuData = SaveManager.GameData.CreateDefaultData();
			SaveManager.ActiveGameData.UnlockWorld(SaveManager.WorldEnum.SandOasis);
			SaveManager.ActiveGameData.UnlockStage("so_a3_rampage");
			SaveManager.Config.mouseControlMode = SaveManager.MouseControlModeEnum.Disabled;
			SaveManager.Config.isGyroEnabled = false;
			DebugManager.Instance.Call("ToggleCountdown", false);
			Menu.menuMemory[Menu.MemoryKeys.ActiveMenu] = (int)Menu.MemoryKeys.Title;
			controls = GetNode<TouchControls>("/root/TouchControls/Controls");
			TransitionManager.Instance.TransitionStarted += () => Record("signal: TransitionStarted");
			TransitionManager.Instance.TransitionProcess += () => Record("signal: TransitionProcess");
			TransitionManager.Instance.TransitionFinish += () => Record("signal: TransitionFinish");
			TransitionManager.Instance.SceneChanged += () => Record("signal: SceneChanged");
			foreach (string argument in OS.GetCmdlineUserArgs())
				if (argument.StartsWith("--rampage-pack="))
				{
					string pack = argument.Substring("--rampage-pack=".Length);
					Check(ProjectSettings.LoadResourcePack(pack), "mounted exported pack: " + pack);
				}
			if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--rampage-with-preload") >= 0)
			{
				SetPhase("Boot common-resource preload");
				preloadRequested = true;
				// Match Boot._Ready: start the request without waiting for it or consuming it.
				TransitionManager.Instance.LoadCommonResources();
			}

			SetPhase("load real menu");
			GetTree().CurrentScene = null; // Keep this test alive across the production scene change.
			Check(GetTree().ChangeSceneToFile(TransitionManager.MenuScenePath) == Error.Ok, "real menu requested");
			await Until(() => GetTree().CurrentScene?.SceneFilePath == TransitionManager.MenuScenePath, 30, "real menu ready");
			await Frames(3);
			ReadyMenu ready = GetTree().CurrentScene.FindChildren("*", "", true, false).OfType<ReadyMenu>().Single();
			LevelDataResource level = ResourceLoader.Load<LevelDataResource>(LevelPath);
			Check(level.LevelPath == StagePath && level.LevelID == "so_a3_rampage", "correct Rampage stage resource");
			Check(level.MissionType == LevelDataResource.MissionTypeEnum.Enemy && level.MissionObjectiveCount == 20,
				"requested mission is Defeat 20 Enemies");
			ready.SetupReadyMenu(level);
			SetPhase("ReadyMenu.LoadLevel async transition");
			loadRequested = true;
			ready.LoadLevel();
			Check(TransitionManager.IsLoadingLevel, "production asynchronous loading transition active");
			await Until(() => GetTree().CurrentScene?.SceneFilePath == StagePath, 180, "Rampage scene replaces menu");
			SetPhase("stage initialization and probes");
			await Until(() => IsInstanceValid(StageSettings.Player), 30, "Rampage player instantiated");
			Check(StageSettings.Instance.Data.LevelID == "so_a3_rampage", "loaded stage has correct mission ID");
			await Until(() => StageSettings.Instance.IsLevelIngame, 60, "stage initialization finishes");
			SetPhase("countdown");
			await Until(() => countdownObserved || Countdown.IsCountdownActive, 10, "real countdown starts");
			await Until(() => !Countdown.IsCountdownActive && !StageSettings.Player.IsCountdown &&
				!TransitionManager.IsTransitionActive && PauseMenu.AllowInputs, 20, "real countdown and loading fade finish");
			await Until(() => controls.Visible && controls.Context == TouchControls.ControlContext.Gameplay,
				10, "gameplay touch overlay becomes available");
			Check(StageSettings.Instance.Data.MissionObjectiveCount == 20 && StageSettings.Instance.CurrentObjectiveCount == 0,
				"enemy objective starts at 0 of 20");
			Control objective = HeadsUpDisplay.Instance.GetNode<Control>("Objectives");
			Check(objective.IsVisibleInTree() && objective.FindChildren("*", "Label", true, false)
				.OfType<Label>().Any(label => label.Text == "20"), "HUD displays the 20-enemy target");
			await Screenshot("rampage-ready.png");
			SetPhase("touch movement");
			PlayerController player = StageSettings.Player;
			Vector3 initialPosition = player.GlobalPosition;
			Vector2 stick = controls.GetMovementTouchCenter();
			GetViewport().PushInput(new InputEventScreenTouch { Index = 91, Position = stick, Pressed = true }, true);
			GetViewport().PushInput(new InputEventScreenDrag
				{ Index = 91, Position = stick + Vector2.Up * controls.GetMovementTouchRadius() }, true);
			await Frames(3);
			Check(Input.GetActionStrength("move_up") > .8f, "touch joystick presses movement action");
			await Until(() => player.GlobalPosition.DistanceTo(initialPosition) > .8f, 5, "touch moves player in Rampage physics");
			GetViewport().PushInput(new InputEventScreenTouch { Index = 91, Position = stick, Pressed = false }, true);
			await Frames(3);
			Check(!Input.IsActionPressed("move_up"), "movement touch releases cleanly");
			await Screenshot("rampage-movement.png");
			SetPhase("complete");
			finished = true;
			Record($"IOS_RAMPAGE_LOADING_PASS: {checks} checks");
			WriteReport("PASS");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			Fail(exception);
		}
	}

	private string Diagnostics()
	{
		string loading = "not requested";
		if (loadRequested)
		{
			Godot.Collections.Array progress = new();
			ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(StagePath, progress);
			loading = status + "; progress=" + (progress.Count > 0 ? progress[0].ToString() : "unknown");
		}
		string common = preloadRequested ? ResourceLoader.LoadThreadedGetStatus(CommonPath).ToString() : "not requested";
		return $"phase={phase}; threaded={loading}; common={common}; transition={TransitionManager.IsTransitionActive}; " +
			$"scene={GetTree().CurrentScene?.SceneFilePath ?? "none"}; " +
			$"stage={(IsInstanceValid(StageSettings.Instance) ? StageSettings.Instance.LevelState.ToString() : "none")}; " +
			$"player={IsInstanceValid(StageSettings.Player)}; countdown={Countdown.IsCountdownActive}; paused={GetTree().Paused}";
	}
	private void SetPhase(string value) { phase = value; Record("phase: " + value); }
	private void Record(string text)
	{
		string line = $"[{(Time.GetTicksMsec() - startTime) / 1000.0:F2}s] {text}";
		timeline.Add(line);
		GD.Print("IOS_RAMPAGE_LOADING: ", line);
		WriteReport(finished ? "FINISHED" : "RUNNING");
	}
	private void WriteReport(string status)
	{
		using FileAccess report = FileAccess.Open(outputDirectory.PathJoin("rampage-loading-result.txt"), FileAccess.ModeFlags.Write);
		report?.StoreString(status + "\n" + string.Join("\n", timeline) + "\n");
	}
	private void Fail(Exception exception)
	{
		if (finished)
			return;
		finished = true;
		controls?.ReleaseAll();
		Record("IOS_RAMPAGE_LOADING_FAIL: " + exception);
		WriteReport("FAIL");
		GD.PushError("IOS_RAMPAGE_LOADING_FAIL: " + exception);
		GetTree().Quit(1);
	}
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
			if (TransitionManager.Instance.IsLoadFailureVisible)
				throw new InvalidOperationException("Production loading recovery prompt opened during " + description + "; " + Diagnostics());
			if (Time.GetTicksMsec() > deadline)
				throw new TimeoutException(description + "; " + Diagnostics());
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
		Check(image.SavePng(outputDirectory.PathJoin(name)) == Error.Ok, "saved " + name);
	}
	private void Check(bool condition, string description)
	{
		if (!condition)
			throw new InvalidOperationException(description);
		checks++;
		Record("check: " + description);
	}
}
