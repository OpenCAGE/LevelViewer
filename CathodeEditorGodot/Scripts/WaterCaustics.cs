using Godot;

/// <summary>
/// A SimpleWater's caustics, as the game draws them (CA_WATER_CAUSTICS_OVERLAY, shaders/water_caustics.gdshader): a box over
/// (CAUSTIC_REFLECTIONS) or under (CAUSTIC_REFRACTIONS) the sheet, CAUSTIC_HEIGHT each way from its middle, that finds the
/// level's surfaces inside it and adds the caustics volume texture to them - the light the water throws, shimmering on what
/// faces it, strongest halfway up and gone at the box's faces. Drawn after the opaque level and before the water, as the
/// game's pass is, so the water's own mirror never has them. Only the alien nest's floor has any (above it, faint).
/// </summary>
public partial class WaterCaustics : Node3D
{
	private const string ShaderPath = "res://shaders/water_caustics.gdshader";
	private static Shader _shader;

	private static readonly StringName VolumeParam = new StringName("caustics_volume");
	private static readonly StringName TextureScaleParam = new StringName("texture_scale");
	private static readonly StringName IntensityParam = new StringName("intensity");
	private static readonly StringName SurfaceWrapParam = new StringName("surface_wrap");
	private static readonly StringName SpeedParam = new StringName("speed");
	private static readonly StringName WrapDirectionParam = new StringName("wrap_direction");

	//Under the water (refractions) and over it (reflections)
	private readonly MeshInstance3D[] _boxes = new MeshInstance3D[2];

	public static WaterCaustics Create(Node3D water)
	{
		WaterCaustics caustics = new WaterCaustics { Name = "WaterCaustics" };
		water.AddChild(caustics);
		return caustics;
	}

	public void Refresh(AlienScene scene, Vector3 half, bool refractions, bool reflections, string texturePath, float height,
		float textureScale, float intensity, float surfaceWrap, float speed)
	{
		Texture3D volume = scene?.GetVolumeTextureByPath(texturePath);
		height = Mathf.Max(height, 0.0001f);
		for (int side = 0; side < 2; side++)
		{
			bool wanted = volume != null && (side == 0 ? refractions : reflections) && intensity > 0f;
			MeshInstance3D box = _boxes[side];
			if (!wanted)
			{
				if (box != null)
					box.Visible = false;
				continue;
			}
			box ??= _boxes[side] = CreateBox(side);
			box.Visible = true;
			//The sheet's axes (this node sits on the water entity's), height up or down from it and as much again
			float up = side == 0 ? -1f : 1f;
			box.Position = new Vector3(0f, up * height, 0f);
			box.Scale = new Vector3(half.X * 2f, height * 2f, half.Z * 2f);
			ShaderMaterial material = (ShaderMaterial)box.MaterialOverride;
			material.SetShaderParameter(VolumeParam, volume);
			material.SetShaderParameter(TextureScaleParam, textureScale);
			material.SetShaderParameter(IntensityParam, intensity);
			material.SetShaderParameter(SurfaceWrapParam, surfaceWrap);
			material.SetShaderParameter(SpeedParam, speed);
			//Back towards the water: up from under it, down from over it
			material.SetShaderParameter(WrapDirectionParam, -up);
		}
	}

	public void HideAll()
	{
		foreach (MeshInstance3D box in _boxes)
		{
			if (box != null)
				box.Visible = false;
		}
	}

	private MeshInstance3D CreateBox(int side)
	{
		_shader ??= GD.Load<Shader>(ShaderPath);
		//After the opaque level and the surface effects' turn, before the water
		ShaderMaterial material = new ShaderMaterial { Shader = _shader, RenderPriority = -21 };
		Mesh mesh = PreviewVisualUtility.GetSharedMesh("surface_effect:box", () => new BoxMesh { Size = Vector3.One });
		MeshInstance3D box = new MeshInstance3D { Name = side == 0 ? "Refractions" : "Reflections", Mesh = mesh, MaterialOverride = material };
		LevelViewerMeshUtil.ConfigureMeshInstance(box);
		box.AddToGroup(LevelViewerPick.SceneFilterGroupName);
		AddChild(box);
		return box;
	}
}
