using Godot;

namespace Project.Interface;

/// <summary> Converts the device safe area into the expanded, undistorted UI canvas. </summary>
public static class MobileScreenLayout
{
	public static readonly Vector2 AuthoredSize = new(1920, 1080);

	public static Rect2 GetSafeRect(Viewport viewport)
	{
		Rect2 bounds = viewport.GetVisibleRect();
		if (OS.GetName() != "iOS" && OS.GetName() != "Android")
			return bounds;
		Rect2I physical = DisplayServer.GetDisplaySafeArea();
		if (physical.Size.X <= 0 || physical.Size.Y <= 0)
			return bounds;
		Transform2D inverse = viewport.GetScreenTransform().AffineInverse();
		Vector2 start = inverse * (Vector2)physical.Position;
		Vector2 end = inverse * (Vector2)physical.End;
		Rect2 safe = new Rect2(start, end - start).Intersection(bounds);
		return safe.HasArea() ? safe : bounds;
	}

	public static Rect2 FitAuthoredCanvas(Rect2 safe)
	{
		float scale = Mathf.Min(safe.Size.X / AuthoredSize.X, safe.Size.Y / AuthoredSize.Y);
		Vector2 size = AuthoredSize * scale;
		return new Rect2(safe.GetCenter() - size * .5f, size);
	}

	public static float ExpandVerticalFov(float authoredFov, Vector2 viewportSize)
	{
		float aspect = viewportSize.X / Mathf.Max(1, viewportSize.Y);
		float expansion = Mathf.Max(1, (AuthoredSize.X / AuthoredSize.Y) / aspect);
		return Mathf.RadToDeg(2 * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(authoredFov) * .5f) * expansion));
	}
}
