using Godot;
using Project.Gameplay;
using Project.Interface;
using Project.Interface.Menus;
using System;

namespace Project.Core;

/// <summary>
/// Handles transitions and scene changes.
/// The transition will play halfway, at which point a signal will be emitted, allowing for loading.
/// Call <see cref="FinishTransition"/> to complete the transition.
/// </summary>
public partial class TransitionManager : Node
{
	public static TransitionManager Instance;
	/// <summary> Path to the main menu scene. </summary>
	public const string MenuScenePath = "res://interface/menu/Menu.tscn";
	/// <summary> Path to story events. </summary>
	public const string EventScenePath = "res://video/event/scene/";
	public const string OptionsScenePath = "res://interface/menu/options/Options.tscn";
	public const string PartyScenePath = "res://party/scene/party menu/PartyMenu.tscn";
	public const string SpecialBookScenePath = "res://interface/menu/special book/SpecialBook.tscn";
	public const string TimeAttackScenePath = "res://interface/menu/time attack/TimeAttack.tscn";
	public const string TimeAttackResultsPath = "res://interface/menu/time attack/TimeAttackResults.tscn";

	public bool IsReloadingScene { get; private set; }
	private Error sceneRequestError;
	private string diagnosticScene = string.Empty;
	private StringName lastLoadingText;
	private ColorRect failureRoot;
	private PanelContainer failurePanel;
	private Button retryButton;
	private Button menuButton;
	private string failedScene;
	private TransitionData failedTransition;
	public bool IsLoadFailureVisible => failureRoot?.Visible == true;

	[Export] private Label loadLabel;
	[Export] private ColorRect fade;
	[Export] private AnimationPlayer animator;
	[Export] private AnimationPlayer loadingAnimator;
	[Export] private Control missionDescriptionRoot;
	[Export] private Label missionDescriptionLabel;

	public override void _EnterTree() => Instance = this;

	#region Transition Types
	// Simple cut transition. During loading, everything will freeze temporarily.
	private void StartCut() => EmitSignal(SignalName.TransitionProcess);
	private void StartFade()
	{
		if (IsTransitionActive)
		{
			GD.PushWarning("Transition is already active!");
			return;
		}

		if (CurrentTransitionData.loadAsynchronously)
			loadingAnimator.Play("show");

		IsTransitionActive = true;
		fade.Color = CurrentTransitionData.color;
		animator.Play("fade");

		if (CurrentTransitionData.inSpeed == 0)
		{
			animator.Seek(animator.CurrentAnimationLength, true);
			CallDeferred(MethodName.EmitSignal, SignalName.TransitionProcess);
		}
		else
		{
			animator.SpeedScale = 1.0f / CurrentTransitionData.inSpeed;
			animator.Connect(AnimationPlayer.SignalName.AnimationFinished, new(Instance, MethodName.TransitionLoading), (uint)ConnectFlags.OneShot);
		}

		EmitSignal(SignalName.TransitionStarted);
	}

	private void FinishFade()
	{
		if (CurrentTransitionData.loadAsynchronously)
			loadingAnimator.Play("hide");

		animator.PlayBackwards("fade");
		if (Mathf.IsZeroApprox(CurrentTransitionData.outSpeed)) // Cut
			animator.Seek(animator.CurrentAnimationLength, true);
		else
			animator.SpeedScale = 1.0f / CurrentTransitionData.outSpeed;

		if (!animator.IsConnected(AnimationPlayer.SignalName.AnimationFinished, new(Instance, MethodName.TransitionFinished)))
			animator.Connect(AnimationPlayer.SignalName.AnimationFinished, new(Instance, MethodName.TransitionFinished), (uint)ConnectFlags.OneShot);
	}
	#endregion

	private TransitionData CurrentTransitionData { get; set; }
	public static bool IsTransitionActive { get; set; }
	public static bool IsLoadingLevel => IsTransitionActive && Instance.CurrentTransitionData.loadAsynchronously;
	/// <summary> Called when the scene changes. </summary>
	[Signal] public delegate void SceneChangedEventHandler();
	/// <summary> Called whenever a transition is started. </summary>
	[Signal] public delegate void TransitionStartedEventHandler();
	/// <summary> Called in the middle of the transition (when the screen is completely black). </summary>
	[Signal] public delegate void TransitionProcessEventHandler();
	/// <summary> Called when the transition is finished. </summary>
	[Signal] public delegate void TransitionFinishEventHandler();
	private void TransitionLoading(string _) => EmitSignal(SignalName.TransitionProcess);
	private void TransitionFinished(string _)
	{
		IsTransitionActive = false;
		LogLoad("transition_finished");
		EmitSignal(SignalName.TransitionFinish);
	}

	public static void StartTransition(TransitionData data)
	{
		SoundManager.SetAudioBusVolume(SoundManager.AudioBuses.GameSfx, 0); // Mute gameplay sound effects
		Instance.animator.Play("RESET"); // Reset animator, just in case
		Instance.animator.Advance(0);
		Instance.UpdateLoadingText(null);

		Instance.CurrentTransitionData = data;
		Instance.missionDescriptionRoot.Visible = data.showMissionDescription;
		Instance.sceneRequestError = Error.Ok;
		Instance.diagnosticScene = Instance.QueuedScene ?? string.Empty;
		Instance.LogLoad("transition_started", $"async={data.loadAsynchronously} common_in_progress={Instance.isLoadingCommonResources}");

		if (data.loadAsynchronously) // Start loading immediately
		{
			GD.Print("Async loading started.");
			Instance.sceneRequestError = ResourceLoader.LoadThreadedRequest(Instance.QueuedScene, string.Empty);
			Instance.LogLoad("scene_request", Instance.sceneRequestError.ToString());
		}

		if (data.inSpeed == 0 && data.outSpeed == 0)
		{
			Instance.StartCut(); // Cut transition
			return;
		}

		Instance.StartFade();
	}

	public static void FinishTransition()
	{
		Instance.LogLoad("finish_requested");
		if (IsInstanceValid(StageSettings.Instance) && (StageSettings.Instance.IsLevelLoading || StageSettings.Instance.IsLevelIngame))
			SoundManager.SetAudioBusVolume(SoundManager.AudioBuses.GameSfx, 100); // Unmute gameplay sound effects

		Instance.UpdateLoadingText(null);
		Instance.FinishFade();
	}

	/// <summary> The scene to load. Note that the scene only gets applied if queued using QueueSceneChange(). </summary>
	public string QueuedScene { get; set; }
	/// <summary> Queues a scene to load and connects the TransitionProcess signal. Be sure to call StartTransition to actually transition to the scene. </summary>
	public static void QueueSceneChange(string scene)
	{
		Instance.QueuedScene = scene;

		var call = new Callable(Instance, MethodName.ApplySceneChange);
		if (!Instance.IsConnected(SignalName.TransitionProcess, call))
			Instance.Connect(SignalName.TransitionProcess, call, (uint)ConnectFlags.OneShot);
	}

	private async void ApplySceneChange()
	{
		SoundManager.instance.CancelDialog(); // Cancel any active dialog
		string path = QueuedScene;
		IsReloadingScene = string.IsNullOrEmpty(path);
		if (IsReloadingScene)
			path = GetTree().CurrentScene?.SceneFilePath ?? string.Empty;
		diagnosticScene = path;
		try
		{
			Error changeError;
			if (IsReloadingScene)
			{
				LogLoad("scene_reload_begin");
				changeError = GetTree().ReloadCurrentScene();
			}
			else
			{
				// Release the previous scene before loading large stages, as before.
				LogLoad("scene_unload_begin");
				GetTree().UnloadCurrentScene();
				LogLoad("scene_unload_finished");
				if (CurrentTransitionData.loadAsynchronously)
				{
					if (sceneRequestError != Error.Ok)
					{
						ShowLoadFailure(path, $"Request failed: {sceneRequestError}");
						return;
					}
					ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(path);
					LogLoad("scene_status", status.ToString());
					while (status == ResourceLoader.ThreadLoadStatus.InProgress)
					{
						await ToSignal(GetTree().CreateTimer(.1f), SceneTreeTimer.SignalName.Timeout);
						ResourceLoader.ThreadLoadStatus next = ResourceLoader.LoadThreadedGetStatus(path);
						if (next != status)
							LogLoad("scene_status", next.ToString());
						status = next;
					}
					if (status != ResourceLoader.ThreadLoadStatus.Loaded)
					{
						// Collect a completed failed request to release its user token. Otherwise
						// Godot reuses the same failed token when the player chooses Retry.
						if (status == ResourceLoader.ThreadLoadStatus.Failed)
							ResourceLoader.LoadThreadedGet(path);
						ShowLoadFailure(path, $"Resource load failed: {status}");
						return;
					}
					LogLoad("scene_get_begin");
					PackedScene scene = ResourceLoader.LoadThreadedGet(path) as PackedScene;
					LogLoad("scene_get_finished", scene == null ? "null_or_wrong_type" : "PackedScene");
					if (scene == null)
					{
						ShowLoadFailure(path, "Loaded resource is not a PackedScene.");
						return;
					}
					LogLoad("scene_instantiate_begin");
					changeError = GetTree().ChangeSceneToPacked(scene);
					// NativeAOT may otherwise collect this wrapper during C# constructors
					// or property setters in instantiation; the native argument is borrowed.
					GC.KeepAlive(scene);
				}
				else
				{
					LogLoad("scene_change_file_begin");
					changeError = GetTree().ChangeSceneToFile(path);
				}
			}
			LogLoad("scene_change_result", changeError.ToString());
			if (changeError != Error.Ok)
			{
				ShowLoadFailure(path, $"Scene could not be instantiated: {changeError}");
				return;
			}
			FinishSceneChange();
		}
		catch (Exception exception)
		{
			ShowLoadFailure(path, $"{exception.GetType().Name}: {exception.Message}");
		}
	}

	private void FinishSceneChange()
	{
		// Reset time scale and unpause whenever we change scenes
		Engine.TimeScale = 1f;
		GetTree().Paused = false;

		QueuedScene = string.Empty; // Clear queue
		LogLoad("scene_change_applied");
		EmitSignal(SignalName.SceneChanged);

		if (!CurrentTransitionData.disableAutoTransition)
			FinishFade();
	}

	private void ShowLoadFailure(string path, string reason)
	{
		LogLoad("scene_failed", reason);
		GD.PushError($"Could not load scene '{path}': {reason}");
		failedScene = path;
		failedTransition = CurrentTransitionData;
		QueuedScene = string.Empty;
		IsReloadingScene = false;
		Engine.TimeScale = 1f;
		GetTree().Paused = false;
		animator.Play("RESET");
		animator.Advance(0);
		loadingAnimator.Play("RESET");
		loadingAnimator.Advance(0);
		missionDescriptionRoot.Hide();
		UpdateLoadingText(null);
		CurrentTransitionData = new(); // This is a failure prompt, no longer an active resource load.
		IsTransitionActive = true; // Keep underlying menus from receiving the prompt's input.
		CreateFailurePrompt();
		failureRoot.Show();
		retryButton.Disabled = string.IsNullOrEmpty(path);
		UpdateFailureLayout();
		if (retryButton.Disabled)
			menuButton.GrabFocus();
		else
			retryButton.GrabFocus();
	}

	private void CreateFailurePrompt()
	{
		if (failureRoot != null)
			return;
		failureRoot = new ColorRect { Name = "LoadFailure", Color = new Color(.035f, .035f, .05f), MouseFilter = Control.MouseFilterEnum.Stop };
		AddChild(failureRoot);
		failureRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		failurePanel = new PanelContainer { Name = "Panel" };
		failureRoot.AddChild(failurePanel);
		MarginContainer margins = new();
		foreach (string side in new[] { "left", "top", "right", "bottom" })
			margins.AddThemeConstantOverride("margin_" + side, 40);
		failurePanel.AddChild(margins);
		VBoxContainer column = new();
		column.AddThemeConstantOverride("separation", 28);
		margins.AddChild(column);
		Label title = new() { Text = "Unable to load", HorizontalAlignment = HorizontalAlignment.Center };
		title.AddThemeFontSizeOverride("font_size", 46);
		column.AddChild(title);
		Label message = new()
		{
			Text = "Try again, or return to the main menu.",
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		message.AddThemeFontSizeOverride("font_size", 32);
		column.AddChild(message);
		HBoxContainer choices = new() { Alignment = BoxContainer.AlignmentMode.Center };
		choices.AddThemeConstantOverride("separation", 28);
		column.AddChild(choices);
		retryButton = new Button { Name = "Retry", Text = "Retry", CustomMinimumSize = new Vector2(360, 88) };
		menuButton = new Button { Name = "MainMenu", Text = "Main menu", CustomMinimumSize = new Vector2(360, 88) };
		foreach (Button button in new[] { retryButton, menuButton })
		{
			button.AddThemeFontSizeOverride("font_size", 38);
			choices.AddChild(button);
		}
		retryButton.Pressed += RetryFailedScene;
		menuButton.Pressed += ReturnToMenuAfterFailure;
		GetViewport().SizeChanged += UpdateFailureLayout;
	}

	private void UpdateFailureLayout()
	{
		if (!IsLoadFailureVisible)
			return;
		Rect2 safe = MobileScreenLayout.GetSafeRect(GetViewport());
		failurePanel.Size = new Vector2(1000, 370);
		float scale = Mathf.Min(1, Mathf.Min(safe.Size.X / 1100, safe.Size.Y / 470));
		failurePanel.Scale = Vector2.One * scale;
		failurePanel.Position = safe.GetCenter() - failurePanel.Size * scale * .5f;
	}

	/// <summary> Used by both native touch and desktop/controller input. </summary>
	public Vector2 GetLoadFailureTouchPosition(bool retry) =>
		IsLoadFailureVisible ? (retry ? retryButton : menuButton).GetGlobalRect().GetCenter() : -Vector2.One;

	public override void _Input(InputEvent inputEvent)
	{
		if (!IsLoadFailureVisible)
			return;
		bool pointerPressed = inputEvent is InputEventScreenTouch { Pressed: true, Canceled: false } ||
			inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
		if (pointerPressed)
		{
			Vector2 position = inputEvent is InputEventScreenTouch touch ? touch.Position : ((InputEventMouseButton)inputEvent).Position;
			GetViewport().SetInputAsHandled();
			if (!retryButton.Disabled && retryButton.GetGlobalRect().HasPoint(position))
				RetryFailedScene();
			else if (menuButton.GetGlobalRect().HasPoint(position))
				ReturnToMenuAfterFailure();
			return;
		}
		if (inputEvent.IsActionPressed("ui_left"))
			retryButton.GrabFocus();
		else if (inputEvent.IsActionPressed("ui_right"))
			menuButton.GrabFocus();
		else if (inputEvent.IsActionPressed("sys_cancel") || inputEvent.IsActionPressed("ui_cancel"))
			ReturnToMenuAfterFailure();
		else if (inputEvent.IsActionPressed("sys_select") || inputEvent.IsActionPressed("ui_accept"))
		{
			if (menuButton.HasFocus())
				ReturnToMenuAfterFailure();
			else if (!retryButton.Disabled)
				RetryFailedScene();
		}
		else
			return;
		GetViewport().SetInputAsHandled();
	}

	public void RetryFailedScene()
	{
		if (!IsLoadFailureVisible || string.IsNullOrEmpty(failedScene))
			return;
		LogLoad("retry_selected");
		failureRoot.Hide();
		IsTransitionActive = false;
		QueueSceneChange(failedScene);
		StartTransition(failedTransition);
	}

	public void ReturnToMenuAfterFailure()
	{
		if (!IsLoadFailureVisible)
			return;
		LogLoad("menu_selected");
		failureRoot.Hide();
		IsTransitionActive = false;
		Menu.menuMemory[Menu.MemoryKeys.ActiveMenu] = (int)Menu.MemoryKeys.MainMenu;
		QueueSceneChange(MenuScenePath);
		StartTransition(new() { inSpeed = .1f, outSpeed = .25f, color = Colors.Black });
	}

	public static string LoadDiagnosticPath => SaveManager.DataDirectory.PathJoin("diagnostics/scene-loading.log");
	private void LogLoad(string phase, string detail = "")
	{
		// Keep diagnostics separate from saves. Only phase/status changes are written;
		// a stalled native call leaves its matching *_begin entry as the final record.
		try
		{
			string path = LoadDiagnosticPath;
			if (DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir()) != Error.Ok)
				return;
			using FileAccess file = FileAccess.Open(path, FileAccess.FileExists(path) ? FileAccess.ModeFlags.ReadWrite : FileAccess.ModeFlags.Write);
			if (file == null)
				return;
			string entry = $"{Time.GetDatetimeStringFromSystem(true)}Z ticks={Time.GetTicksMsec()} phase={phase} path={diagnosticScene} {detail}";
			if (file.GetLength() > 262144)
			{
				file.Close();
				using FileAccess reset = FileAccess.Open(path, FileAccess.ModeFlags.Write);
				reset?.StoreLine(entry);
			}
			else
			{
				file.SeekEnd();
				file.StoreLine(entry);
			}
		}
		catch (Exception)
		{
			// Diagnostic storage must never interrupt loading or recovery.
		}
	}

	private readonly string commonResourcesScenePath = "res://object/CommonResources.tscn";
	private bool isLoadingCommonResources;
	/// <summary>
	/// Attempts to reduce load times by loading common objects before-hand. Called on boot.
	/// </summary>
	public async void LoadCommonResources()
	{
		LogLoad("common_request_begin", commonResourcesScenePath);
		Error err = ResourceLoader.LoadThreadedRequest(commonResourcesScenePath);
		LogLoad("common_request_result", err.ToString());
		if (err != Error.Ok)
		{
			GD.PrintErr("Couldn't load common resources!");
			return;
		}

		GD.Print("Loading Common Resources.");
		isLoadingCommonResources = true;

		ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(commonResourcesScenePath);
		while (status == ResourceLoader.ThreadLoadStatus.InProgress) // Still loading
		{
			await ToSignal(GetTree().CreateTimer(.1f), SceneTreeTimer.SignalName.Timeout); // Wait a bit
			status = ResourceLoader.LoadThreadedGetStatus(commonResourcesScenePath);
		}

		LogLoad("common_finished", status.ToString());
		GD.Print("Common Resources: ", status);
		isLoadingCommonResources = false;
	}

	public void UpdateLoadingText(StringName localizationKey, int currentProgress = 0, int maxProgress = 0)
	{
		if (localizationKey != lastLoadingText)
		{
			lastLoadingText = localizationKey;
			LogLoad("loading_text", localizationKey?.ToString() ?? "cleared");
		}
		if (localizationKey == null)
		{
			loadLabel.Text = string.Empty;
			return;
		}

		loadLabel.Text = Tr(localizationKey);
		if (maxProgress != 0)
			loadLabel.Text += $" {currentProgress}/{maxProgress}";
	}

	public void SetMissionDescriptionText(StringName typeKey, StringName descriptionKey)
	{
		string missionText = Tr(descriptionKey);
		if (SaveManager.Config.textLocale.LocaleId != "ja")
			missionText = missionText.Replace('\n', ' ');
		else
			missionText = missionText.Replace("\n", "");

		missionDescriptionLabel.Text = $"{Tr(typeKey)}: {missionText}";
	}
}

public struct TransitionData
{
	// Keep both speeds at 0 to perform simple cut transitions
	public float inSpeed;
	public float outSpeed;
	public Color color;
	public bool loadAsynchronously;
	public bool disableAutoTransition;
	public bool showMissionDescription;
}
