using Godot;
using Project.Core;

namespace Project.Interface.Menus;

[Tool]
public partial class VideoStreamFileLoadPlayer : VideoStreamPlayer
{
	[Export(PropertyHint.File)]
	private string videoFilePath;
	public void SetVideoFilePath(string path) => videoFilePath = path;

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
			return;

		ReloadVideoPath();
	}

	public void ReloadVideoPath()
	{
		if (string.IsNullOrEmpty(videoFilePath))
			return;

		string resolvedPath = ResourceUid.EnsurePath(videoFilePath);
		string portablePath = resolvedPath.GetBaseName() + ".ogv";
		// Theora is built into Godot and does not require the desktop FFmpeg extension.
		string targetPath = SaveManager.IsMobilePlatform ? portablePath : resolvedPath;
		if (!ResourceLoader.Exists(targetPath, "VideoStream") && ResourceLoader.Exists(portablePath, "VideoStream"))
			targetPath = portablePath;

		if (!ResourceLoader.Exists(targetPath, "VideoStream"))
		{
			GD.PushWarning($"Couldn't load video file {targetPath}!");
			return;
		}

		Stream = ResourceLoader.Load<VideoStream>(targetPath, "VideoStream");
	}
}
