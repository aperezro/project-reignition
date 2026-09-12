using Godot;
using Project.Core;
using Project.Gameplay;
using System;
using System.Collections.Generic;

namespace Project.Interface.Touch;

/// <summary>
/// A persistent multitouch controller that feeds the same actions as a gamepad.
/// Each finger owns its initial control until lifted, including outside its bounds.
/// </summary>
public partial class TouchControls : Control
{
	public static bool IsTouchActive { get; private set; }
	internal enum ControlContext { Hidden, Menu, Gameplay, Party, Cutscene }
	internal sealed class TouchButton(string id, string label, Vector2 center, float radius, Color color, params string[] actions)
	{
		public readonly string Id = id;
		public readonly string Label = label;
		public readonly Vector2 Center = center;
		public readonly float Radius = radius;
		public readonly Color Color = color;
		public readonly string[] Actions = actions;
		public bool Contains(Vector2 position) => position.DistanceSquaredTo(Center) <= Radius * Radius;
	}

	private enum PointerKind { Move, Camera, Button }
	private sealed class Pointer(PointerKind kind, Vector2 origin, TouchButton button = null)
	{
		public readonly PointerKind Kind = kind;
		public readonly Vector2 Origin = origin;
		public readonly TouchButton Button = button;
		public Vector2 Position = origin;
	}

	private static readonly Color Blue = new("65cfff");
	private static readonly Color Gold = new("ffd47b");
	private static readonly Color White = new("e7f1ff");
	private readonly Dictionary<int, Pointer> pointers = new();
	private readonly Dictionary<string, float> ownedActions = new();
	private readonly Dictionary<string, float> nextActions = new();
	private readonly Dictionary<StringName, List<InputEvent>> removedMouseBindings = new();
	internal readonly List<TouchButton> Buttons = new();
	internal Vector2 MoveCenter { get; private set; }
	internal float MoveRadius { get; private set; }
	internal Rect2 SafeRect { get; private set; }
	internal ControlContext Context { get; private set; }
	public Vector2 GetMovementTouchCenter() => MoveCenter;
	public float GetMovementTouchRadius() => MoveRadius;
	public Vector2 GetActionTouchPosition(StringName action)
	{
		foreach (TouchButton button in Buttons)
			if (Array.Exists(button.Actions, name => name == action.ToString()))
				return button.Center;
		return new Vector2(-1, -1);
	}
	private float scale = 1f;
	private bool enabled;
	private bool mobile;
	private bool focused = true;
	private bool applicationPaused;
	private bool testing;
	private bool savedMouseEmulation;
	private bool savedTouchEmulation;
	private ulong sceneId;
	private int menuId = -1;
	private Node partyPauseManager;
	private Font font;

	public override void _Ready()
	{
		mobile = OS.HasFeature("ios") || OS.HasFeature("android");
		testing = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--touch-controls-self-test");
		enabled = !Engine.IsEditorHint() && (mobile || testing ||
			Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--touch-controls"));
		MouseFilter = MouseFilterEnum.Ignore;
		FocusMode = FocusModeEnum.None;
		ProcessMode = ProcessModeEnum.Always;
		ProcessPriority = -100;
		ProcessPhysicsPriority = -100;
		Visible = enabled;
		SetProcess(enabled);
		SetPhysicsProcess(enabled);
		SetProcessInput(enabled);
		if (!enabled)
			return;

		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		font = ThemeDB.FallbackFont;
		savedMouseEmulation = Input.EmulateMouseFromTouch;
		savedTouchEmulation = Input.EmulateTouchFromMouse;
		Input.EmulateMouseFromTouch = false;
		// Desktop testing uses real mouse events as finger zero. Prevent the same
		// click from also firing the game's original mouse-to-jump binding.
		if (!mobile)
		{
			Input.EmulateTouchFromMouse = false;
			RemoveMouseBindings();
		}

		partyPauseManager = GetNodeOrNull<Node>("/root/PauseManager");
		if (IsInstanceValid(TransitionManager.Instance))
		{
			TransitionManager.Instance.TransitionStarted += ReleaseAll;
			TransitionManager.Instance.SceneChanged += ReleaseAll;
		}
		RefreshContext();
		GD.Print("Touch controls enabled: left stick to move, right side to look, labeled buttons for actions.");
		if (testing)
			Callable.From(() => TouchControlsTests.Run(this)).CallDeferred();
	}

	public override void _ExitTree()
	{
		if (!enabled)
			return;
		ReleaseAll();
		IsTouchActive = false;
		Input.EmulateMouseFromTouch = savedMouseEmulation;
		Input.EmulateTouchFromMouse = savedTouchEmulation;
		foreach (var (action, events) in removedMouseBindings)
			foreach (InputEvent inputEvent in events)
				if (InputMap.HasAction(action) && !InputMap.ActionHasEvent(action, inputEvent))
					InputMap.ActionAddEvent(action, inputEvent);
		if (IsInstanceValid(TransitionManager.Instance))
		{
			TransitionManager.Instance.TransitionStarted -= ReleaseAll;
			TransitionManager.Instance.SceneChanged -= ReleaseAll;
		}
	}

	public override void _Notification(int what)
	{
		if (!enabled)
			return;
		switch (what)
		{
			case (int)NotificationApplicationFocusOut:
			case (int)NotificationWMWindowFocusOut:
				focused = false;
				ReleaseAll();
				break;
			case (int)NotificationApplicationPaused:
				applicationPaused = true;
				ReleaseAll();
				break;
			case (int)NotificationApplicationFocusIn:
			case (int)NotificationWMWindowFocusIn:
				focused = true;
				break;
			case (int)NotificationApplicationResumed:
				applicationPaused = false;
				break;
		}
	}

	public override void _Process(double delta) => RefreshContext();
	public override void _PhysicsProcess(double delta) => RefreshContext();

	private void RefreshContext()
	{
		if (!enabled || testing)
			return;
		ulong newSceneId = IsInstanceValid(GetTree().CurrentScene) ? GetTree().CurrentScene.GetInstanceId() : 0;
		int newMenuId = Menus.Menu.menuMemory.TryGetValue(Menus.Menu.MemoryKeys.ActiveMenu, out int activeMenu) ? activeMenu : -1;
		if (sceneId != newSceneId || menuId != newMenuId)
		{
			ReleaseAll();
			sceneId = newSceneId;
			menuId = newMenuId;
		}
		ControlContext nextContext = GetContext();
		Rect2 safeRect = GetSafeRect();
		if (Context != nextContext || SafeRect != safeRect)
			Configure(nextContext, safeRect);
	}

	private ControlContext GetContext()
	{
		if (!focused || applicationPaused || TransitionManager.IsTransitionActive)
			return ControlContext.Hidden;
		if (GetTree().Paused)
			return ControlContext.Menu;
		// A finished story event remains loaded behind its unlock notifications.
		// Rewards need Confirm even while that scene still marks itself as a cutscene.
		if (IsInstanceValid(NotificationManager.Instance) && NotificationManager.Instance.IsActive)
			return ControlContext.Menu;
		if (IsInstanceValid(DebugManager.Instance) && DebugManager.Instance.IsCutsceneActive)
			return ControlContext.Cutscene;
		// The options book contains a live 3D test stage behind its pages.
		if (GetTree().CurrentScene is Menus.Options options && !options.IsTouchControlTestActive)
			return ControlContext.Menu;
		if (IsInstanceValid(StageSettings.Instance) && StageSettings.Instance.IsInsideTree())
		{
			if (StageSettings.Instance.IsLevelLoading)
				return ControlContext.Hidden;
			if (StageSettings.Instance.IsLevelIngame && IsInstanceValid(StageSettings.Player))
				return ControlContext.Gameplay;
		}
		if (IsInstanceValid(partyPauseManager) && partyPauseManager.HasMethod("is_minigame_active") &&
			partyPauseManager.Call("is_minigame_active").AsBool())
			return ControlContext.Party;
		return ControlContext.Menu;
	}

	private Rect2 GetSafeRect()
	{
		Rect2 viewportRect = GetViewportRect();
		if (!mobile)
			return viewportRect.Grow(-24f);

		// The display API uses physical screen pixels; root touch positions and
		// drawing use the stretched viewport's logical coordinates.
		Rect2I physicalSafe = DisplayServer.GetDisplaySafeArea();
		if (physicalSafe.Size.X <= 0 || physicalSafe.Size.Y <= 0)
			return viewportRect.Grow(-24f);
		return ConvertSafeRect(physicalSafe, GetViewport().GetScreenTransform(), viewportRect);
	}

	internal static Rect2 ConvertSafeRect(Rect2 physicalSafe, Transform2D screenTransform, Rect2 viewportRect)
	{
		// The transform includes Retina scale and any viewport stretch/letterboxing.
		Transform2D inverse = screenTransform.AffineInverse();
		Vector2 origin = inverse * physicalSafe.Position;
		Vector2 end = inverse * physicalSafe.End;
		Rect2 result = viewportRect.Intersection(new Rect2(origin, end - origin));
		return result.Size.X > 36f && result.Size.Y > 36f ? result.Grow(-18f) : viewportRect.Grow(-24f);
	}

	internal void Configure(ControlContext context, Rect2 safeRect)
	{
		ReleaseAll();
		Context = context;
		SafeRect = safeRect;
		Visible = context != ControlContext.Hidden;
		Buttons.Clear();
		scale = Mathf.Min(safeRect.Size.Y / 1032f, safeRect.Size.X / 1700f);
		MoveRadius = 132f * scale;
			MoveCenter = new Vector2(safeRect.Position.X + 185f * scale,
				context == ControlContext.Menu && GetTree().Paused
					? safeRect.Position.Y + 350f * scale
					: safeRect.End.Y - 200f * scale);
		if (!Visible)
			return;

		if (context == ControlContext.Gameplay)
		{
			// Leave the far-right soul gauge visible between the buttons and screen edge.
			Add("jump", "JUMP", 330, 160, 86, Blue, "button_jump");
			Add("action", "ACTION", 535, 155, 73, White, "button_action");
			Add("attack", "ATTACK", 330, 365, 68, Gold, "button_attack");
			Add("light", "LIGHT\nDASH", 510, 350, 63, White, "button_light_dash");
			Add("speed", "SPEED\nBREAK", 725, 150, 71, Gold, "button_speedbreak");
			Add("time", "TIME\nBREAK", 695, 350, 63, Blue, "button_timebreak");
			AddLeft("brake", "BRAKE", 405, 130, 65, White, "button_brake");
			AddLeft("step_left", "STEP\n<", 100, 425, 60, White, "button_step_left");
			AddLeft("step_right", "STEP\n>", 270, 425, 60, White, "button_step_right");
		}
		else if (context == ControlContext.Party)
		{
			Add("primary", "PRIMARY", 150, 170, 86, Blue, "button_primary1");
			Add("secondary", "SECONDARY", 365, 165, 77, Gold, "button_secondary1");
		}
		else if (context == ControlContext.Menu)
		{
			Add("confirm", "CONFIRM", 150, 165, 86, Blue, "sys_select", "ui_select", "button_primary1");
			Add("back", "BACK", 365, 160, 76, White, "sys_cancel", "ui_cancel", "button_secondary1");
			Add("sort", "SORT", 150, 360, 62, Gold, "sys_sort", "ui_focus_next");
			Add("clear", "CLEAR", 325, 345, 61, White, "sys_clear", "ui_text_delete");
			Add("mods", "MODS", 495, 330, 57, White, "button_attack");
			AddLeft("previous", "PREV", 100, 425, 60, White, "button_step_left");
			AddLeft("next", "NEXT", 270, 425, 60, White, "button_step_right");
		}
		else if (context == ControlContext.Cutscene)
		{
			Add("back", "BACK", 140, 155, 65, White, "sys_cancel", "ui_cancel");
		}

		string pauseLabel = context == ControlContext.Cutscene ? "HOLD\nTO SKIP" :
			context == ControlContext.Menu ? (GetTree().Paused ? "RESUME" : "START") :
			context == ControlContext.Gameplay && IsInstanceValid(StageSettings.Instance) && StageSettings.Instance.IsControlTest ? "MENU" : "PAUSE";
		Buttons.Add(new TouchButton("pause", pauseLabel,
			new Vector2(safeRect.GetCenter().X, safeRect.Position.Y + 85f * scale),
			62f * scale, White, "sys_pause", "ui_accept", "button_pause1"));
		QueueRedraw();
	}

	private void Add(string id, string label, float right, float bottom, float radius, Color color, params string[] actions) =>
		Buttons.Add(new TouchButton(id, label, SafeRect.End - new Vector2(right, bottom) * scale, radius * scale, color, actions));
	private void AddLeft(string id, string label, float left, float bottom, float radius, Color color, params string[] actions) =>
		Buttons.Add(new TouchButton(id, label,
			new Vector2(SafeRect.Position.X + left * scale, SafeRect.End.Y - bottom * scale), radius * scale, color, actions));

	public override void _Input(InputEvent inputEvent)
	{
		if (!enabled)
			return;
		if (inputEvent is InputEventKey key && key.Pressed || inputEvent is InputEventJoypadButton joy && joy.Pressed ||
			inputEvent is InputEventJoypadMotion motion && Mathf.Abs(motion.AxisValue) > 0.25f)
		{
			if (pointers.Count > 0)
				ReleaseAll();
			IsTouchActive = false;
			return;
		}
		RefreshContext();
		bool handled = false;
		switch (inputEvent)
		{
			case InputEventScreenTouch touch:
				handled = touch.Pressed && !touch.Canceled ? BeginTouch(touch.Index, touch.Position) : EndTouch(touch.Index);
				break;
			case InputEventScreenDrag drag:
				handled = MoveTouch(drag.Index, drag.Position);
				break;
			case InputEventMouseButton mouse when !mobile && mouse.ButtonIndex == MouseButton.Left:
				handled = mouse.Pressed && !mouse.Canceled ? BeginTouch(-1, mouse.Position) : EndTouch(-1);
				break;
			case InputEventMouseMotion mouse when !mobile:
				handled = MoveTouch(-1, mouse.Position);
				break;
		}
		if (handled)
			GetViewport().SetInputAsHandled();
	}

	internal bool BeginTouch(int index, Vector2 position)
	{
		if (!focused || applicationPaused || Context == ControlContext.Hidden || !SafeRect.HasPoint(position))
			return false;
		EndTouch(index);
		Pointer pointer = null;
		foreach (TouchButton button in Buttons)
		{
			if (!button.Contains(position))
				continue;
			pointer = new Pointer(PointerKind.Button, position, button);
			break;
		}
		if (pointer == null && Context != ControlContext.Cutscene &&
			position.DistanceTo(MoveCenter) <= MoveRadius * 1.4f && !HasPointer(PointerKind.Move))
			pointer = new Pointer(PointerKind.Move, MoveCenter);
		if (pointer == null && Context == ControlContext.Gameplay &&
			position.X > SafeRect.GetCenter().X && !HasPointer(PointerKind.Camera))
			pointer = new Pointer(PointerKind.Camera, position);
		if (pointer == null)
			return false;
		pointer.Position = position;
		pointers[index] = pointer;
		IsTouchActive = true;
		if (!mobile)
			Input.MouseMode = Input.MouseModeEnum.Visible;
		if (IsInstanceValid(Runtime.Instance))
			Runtime.Instance.IsUsingMouse = false;
		ApplyActions();
		return true;
	}

	internal bool MoveTouch(int index, Vector2 position)
	{
		if (!pointers.TryGetValue(index, out Pointer pointer))
			return false;
		pointer.Position = position;
		ApplyActions();
		return true;
	}

	internal bool EndTouch(int index)
	{
		if (!pointers.Remove(index))
			return false;
		ApplyActions();
		return true;
	}

	private bool HasPointer(PointerKind kind)
	{
		foreach (Pointer pointer in pointers.Values)
			if (pointer.Kind == kind)
				return true;
		return false;
	}

	private void AddAction(string action, float strength)
	{
		if (strength <= 0f)
			return;
		nextActions.TryGetValue(action, out float existing);
		nextActions[action] = Mathf.Max(existing, strength);
	}

	private void AddAxis(Vector2 vector, string left, string right, string up, string down)
	{
		AddAction(left, -vector.X);
		AddAction(right, vector.X);
		AddAction(up, -vector.Y);
		AddAction(down, vector.Y);
	}

	private void ApplyActions()
	{
		nextActions.Clear();
		foreach (Pointer pointer in pointers.Values)
		{
			if (pointer.Kind == PointerKind.Button)
			{
				foreach (string action in pointer.Button.Actions)
					AddAction(action, 1f);
				continue;
			}
			Vector2 vector = ((pointer.Position - pointer.Origin) /
				(pointer.Kind == PointerKind.Camera ? 110f * scale : MoveRadius)).LimitLength();
			if (pointer.Kind == PointerKind.Camera)
				AddAxis(vector, "camera_left", "camera_right", "camera_up", "camera_down");
			else if (Context == ControlContext.Gameplay)
				AddAxis(vector, "move_left", "move_right", "move_up", "move_down");
			else
			{
				AddAxis(vector, "move_left1", "move_right1", "move_up1", "move_down1");
				if (Context == ControlContext.Menu)
				{
					// Menus want one discrete direction so a diagonal cannot move two selections.
					Vector2 menuVector = vector.Length() < 0.28f ? Vector2.Zero :
						Mathf.Abs(vector.X) > Mathf.Abs(vector.Y) ? new Vector2(Mathf.Sign(vector.X), 0) :
						new Vector2(0, Mathf.Sign(vector.Y));
					AddAxis(menuVector, "ui_left", "ui_right", "ui_up", "ui_down");
				}
			}
		}

		foreach (string action in ownedActions.Keys)
			if (!nextActions.ContainsKey(action))
				Input.ActionRelease(action);
		foreach (var (action, strength) in nextActions)
			if (InputMap.HasAction(action) && (!ownedActions.TryGetValue(action, out float previous) || !Mathf.IsEqualApprox(previous, strength)))
				Input.ActionPress(action, strength);
		ownedActions.Clear();
		foreach (var (action, strength) in nextActions)
			if (InputMap.HasAction(action))
				ownedActions[action] = strength;
		QueueRedraw();
	}

	internal void ReleaseAll()
	{
		pointers.Clear();
		foreach (string action in ownedActions.Keys)
			Input.ActionRelease(action);
		ownedActions.Clear();
		nextActions.Clear();
		if (IsInsideTree())
			QueueRedraw();
	}

	private void RemoveMouseBindings()
	{
		foreach (StringName action in InputMap.GetActions())
		{
			foreach (InputEvent inputEvent in InputMap.ActionGetEvents(action))
			{
				if (inputEvent is not InputEventMouseButton)
					continue;
				if (!removedMouseBindings.TryGetValue(action, out List<InputEvent> events))
					removedMouseBindings[action] = events = new();
				events.Add(inputEvent);
				InputMap.ActionEraseEvent(action, inputEvent);
			}
		}
	}

	public override void _Draw()
	{
		if (!enabled || Context == ControlContext.Hidden)
			return;
		if (Context != ControlContext.Cutscene)
		{
			DrawDisc(MoveCenter, MoveRadius, Blue, HasPointer(PointerKind.Move), 0.13f);
			Vector2 knob = MoveCenter;
			foreach (Pointer pointer in pointers.Values)
				if (pointer.Kind == PointerKind.Move)
					knob += (pointer.Position - pointer.Origin).LimitLength(MoveRadius * 0.70f);
			DrawDisc(knob, 48f * scale, Blue, HasPointer(PointerKind.Move), 0.27f);
			DrawLabel(Context == ControlContext.Menu ? "NAVIGATE" : "MOVE",
				MoveCenter + new Vector2(0, MoveRadius + 37f * scale), 23, White);
			foreach (Vector2 direction in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
			{
				Vector2 point = MoveCenter + direction * MoveRadius * 0.82f;
				Vector2 tangent = direction.Orthogonal() * 10f * scale;
				DrawPolyline(new[] { point - direction * 8f * scale + tangent, point,
					point - direction * 8f * scale - tangent }, new Color(White, 0.7f), 2f * scale, true);
			}
		}
		foreach (TouchButton button in Buttons)
		{
			bool pressed = false;
			foreach (Pointer pointer in pointers.Values)
				if (pointer.Button == button)
					pressed = true;
			DrawDisc(button.Center, button.Radius, button.Color, pressed, 0.20f);
			DrawLabel(button.Label, button.Center, button.Radius > 80f * scale ? 26 : 22, button.Color);
		}
		if (Context == ControlContext.Gameplay)
		{
			bool looking = false;
			foreach (Pointer pointer in pointers.Values)
			{
				if (pointer.Kind != PointerKind.Camera)
					continue;
				looking = true;
				DrawDisc(pointer.Origin, 75f * scale, White, true, 0.08f);
				DrawDisc(pointer.Origin + (pointer.Position - pointer.Origin).LimitLength(75f * scale),
					28f * scale, White, true, 0.18f);
			}
			if (!looking)
				DrawLabel("DRAG TO LOOK", SafeRect.End - new Vector2(230f, 575f) * scale, 20, new Color(White, 0.6f));
		}
	}

	private void DrawDisc(Vector2 center, float radius, Color color, bool pressed, float opacity)
	{
		DrawCircle(center, radius, new Color(0.02f, 0.045f, 0.085f, pressed ? 0.62f : 0.38f));
		DrawCircle(center, radius, new Color(color, pressed ? 0.38f : opacity));
		DrawArc(center, radius, 0, Mathf.Tau, 64, new Color(color, pressed ? 1f : 0.64f), (pressed ? 4f : 2f) * scale, true);
	}

	private void DrawLabel(string text, Vector2 center, int fontSize, Color color)
	{
		int size = Mathf.Max(12, Mathf.RoundToInt(fontSize * scale));
		string[] lines = text.Split('\n');
		float lineHeight = size * 1.16f;
		for (int i = 0; i < lines.Length; i++)
		{
			float width = font.GetStringSize(lines[i], HorizontalAlignment.Left, -1, size).X;
			Vector2 position = center + new Vector2(-width * 0.5f, (i - (lines.Length - 1) * 0.5f) * lineHeight + size * 0.35f);
			DrawStringOutline(font, position, lines[i], HorizontalAlignment.Left, -1, size, 4, new Color(0f, 0f, 0f, 0.7f));
			DrawString(font, position, lines[i], HorizontalAlignment.Left, -1, size, color);
		}
	}
}
