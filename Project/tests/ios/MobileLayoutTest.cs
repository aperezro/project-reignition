using Godot;
using Project.Core;
using Project.Interface;
using Project.Interface.Menus;
using System;
using System.Threading.Tasks;

namespace Project.Tests.Ios;

/// <summary> Renders the actual mobile menus at both target device shapes. </summary>
public partial class MobileLayoutTest : Node
{
	private int checks;
	private readonly string output = ProjectSettings.GlobalizePath("res://../build/ios/layout");

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
			DirAccess.MakeDirRecursiveAbsolute(output);
			Check(SaveManager.DataDirectory.Contains("ios-smoke-test"), "isolated saves");
			Check(RenderingServer.GetCurrentRenderingMethod() == "mobile", "actual Mobile renderer");
			SaveManager.ActiveSaveSlotIndex = -1;
			SaveManager.MenuData = SaveManager.GameData.CreateDefaultData();
			GetTree().CurrentScene = null;
			foreach (var device in new[] { ("iphone", new Vector2I(1302, 600)), ("ipad", new Vector2I(1024, 768)) })
			{
				GetTree().Root.Size = device.Item2;
				SaveManager.ApplyConfig();
				await Frames(5);
				Vector2 canvas = GetViewport().GetVisibleRect().Size;
				Vector2 physical = GetTree().Root.Size;
				Check(Mathf.Abs(canvas.X / canvas.Y - physical.X / physical.Y) < .002f, device.Item1 + " full-screen viewport aspect");
				Transform2D screen = GetViewport().GetScreenTransform();
				Check((screen * Vector2.Zero).Length() < 1, device.Item1 + " no top or left letterbox");
				Check((screen * canvas).DistanceTo(physical) < 2, device.Item1 + " no bottom or right letterbox");
				Check(Mathf.Abs(screen.X.Length() - screen.Y.Length()) < .001f, device.Item1 + " canvas uses uniform scale");
				Menu.menuMemory[Menu.MemoryKeys.ActiveMenu] = (int)Menu.MemoryKeys.MainMenu;
				Check(GetTree().ChangeSceneToFile(TransitionManager.MenuScenePath) == Error.Ok, "real menu requested");
				await Until(() => GetTree().CurrentScene?.HasNode("Clip/MainMenu") == true, "menu loaded");
				MainMenu menu = GetTree().CurrentScene.GetNode<MainMenu>("Clip/MainMenu");
				await Until(() => menu.Get("isProcessing").AsBool(), "main menu entrance complete");
				await Frames(60);
				Control foreground = GetTree().CurrentScene.GetNode<Control>("Clip");
				Control background = GetTree().CurrentScene.GetNode<Control>("FullScreenBackground");
				Check(foreground.Size.IsEqualApprox(MobileScreenLayout.AuthoredSize), device.Item1 + " original menu composition retained");
				Rect2 frame = foreground.GetGlobalRect();
				Rect2 safe = MobileScreenLayout.GetSafeRect(GetViewport());
				Check(frame.GetCenter().DistanceTo(safe.GetCenter()) < 1, device.Item1 + " menu centered in safe canvas");
				Check(safe.Encloses(frame), device.Item1 + " menu entirely inside safe area");
				Check(background.Size.DistanceTo(canvas) < 1, device.Item1 + " decorative backdrop covers whole screen");
				await Screenshot(device.Item1 + "-main-menu.png");
				menu.DisableProcessing();
				menu.HideMenu();
				var save = GetTree().CurrentScene.GetNode<SaveSelect>("Clip/SaveSelect");
				save.ShowMenu();
				await Until(() => save.Get("isProcessing").AsBool(), "save selection entrance complete");
				await Frames(45);
				await Screenshot(device.Item1 + "-save-menu.png");
			}
			// A notched landscape viewport and home indicator must still contain the complete menu.
			Rect2 notched = new(new Vector2(100, 0), new Vector2(2144, 1025));
			Rect2 fitted = MobileScreenLayout.FitAuthoredCanvas(notched);
			Check(notched.Encloses(fitted), "notched safe area contains all menu content");
			Check(fitted.GetCenter().IsEqualApprox(notched.GetCenter()), "notched menu centered");
			Check(Mathf.IsEqualApprox(fitted.Size.X / fitted.Size.Y, 16f / 9), "notched menu proportions retained");
			Check(Mathf.IsEqualApprox(MobileScreenLayout.ExpandVerticalFov(70, new Vector2(2344, 1080)), 70), "phone preserves vertical camera view");
			float ipadFov = MobileScreenLayout.ExpandVerticalFov(70, new Vector2(1920, 1440));
			Check(ipadFov > 70 && ipadFov < 90, "iPad reveals more vertically instead of cropping horizontal camera view");
			using FileAccess report = FileAccess.Open(output.PathJoin("result.txt"), FileAccess.ModeFlags.Write);
			report.StoreString($"PASS: {checks} mobile layout checks\n");
			GD.Print("IOS_LAYOUT_PASS: ", checks, " checks");
			GetTree().Quit();
		}
		catch (Exception exception)
		{
			GD.PushError("IOS_LAYOUT_FAIL: " + exception);
			GetTree().Quit(1);
		}
	}

	private async Task Frames(int count)
	{
		for (int i = 0; i < count; i++)
			await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
	}

	private async Task Until(Func<bool> condition, string description)
	{
		ulong deadline = Time.GetTicksMsec() + 20000;
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
		Check(image.SavePng(output.PathJoin(filename)) == Error.Ok, "screenshot " + filename);
	}

	private void Check(bool condition, string description)
	{
		if (!condition) throw new InvalidOperationException(description);
		checks++;
		GD.Print("IOS_LAYOUT_CHECK: ", description);
	}
}
