using Godot;
using System.Collections.Generic;

/// <summary>
/// The water's mirror, as the game draws it (RENDER_POV::render_reflection): one water plane a frame - the one most worth it
/// on screen - and the level rendered again from a camera mirrored through it, into a texture the water reads where it
/// lies on that plane (shaders/water_surface.gdshader). Only what the level marks include_in_planar_reflections is in it
/// (layer 20, AlienScene.ApplyReflectionLayer: the nest has 1,759 of its 5,389 models so), and nothing on the far side of the
/// plane (model_reference_clip_to_reflection). Half the screen's size and linear, as the screen the water otherwise reads
/// is; drawn only while some water is on screen. Other water - another plane, or none chosen - falls back to the
/// screen-space reflection.
/// </summary>
public partial class LevelViewerPlanarReflection : Node
{
	public const uint ReflectionLayer = 1u << 19;
	private const float ResolutionScale = 0.5f;

	private static readonly StringName ReflectionGlobal = new StringName("viewer_reflection");
	private static readonly StringName PlaneGlobal = new StringName("viewer_reflection_plane");
	private static readonly StringName ActiveGlobal = new StringName("viewer_reflection_active");

	//The water a plane can be taken from: sheets (WaterSurface) face up their own Y; puddles (a water material on a model's
	//mesh) lie flat
	private static readonly Dictionary<MeshInstance3D, bool> _candidates = new Dictionary<MeshInstance3D, bool>();

	public static void Register(MeshInstance3D water, bool facesOwnUp)
	{
		if (water == null || _candidates.ContainsKey(water))
			return;
		_candidates[water] = facesOwnUp;
		water.TreeExited += () => _candidates.Remove(water);
	}

	private SubViewport _viewport;
	private Camera3D _camera;
	private Godot.Environment _environment;
	private Godot.Environment _environmentSource;
	private Godot.Environment.BGMode _environmentBackground;
	private Color _environmentColour;
	private bool _active;

	public override void _Ready()
	{
		Name = "PlanarReflection";
		ProcessPriority = 100;
		RenderingServer.GlobalShaderParameterSet(ActiveGlobal, 0f);
	}

	public override void _Process(double delta)
	{
		Camera3D main = GetViewport()?.GetCamera3D();
		if (main == null || !PreviewVisibilitySettings.RenderWater || !PreviewVisibilitySettings.PlanarReflections
			|| !TryChoosePlane(main, out Vector3 normal, out float distance))
		{
			SetActive(false);
			return;
		}

		EnsureViewport(main);
		//Mirrored through the plane: its position, and the way it faces and its up reflected. The basis built from those is
		//the mirror's turned round its vertical, so the water reads it flipped across (as the game's reads 1 - u)
		Transform3D view = main.GlobalTransform;
		Vector3 position = view.Origin - 2f * (normal.Dot(view.Origin) - distance) * normal;
		Vector3 forward = Reflect(-view.Basis.Z, normal);
		Vector3 up = Reflect(view.Basis.Y, normal);
		_camera.GlobalTransform = new Transform3D(Basis.LookingAt(forward, up), position);
		_camera.Fov = main.Fov;
		_camera.Near = main.Near;
		_camera.Far = main.Far;
		_camera.KeepAspect = main.KeepAspect;

		RenderingServer.GlobalShaderParameterSet(PlaneGlobal, new Vector4(normal.X, normal.Y, normal.Z, distance));
		SetActive(true);
	}

	private static Vector3 Reflect(Vector3 v, Vector3 n) => v - 2f * v.Dot(n) * n;

	/* The water most worth mirroring: on screen, the camera above it, and the biggest for how far away it is - the game
	   keeps one reflection plane a frame too (cReflectionMap::GetOptimalCandidate) */
	private static bool TryChoosePlane(Camera3D camera, out Vector3 normal, out float distance)
	{
		normal = Vector3.Up;
		distance = 0f;
		if (_candidates.Count == 0)
			return false;

		Godot.Collections.Array<Plane> frustum = camera.GetFrustum();
		Vector3 eye = camera.GlobalPosition;
		float best = 0f;
		bool found = false;
		List<MeshInstance3D> dead = null;
		foreach (KeyValuePair<MeshInstance3D, bool> entry in _candidates)
		{
			MeshInstance3D water = entry.Key;
			if (!GodotObject.IsInstanceValid(water))
			{
				(dead ??= new List<MeshInstance3D>()).Add(water);
				continue;
			}
			if (!water.IsVisibleInTree())
				continue;
			Aabb box = water.GlobalTransform * water.GetAabb();
			if (!Intersects(frustum, box))
				continue;
			Vector3 n = entry.Value ? water.GlobalBasis.Y.Normalized() : Vector3.Up;
			Vector3 point = entry.Value ? water.GlobalPosition : box.GetCenter();
			float height = n.Dot(eye - point);
			if (height <= 0.01f)
				continue;
			float reach = Mathf.Max(box.Size.Length(), 0.1f);
			float away = Mathf.Max(eye.DistanceTo(Closest(box, eye)), 1f);
			float score = reach / away;
			if (score > best)
			{
				best = score;
				found = true;
				normal = n;
				distance = n.Dot(point);
			}
		}
		if (dead != null)
		{
			foreach (MeshInstance3D water in dead)
				_candidates.Remove(water);
		}
		return found;
	}

	private static bool Intersects(Godot.Collections.Array<Plane> frustum, Aabb box)
	{
		Vector3 min = box.Position, max = box.End;
		foreach (Plane plane in frustum)
		{
			//The box's corner furthest along the plane's normal (Godot's frustum planes face out)
			Vector3 inner = new Vector3(plane.Normal.X > 0f ? min.X : max.X, plane.Normal.Y > 0f ? min.Y : max.Y, plane.Normal.Z > 0f ? min.Z : max.Z);
			if (plane.DistanceTo(inner) > 0f)
				return false;
		}
		return true;
	}

	private static Vector3 Closest(Aabb box, Vector3 point) => point.Clamp(box.Position, box.End);

	private void EnsureViewport(Camera3D main)
	{
		Vector2 screen = main.GetViewport().GetVisibleRect().Size;
		Vector2I size = new Vector2I(Mathf.Max((int)(screen.X * ResolutionScale), 16), Mathf.Max((int)(screen.Y * ResolutionScale), 16));
		if (_viewport == null || !GodotObject.IsInstanceValid(_viewport))
		{
			_viewport = new SubViewport
			{
				Name = "ReflectionViewport",
				Size = size,
				//The level as it is, seen again: the world itself, never a copy
				World3D = main.GetViewport().FindWorld3D(),
				RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
				//Linear and unclamped, as the screen the water reads otherwise is (its tonemap comes after)
				UseHdr2D = true,
				HandleInputLocally = false,
				GuiDisableInput = true,
				Msaa3D = Viewport.Msaa.Disabled,
				ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
			};
			AddChild(_viewport);
			_camera = new Camera3D { Name = "ReflectionCamera", CullMask = ReflectionLayer, Current = true };
			_viewport.AddChild(_camera);
			RenderingServer.GlobalShaderParameterSet(ReflectionGlobal, _viewport.GetTexture());
		}
		else if (_viewport.Size != size)
			_viewport.Size = size;
		EnsureEnvironment();
	}

	/* The level's environment, untonemapped: the screen the water reads is the scene before its tonemap, and the water's
	   own colour goes through that tonemap after. Copied again when the level's sky changes (Render Galaxy). */
	private void EnsureEnvironment()
	{
		WorldEnvironment world = GetTree().Root.FindChild("WorldEnvironment", true, false) as WorldEnvironment;
		Godot.Environment source = world?.Environment;
		if (source == null)
			return;
		if (_environment != null && source == _environmentSource && source.BackgroundMode == _environmentBackground && source.BackgroundColor == _environmentColour)
			return;
		_environmentSource = source;
		_environmentBackground = source.BackgroundMode;
		_environmentColour = source.BackgroundColor;
		_environment = (Godot.Environment)source.Duplicate();
		_environment.TonemapMode = Godot.Environment.ToneMapper.Linear;
		_environment.TonemapExposure = 1f;
		_environment.GlowEnabled = false;
		_environment.AdjustmentEnabled = false;
		_camera.Environment = _environment;
	}

	private void SetActive(bool active)
	{
		if (active == _active)
			return;
		_active = active;
		if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
			_viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
		RenderingServer.GlobalShaderParameterSet(ActiveGlobal, active ? 1f : 0f);
		if (!active)
			RenderingServer.GlobalShaderParameterSet(PlaneGlobal, new Vector4(0f, 1f, 0f, -100000f));
	}
}
