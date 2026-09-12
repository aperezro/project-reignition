using Godot;
using Project.Core;
using System.Collections.Generic;

namespace Project.Gameplay;

public partial class MultiplicativeBloomRenderer : Node
{
	[Export] private Camera3D    bloomCamera;
	[Export] private SubViewport bloomViewport;
	[Export] private Camera3D    foregroundMaskCamera;
	[Export] private SubViewport foregroundMaskViewport;
	[Export] private string      compositorScriptPath = "res://MultiplicativeBloomCompositor.gd";

	[Export(PropertyHint.Layers3DRender)] private uint foregroundLayers = 1u << 6;          // Layer 7 = Sonic
	[Export(PropertyHint.Layers3DRender)] private uint terrainLayers    = 1u << 1;          // Layer 2 = terrain. Set to 0 to disable.

	[Export(PropertyHint.Range, "64,512,1")]      private int   maxBloomHeight            = 256;
	[Export(PropertyHint.Range, "64,2160,1")]     private int   maxForegroundMaskHeight   = 256;
	[Export(PropertyHint.Range, "5.0,200.0,1.0")] private float maxForegroundMaskDistance = 30.0f;

	[Export(PropertyHint.Range, "0.1,6.0,0.05")]  private float strength            = 1.5f;
	[Export(PropertyHint.Range, "0.1,4.0,0.05")]  private float maskBlurStrength    = 1.0f;
	[Export(PropertyHint.Range, "0.0,0.2,0.001")] private float effectThreshold     = 0.01f;
	[Export(PropertyHint.Range, "0.05,5.0,0.01")] private float foregroundThreshold = 1.3f;
	[Export(PropertyHint.Range, "0.0,1.0,0.01")]  private float foregroundEdgeStart = 0.5f;

	private const uint  BloomLayerBit       = 1u << 17;       // Layer 18 - bloom cubes
	private const uint  OccluderRenderLayer = 1u << 18;       // Layer 19 - reserved for terrain duplicates
	private const float MaxBloomDistance    = 80.0f;

	private GodotObject _compositorEffect;
	private Camera3D    _attachedCamera;
	private StandardMaterial3D _occluderMaterial;
	private readonly List<MeshInstance3D> _bloomMeshes = new();
	private ShaderMaterial _mobileComposite;
	private readonly List<ShaderMaterial> _mobileBloomBlur = new();
	private readonly List<ShaderMaterial> _mobileMaskBlur = new();

	private const string MobileBlurShader = """
		shader_type canvas_item;
		render_mode unshaded, blend_disabled;
		uniform sampler2D source_tex : filter_linear, repeat_disable;
		uniform vec2 texel_size;
		uniform vec2 direction;
		uniform float strength = 1.0;
		void fragment() {
			vec2 offset = texel_size * strength * direction;
			COLOR = texture(source_tex, UV) * 0.3125;
			COLOR += (texture(source_tex, UV - offset) + texture(source_tex, UV + offset)) * 0.234375;
			COLOR += (texture(source_tex, UV - offset * 2.0) + texture(source_tex, UV + offset * 2.0)) * 0.09375;
			COLOR += (texture(source_tex, UV - offset * 3.0) + texture(source_tex, UV + offset * 3.0)) * 0.015625;
		}
		""";
	private const string MobileCompositeShader = """
		shader_type canvas_item;
		render_mode unshaded, blend_mul;
		uniform sampler2D bloom_tex : filter_linear, repeat_disable;
		uniform sampler2D mask_tex : filter_linear, repeat_disable;
		uniform float effect_threshold;
		uniform float foreground_threshold;
		uniform float foreground_edge_start;
		void fragment() {
			float foreground = texture(mask_tex, UV).a;
			float amount = 1.0 - smoothstep(foreground_edge_start, foreground_threshold, foreground);
			vec3 bloom = texture(bloom_tex, UV).rgb;
			if (1.0 - dot(bloom, vec3(0.333)) < effect_threshold) amount = 0.0;
			COLOR = vec4(mix(vec3(1.0), bloom, amount), 1.0);
		}
		""";

	public override void _Ready()
	{
		if (RenderingServer.GetCurrentRenderingMethod() == "mobile")
		{
			// Mobile's scene-color attachment is not a storage image. Use the same
			// separable blur and multiply blend through small raster viewports instead.
			SetupMobileComposite();
		}
		else
		{
			var script = ResourceLoader.Load<GDScript>(compositorScriptPath);
			if (script == null)
			{
				GD.PrintErr("MultiplicativeBloomRenderer: failed to load ", compositorScriptPath);
				return;
			}
			_compositorEffect = (GodotObject)script.New();
		}

		SetupBloomViewport();
		SetupForegroundMaskViewport();

		// Always-on rendering. Per-pixel early-returns in the shader handle
		// the "no bloom contribution" case efficiently.
		if (bloomViewport          != null) bloomViewport.RenderTargetUpdateMode          = SubViewport.UpdateMode.Always;
		if (foregroundMaskViewport != null) foregroundMaskViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

		CallDeferred(MethodName.CacheBloomMeshes);
		CallDeferred(MethodName.SetupOccluders);
	}

	private void SetupMobileComposite()
	{
		if (bloomViewport == null || foregroundMaskViewport == null)
			return;

		Shader blurShader = new() { Code = MobileBlurShader };
		Texture2D bloom = CreateMobileBlur(bloomViewport.GetTexture(), bloomViewport.Size, blurShader, _mobileBloomBlur);
		Texture2D mask = CreateMobileBlur(foregroundMaskViewport.GetTexture(), foregroundMaskViewport.Size, blurShader, _mobileMaskBlur);
		_mobileComposite = new ShaderMaterial { Shader = new Shader { Code = MobileCompositeShader } };
		_mobileComposite.SetShaderParameter("bloom_tex", bloom);
		_mobileComposite.SetShaderParameter("mask_tex", mask);
		CanvasLayer layer = new() { Name = "MobileBloomComposite", Layer = -1 };
		AddChild(layer);
		ColorRect quad = new() { Color = Colors.White, Material = _mobileComposite, MouseFilter = Control.MouseFilterEnum.Ignore };
		layer.AddChild(quad);
		quad.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
	}

	private Texture2D CreateMobileBlur(Texture2D source, Vector2I size, Shader shader, List<ShaderMaterial> materials)
	{
		for (int pass = 0; pass < 2; pass++)
		{
			SubViewport viewport = new()
			{
				Size = size,
				Disable3D = true,
				TransparentBg = true,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			};
			ShaderMaterial material = new() { Shader = shader };
			material.SetShaderParameter("source_tex", source);
			material.SetShaderParameter("texel_size", new Vector2(1f / size.X, 1f / size.Y));
			material.SetShaderParameter("direction", pass == 0 ? Vector2.Right : Vector2.Down);
			materials.Add(material);
			viewport.AddChild(new ColorRect { Size = size, Color = Colors.White, Material = material, MouseFilter = Control.MouseFilterEnum.Ignore });
			AddChild(viewport);
			source = viewport.GetTexture();
		}
		return source;
	}

	private void SetupBloomViewport()
	{
		if (bloomCamera == null || bloomViewport == null) return;

		// Render bloom cubes plus terrain occluder duplicates
		bloomCamera.CullMask = BloomLayerBit | OccluderRenderLayer;
		bloomCamera.Near = 0.01f;
		bloomCamera.Far  = MaxBloomDistance;

		var env = new Godot.Environment();
		env.BackgroundMode  = Godot.Environment.BGMode.Color;
		env.BackgroundColor = new Color(1, 1, 1);
		bloomCamera.Environment = env;
		bloomCamera.MakeCurrent();
	}

	private void SetupForegroundMaskViewport()
	{
		if (foregroundMaskCamera == null || foregroundMaskViewport == null) return;

		foregroundMaskCamera.CullMask = foregroundLayers;
		foregroundMaskCamera.Near = 0.01f;
		foregroundMaskCamera.Far  = maxForegroundMaskDistance;

		var env = new Godot.Environment();
		env.BackgroundMode = Godot.Environment.BGMode.ClearColor;
		foregroundMaskCamera.Environment = env;
		foregroundMaskCamera.MakeCurrent();

		foregroundMaskViewport.TransparentBg = true;
	}

	public override void _EnterTree()
	{
		Vector2I rootSize = GetTree().Root.Size;

		Vector2I bloomSize = rootSize;
		if (bloomSize.Y > maxBloomHeight)
		{
			float aspect = (float)rootSize.X / rootSize.Y;
			bloomSize = new Vector2I((int)(maxBloomHeight * aspect), maxBloomHeight);
		}
		if (bloomViewport != null) bloomViewport.Size = bloomSize;

		Vector2I maskSize = rootSize;
		if (maskSize.Y > maxForegroundMaskHeight)
		{
			float aspect = (float)rootSize.X / rootSize.Y;
			maskSize = new Vector2I((int)(maxForegroundMaskHeight * aspect), maxForegroundMaskHeight);
		}
		if (foregroundMaskViewport != null) foregroundMaskViewport.Size = maskSize;
	}

	private void CacheBloomMeshes()
	{
		_bloomMeshes.Clear();
		CollectBloomMeshes(GetTree().Root);
		GD.Print("MultiplicativeBloomRenderer: cached ", _bloomMeshes.Count, " bloom meshes.");
	}

	private void CollectBloomMeshes(Node node)
	{
		if (node is MeshInstance3D mi && (mi.Layers & BloomLayerBit) != 0)
			_bloomMeshes.Add(mi);
		foreach (Node child in node.GetChildren())
			CollectBloomMeshes(child);
	}

	private void SetupOccluders()
	{
		if (terrainLayers == 0) return;

		// Cheap unshaded white material - matches bloom viewport background,
		// so the duplicate is visually invisible but writes depth.
		_occluderMaterial = new StandardMaterial3D();
		_occluderMaterial.AlbedoColor = new Color(1, 1, 1);
		_occluderMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;

		var sources = new List<MeshInstance3D>();
		CollectOccluderSources(GetTree().Root, sources);

		foreach (var src in sources)
		{
			if (!IsInstanceValid(src) || src.Mesh == null) continue;

			var dup = new MeshInstance3D
			{
				Name             = "_BloomOccluder",
				Mesh             = src.Mesh,                // shared resource - no extra mesh memory
				MaterialOverride = _occluderMaterial,
				Layers           = OccluderRenderLayer,
				CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off
			};
			src.AddChild(dup);                              // inherits transform from parent
		}

		GD.Print("MultiplicativeBloomRenderer: created ", sources.Count, " bloom occluders.");
	}

	private void CollectOccluderSources(Node node, List<MeshInstance3D> list)
	{
		if (node is MeshInstance3D mi
			&& (mi.Layers & terrainLayers)  != 0
			&& (mi.Layers & BloomLayerBit)  == 0
			&& (mi.Layers & OccluderRenderLayer) == 0)
		{
			list.Add(mi);
		}
		foreach (Node child in node.GetChildren())
			CollectOccluderSources(child, list);
	}

	public override void _Process(double delta)
	{
		if (_compositorEffect == null && _mobileComposite == null) return;

		var camera = GetViewport().GetCamera3D();
		if (camera == null) return;

		SyncCameras(camera);
		if (_mobileComposite != null)
		{
			foreach (ShaderMaterial material in _mobileBloomBlur)
				material.SetShaderParameter("strength", strength);
			foreach (ShaderMaterial material in _mobileMaskBlur)
				material.SetShaderParameter("strength", maskBlurStrength);
			_mobileComposite.SetShaderParameter("effect_threshold", effectThreshold);
			_mobileComposite.SetShaderParameter("foreground_threshold", foregroundThreshold);
			_mobileComposite.SetShaderParameter("foreground_edge_start", foregroundEdgeStart);
			return;
		}

		EnsureCompositorAttached(camera);

		_compositorEffect.Set("strength",              strength);
		_compositorEffect.Set("mask_blur_strength",    maskBlurStrength);
		_compositorEffect.Set("effect_threshold",      effectThreshold);
		_compositorEffect.Set("foreground_threshold",  foregroundThreshold);
		_compositorEffect.Set("foreground_edge_start", foregroundEdgeStart);

		if (bloomViewport != null)
		{
			_compositorEffect.Call("update_bloom_size", bloomViewport.Size.X, bloomViewport.Size.Y);
			Rid rsRid = bloomViewport.GetTexture().GetRid();
			Rid rdRid = RenderingServer.TextureGetRdTexture(rsRid);
			_compositorEffect.Call("update_bloom_texture", rdRid);
		}
		if (foregroundMaskViewport != null)
		{
			_compositorEffect.Call("update_mask_size", foregroundMaskViewport.Size.X, foregroundMaskViewport.Size.Y);
			Rid rsRid = foregroundMaskViewport.GetTexture().GetRid();
			Rid rdRid = RenderingServer.TextureGetRdTexture(rsRid);
			_compositorEffect.Call("update_foreground_mask", rdRid);
		}
	}

	private void SyncCameras(Camera3D mainCamera)
	{
		if (bloomCamera != null)
		{
			bloomCamera.Fov             = mainCamera.Fov;
			bloomCamera.Size            = mainCamera.Size;
			bloomCamera.Projection      = mainCamera.Projection;
			bloomCamera.Near            = mainCamera.Near;
			bloomCamera.Far             = mainCamera.Far;
			bloomCamera.GlobalTransform = mainCamera.GetGlobalTransformInterpolated();
			bloomCamera.ResetPhysicsInterpolation();
		}
		if (foregroundMaskCamera != null)
		{
			foregroundMaskCamera.Fov             = mainCamera.Fov;
			foregroundMaskCamera.Size            = mainCamera.Size;
			foregroundMaskCamera.Projection      = mainCamera.Projection;
			foregroundMaskCamera.Near            = mainCamera.Near;
			foregroundMaskCamera.Far             = maxForegroundMaskDistance;
			foregroundMaskCamera.GlobalTransform = mainCamera.GetGlobalTransformInterpolated();
			foregroundMaskCamera.ResetPhysicsInterpolation();
		}
	}

	private void EnsureCompositorAttached(Camera3D camera)
	{
		if (camera == _attachedCamera) return;
		_attachedCamera = camera;

		Compositor compositor = camera.Compositor;
		Godot.Collections.Array effects;

		if (compositor == null)
		{
			compositor = new Compositor();
			effects    = new Godot.Collections.Array();
			effects.Add(_compositorEffect);
			compositor.Set("compositor_effects", effects);
			camera.Compositor = compositor;
			return;
		}

		effects = compositor.Get("compositor_effects").AsGodotArray();
		if (effects == null) effects = new Godot.Collections.Array();

		bool present = false;
		foreach (var e in effects)
			if (e.Obj == _compositorEffect) { present = true; break; }

		if (!present)
		{
			effects.Add(_compositorEffect);
			compositor.Set("compositor_effects", effects);
		}
	}
}
