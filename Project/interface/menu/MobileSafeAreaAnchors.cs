using Godot;
using Project.Core;
using System.Collections.Generic;

namespace Project.Interface;

/// <summary> Insets edge-anchored HUD elements without changing their animation offsets. </summary>
public partial class MobileSafeAreaAnchors : Node
{
	private readonly Dictionary<Control, Vector4> anchors = new();
	private Rect2 lastSafeRect;
	private Control root;

	public override void _Ready()
	{
		if (!SaveManager.IsMobilePlatform)
		{
			SetProcess(false);
			return;
		}
		root = GetParent<Control>();
		foreach (Node child in root.GetChildren())
			if (child is Control control)
				anchors[control] = new Vector4(control.AnchorLeft, control.AnchorTop, control.AnchorRight, control.AnchorBottom);
		UpdateAnchors();
	}

	public override void _Process(double delta)
	{
		if (root != null && MobileScreenLayout.GetSafeRect(GetViewport()) != lastSafeRect)
			UpdateAnchors();
	}

	private void UpdateAnchors()
	{
		lastSafeRect = MobileScreenLayout.GetSafeRect(GetViewport());
		Vector2 size = GetViewport().GetVisibleRect().Size;
		foreach (var item in anchors)
		{
			Control control = item.Key;
			Vector4 original = item.Value;
			control.SetAnchor(Side.Left, (lastSafeRect.Position.X + original.X * lastSafeRect.Size.X) / size.X, true, false);
			control.SetAnchor(Side.Top, (lastSafeRect.Position.Y + original.Y * lastSafeRect.Size.Y) / size.Y, true, false);
			control.SetAnchor(Side.Right, (lastSafeRect.Position.X + original.Z * lastSafeRect.Size.X) / size.X, true, false);
			control.SetAnchor(Side.Bottom, (lastSafeRect.Position.Y + original.W * lastSafeRect.Size.Y) / size.Y, true, false);
		}
	}
}
