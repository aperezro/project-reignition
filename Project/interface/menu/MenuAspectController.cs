using Godot;
using Project.Core;
using System.Collections.Generic;

namespace Project.Interface;

/// <summary> Centers authored menu artwork independently of the full-screen game viewport. </summary>
public partial class MenuAspectController : Control
{
	[Export] public bool FitMenuContent = true;
	private readonly List<Control> content = new();
	private TextureRect backdrop;
	private Control decoration;
	private Rect2 lastSafeRect;

	public override void _Ready()
	{
		foreach (Node child in GetChildren())
			if (child is Control control)
				content.Add(control);

		if (SaveManager.IsMobilePlatform && FitMenuContent && content.Count > 0 &&
			content[0].GetNodeOrNull<TextureRect>("Background") is TextureRect background)
		{
			// Backgrounds fill each device shape; foreground menus retain their composition.
			backdrop = new TextureRect
			{
				Name = "FullScreenBackground",
				Texture = background.Texture,
				ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
				StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
				MouseFilter = MouseFilterEnum.Ignore,
			};
			AddChild(backdrop);
			MoveChild(backdrop, 0);
			background.Hide();
			decoration = new Control { Name = "FullScreenDecoration", MouseFilter = MouseFilterEnum.Ignore };
			AddChild(decoration);
			MoveChild(decoration, 1);
			// These two layers are decorative, with no interactive elements. Extend
			// them beyond the fitted menu so the parchment edge and flames remain
			// at the screen edge instead of outlining a smaller rectangle.
			foreach (string name in new[] { "Char", "Flames" })
				content[0].GetNodeOrNull<Control>(name)?.Reparent(decoration, false);
		}

		Resized += UpdateLayout;
		UpdateLayout();
	}

	public override void _Process(double delta)
	{
		// Landscape can flip safe insets without changing the window dimensions.
		if (SaveManager.IsMobilePlatform && MobileScreenLayout.GetSafeRect(GetViewport()) != lastSafeRect)
			UpdateLayout();
	}

	private void UpdateLayout()
	{
		lastSafeRect = MobileScreenLayout.GetSafeRect(GetViewport());
		if (backdrop != null)
			backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		if (decoration != null)
		{
			float coverScale = Mathf.Max(Size.X / MobileScreenLayout.AuthoredSize.X, Size.Y / MobileScreenLayout.AuthoredSize.Y);
			decoration.Size = MobileScreenLayout.AuthoredSize;
			decoration.Scale = Vector2.One * coverScale;
			decoration.Position = (Size - decoration.Size * coverScale) * .5f;
		}
		foreach (Control control in content)
		{
			control.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
			if (SaveManager.IsMobilePlatform && FitMenuContent)
			{
				Rect2 frame = MobileScreenLayout.FitAuthoredCanvas(lastSafeRect);
				control.Size = MobileScreenLayout.AuthoredSize;
				control.Scale = Vector2.One * (frame.Size.X / MobileScreenLayout.AuthoredSize.X);
				control.Position = frame.Position;
			}
			else
			{
				control.Scale = Vector2.One;
				control.Position = Vector2.Zero;
				control.Size = Size;
			}
		}
	}
}
