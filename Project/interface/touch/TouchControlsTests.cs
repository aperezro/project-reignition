using Godot;
using System;

namespace Project.Interface.Touch;

/// <summary>Run with -- --touch-controls-self-test. Uses Godot's real Input action state.</summary>
internal static class TouchControlsTests
{
	internal static void Run(TouchControls controls)
	{
		int assertions = 0;
		void Check(bool condition, string message)
		{
			assertions++;
			if (!condition)
				throw new InvalidOperationException(message);
		}
		bool Pressed(string action) => Input.IsActionPressed(action);
		Rect2 safe = new(24, 24, 1872, 1032);
		try
		{
			controls.Configure(TouchControls.ControlContext.Gameplay, safe);
			foreach (TouchControls.TouchButton button in controls.Buttons)
			{
				Check(safe.Encloses(new Rect2(button.Center - Vector2.One * button.Radius,
					Vector2.One * button.Radius * 2)), $"Button outside safe area: {button.Id}");
				Check(controls.BeginTouch(0, button.Center), $"Unreachable button: {button.Id}");
				foreach (string action in button.Actions)
					Check(Pressed(action), $"Missing press: {action}");
				// Retain the initial button while a thumb drifts outside its hit area.
				controls.MoveTouch(0, new Vector2(-100, -100));
				foreach (string action in button.Actions)
					Check(Pressed(action), $"Button was released while finger remained down: {action}");
				controls.EndTouch(0);
				foreach (string action in button.Actions)
					Check(!Pressed(action), $"Stuck action: {action}");
			}

			Vector2 jump = controls.GetActionTouchPosition("button_jump");
			Vector2 move = controls.MoveCenter;
			controls.BeginTouch(1, move);
			controls.MoveTouch(1, move + new Vector2(0.5f, -0.5f) * controls.MoveRadius);
			Check(Mathf.IsEqualApprox(Input.GetActionStrength("move_right"), 0.5f), "Movement lost analog strength");
			Check(Mathf.IsEqualApprox(Input.GetActionStrength("move_up"), 0.5f), "Movement lost its vertical component");
			controls.BeginTouch(2, jump);
			controls.BeginTouch(3, jump);
			controls.EndTouch(2);
			Check(Pressed("button_jump"), "Releasing one finger released a button held by another");
			Check(Pressed("move_up"), "Jump interfered with movement");
			controls.EndTouch(3);
			Check(!Pressed("button_jump") && Pressed("move_up"), "Button release interfered with movement");
			controls.MoveTouch(1, move + new Vector2(1000, -1000));
			Check(Input.GetVector("move_left", "move_right", "move_up", "move_down").Length() <= 1.0001f,
				"Diagonal movement exceeds full speed");
			controls.EndTouch(1);
			Check(!Pressed("move_up") && !Pressed("move_right"), "Movement did not stop on release");

			Vector2 look = new(1200, 350);
			controls.BeginTouch(4, look);
			controls.MoveTouch(4, look + new Vector2(100, 0));
			Check(Pressed("camera_right") && !Pressed("move_right"), "Camera drag controls movement");
			controls.BeginTouch(5, jump);
			controls.Configure(TouchControls.ControlContext.Menu, safe);
			Check(!Pressed("camera_right") && !Pressed("button_jump"), "Context transition left held actions");
			Check(!controls.MoveTouch(5, jump), "A stale finger survived a context transition");

			controls.BeginTouch(6, controls.GetActionTouchPosition("sys_select"));
			Check(Pressed("sys_select") && Pressed("ui_select") && !Pressed("sys_pause"),
				"Menu confirm accidentally triggers Start/Pause");
			controls.EndTouch(6);
			controls.BeginTouch(7, controls.MoveCenter);
			controls.MoveTouch(7, controls.MoveCenter + new Vector2(0.4f, -0.9f) * controls.MoveRadius);
			Check(Pressed("ui_up") && !Pressed("ui_right") && !Pressed("move_up"),
				"Menu stick should select one direction without moving the adventure player");
			controls.EndTouch(7);
			controls.BeginTouch(8, controls.GetActionTouchPosition("sys_pause"));
			Check(Pressed("sys_pause") && !Pressed("sys_select"), "Start must be separate from Confirm");
			controls._Notification((int)Node.NotificationApplicationFocusOut);
			Check(!Pressed("sys_pause"), "Backgrounding left Pause pressed");
			Check(!controls.BeginTouch(9, controls.MoveCenter), "Input accepted while unfocused");
			controls._Notification((int)Node.NotificationApplicationFocusIn);
			controls._Notification((int)Node.NotificationApplicationPaused);
			Check(!controls.BeginTouch(9, controls.MoveCenter), "Input accepted while suspended");
			controls._Notification((int)Node.NotificationApplicationResumed);
			controls.GetTree().Paused = true;
			controls.Configure(TouchControls.ControlContext.Menu, safe);
			Check(controls.MoveCenter.Y + controls.MoveRadius < safe.GetCenter().Y,
				"Paused navigation stick covers the lower-left menu options");
			Check(safe.Encloses(new Rect2(controls.MoveCenter - Vector2.One * controls.MoveRadius,
				Vector2.One * controls.MoveRadius * 2)), "Paused navigation stick leaves the safe area");
			controls.GetTree().Paused = false;

			controls.Configure(TouchControls.ControlContext.Party, safe);
			controls.BeginTouch(10, controls.GetActionTouchPosition("button_primary1"));
			Check(Pressed("button_primary1") && !Pressed("button_jump"), "Party input mapped to adventure controls");
			controls.ReleaseAll();
			controls.Configure(TouchControls.ControlContext.Hidden, safe);
			Check(!controls.BeginTouch(11, jump), "Hidden controls accepted touches");

			// iPad: 1920x1080 content in a 2048x1536 display with 192px letterbox bars.
			Transform2D ipadTransform = new(new Vector2(2048f / 1920f, 0), new Vector2(0, 2048f / 1920f), new Vector2(0, 192));
			Rect2 ipad = TouchControls.ConvertSafeRect(new Rect2(0, 24, 2048, 1492), ipadTransform, new Rect2(0, 0, 1920, 1080));
			Check(ipad.Position.IsEqualApprox(new Vector2(18, 18)) && ipad.Size.IsEqualApprox(new Vector2(1884, 1044)),
				"iPad letterboxing incorrectly reduced or shifted the touch area");
			// iPhone landscape notch and home-indicator insets are inside viewport bounds.
			Transform2D iphoneTransform = new(new Vector2(1.4f, 0), new Vector2(0, 1.4f), Vector2.Zero);
			Rect2 iphone = TouchControls.ConvertSafeRect(new Rect2(88, 0, 2512, 1478), iphoneTransform, new Rect2(0, 0, 1920, 1080));
			Check(iphone.Position.X > 70 && iphone.End.X < 1850 && iphone.End.Y < 1040,
				"iPhone safe-area insets were ignored");
			controls.Configure(TouchControls.ControlContext.Gameplay, iphone);
			foreach (TouchControls.TouchButton button in controls.Buttons)
				Check(iphone.Encloses(new Rect2(button.Center - Vector2.One * button.Radius,
					Vector2.One * button.Radius * 2)), $"iPhone button outside safe area: {button.Id}");

			// Expand fills wide iPhones and taller iPads while preserving the authored scale.
			foreach (var display in new[]
			{
				("fullscreen iPhone", new Vector2(2622, 1206), new Vector2(2622f / 1206f * 1080f, 1080), new Rect2(100, 0, 2422, 1154)),
				("fullscreen iPad", new Vector2(2048, 1536), new Vector2(1920, 1440), new Rect2(0, 24, 2048, 1492))
			})
			{
				Vector2 ratio = display.Item2 / display.Item3;
				Rect2 expandedSafe = TouchControls.ConvertSafeRect(display.Item4,
					new Transform2D(new Vector2(ratio.X, 0), new Vector2(0, ratio.Y), Vector2.Zero),
					new Rect2(Vector2.Zero, display.Item3));
				foreach (var context in new[] { TouchControls.ControlContext.Gameplay, TouchControls.ControlContext.Menu })
				{
					controls.Configure(context, expandedSafe);
					foreach (TouchControls.TouchButton button in controls.Buttons)
						Check(expandedSafe.Encloses(new Rect2(button.Center - Vector2.One * button.Radius,
							Vector2.One * button.Radius * 2)), $"{display.Item1} {context} button outside safe area: {button.Id}");
					Check(expandedSafe.Encloses(new Rect2(controls.MoveCenter - Vector2.One * controls.MoveRadius,
						Vector2.One * controls.MoveRadius * 2)), $"{display.Item1} joystick outside safe area");
				}
				controls.BeginTouch(12, controls.GetActionTouchPosition("sys_select"));
				Check(Pressed("sys_select") && Pressed("ui_select"), $"{display.Item1} Confirm cannot be pressed");
				controls.EndTouch(12);
			}
			GD.Print($"TOUCH SELF-TEST PASS ({assertions} assertions): actions, multitouch, analog movement, camera, menus, party, lifecycle, iPhone and iPad layout.");
			controls.GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError($"TOUCH SELF-TEST FAILED after {assertions} assertions: {exception}");
			controls.GetTree().Quit(1);
		}
		finally
		{
			controls.ReleaseAll();
		}
	}
}
