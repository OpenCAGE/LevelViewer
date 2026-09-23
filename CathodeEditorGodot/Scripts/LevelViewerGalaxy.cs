using CATHODE;
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// The level's galaxy - the starfield in RENDERABLE/GALAXY/GALAXY.ITEMS_BIN - drawn as the viewport's sky,
/// the way the game's cGalaxyManager draws it. With it switched off, or on a level with no stars, the
/// plain sky comes back.
///
/// Every star is a quad as big as its angular size on screen plus one pixel, centred on the direction it
/// is stored with and put on the far plane, so it only shows where nothing else was drawn. The pixel
/// shader does not sample the star: it integrates the star's gaussian over the part of it each pixel
/// covers, with an erf table built exactly as the engine's CreateErfTable builds it, so a star smaller
/// than a pixel keeps its brightness instead of flickering in and out as the camera turns. A star's
/// colour is its template colour times its intensity, both packed into the vertex colour as bytes as the
/// game packs them, and it is added to a black background in the same HDR units the level is drawn in.
///
/// The one departure is for a viewport smaller than the game's frame. A star below a pixel spreads its
/// light over the whole pixel, and a docked viewport's pixels each cover several times the sky a 1080p
/// game pixel does, so the same star came out several times dimmer than in the game - the retail galaxy
/// all but vanished in a 400-pixel-high panel. Below 1080 rows the light is scaled back up to what a
/// 1080-row frame would give its pixel, capped at the star's own peak so a star bigger than a pixel is
/// not brightened past it. From 1080 rows up the result is exactly the game's.
///
/// A plain class holding a MeshInstance3D rather than a node script, the way LevelViewerBoxSelect holds its
/// panel: a script class the exported .pck has never heard of can't be put in the tree. The shader is built
/// from code for the same reason.
/// </summary>
public static class LevelViewerGalaxy
{
	//cGalaxyManager::CreateErfTable: 128 texels holding the running sum of a gaussian of sigma 0.3 sampled
	//over [-1, 1), normalised by the total - so across a star's quad, 0 to 1, it goes from 0 to 1
	private const int ErfTableSize = 128;
	private const float ErfTableSigma = 0.3f;

	//The game's frame height the stars are matched to in a smaller viewport (see above)
	private const float ReferenceRows = 1080f;

	/* CA_GALAXY with TRILIST_VERTS, which is the only permutation the game draws with (its point sprite
	   pixel shader returns black). VERTEX is the star's direction, UV the quad corner, UV2.x the star's
	   angular size in radians and COLOR its colour with its intensity in alpha.

	   There is no guard on the sign of w, as there is none in the game: a star behind the camera lands
	   mirrored through the middle of the screen, so the sky there shows both halves of the galaxy at once
	   and is as dense as the game's. */
	private const string ShaderCode = @"shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled, skip_vertex_transform;

uniform sampler2D erf_table : filter_linear, repeat_disable;
uniform vec2 projection_scale = vec2(1.0);
uniform vec2 pixel_size = vec2(0.001);
uniform float pixel_gain = 1.0;
uniform float peak_density = 7.0;

varying vec2 star_uv;
varying vec3 star_colour;

void vertex() {
	vec2 corner = (UV - 0.5) * vec2(1.0, -1.0);
	vec2 size = projection_scale * UV2.x;
	vec2 grow = pixel_size / max(size, vec2(1e-9));
	star_uv = grow * corner + corner + 0.5;
	star_colour = COLOR.rgb * COLOR.a;

	vec4 clip = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(VERTEX, 0.0));
	vec2 ndc = clip.xy / clip.w;
	POSITION = vec4((size + pixel_size) * corner + ndc, 0.0, 1.0);
}

void fragment() {
	vec2 footprint = vec2(dFdx(star_uv.x), dFdy(star_uv.y));
	vec2 hi = star_uv + footprint * 0.5;
	vec2 lo = star_uv - footprint * 0.5;
	float area = footprint.x * footprint.y;
	float ex = texture(erf_table, vec2(hi.x, 0.5)).r - texture(erf_table, vec2(lo.x, 0.5)).r;
	float ey = texture(erf_table, vec2(hi.y, 0.5)).r - texture(erf_table, vec2(lo.y, 0.5)).r;
	vec3 average = ey * (ex * star_colour) / (area != 0.0 ? area : 1.0);
	ALBEDO = min(average * pixel_gain, star_colour * peak_density);
}
";

	private static readonly StringName ProjectionScaleParam = new StringName("projection_scale");
	private static readonly StringName PixelSizeParam = new StringName("pixel_size");
	private static readonly StringName PixelGainParam = new StringName("pixel_gain");
	private static readonly StringName PeakDensityParam = new StringName("peak_density");
	private static readonly StringName ErfTableParam = new StringName("erf_table");

	private static MeshInstance3D _stars;
	private static ShaderMaterial _material;
	private static int _starCount;
	private static bool _enabled = true;
	private static bool _subscribed;
	private static Vector2 _projectionScale;
	private static Vector2 _pixelSize;

	private static WorldEnvironment _worldEnvironment;
	private static bool _backgroundSaved;
	private static Godot.Environment.BGMode _savedBackgroundMode;
	private static Godot.Environment.ReflectionSource _savedReflectionSource;

	/// <summary>Whether the galaxy is being drawn as the sky right now.</summary>
	private static bool Showing => _enabled && _starCount > 0;

	/// <summary>
	/// Show this galaxy - the loaded level's, or one OpenCAGE regenerated - in place of whatever was shown.
	/// Null or empty puts the plain sky back. Main thread only.
	/// </summary>
	public static void SetGalaxy(Node host, GalaxyItems galaxy)
	{
		List<GalaxyItems.Star> stars = galaxy?.Entries;
		if (stars == null || stars.Count == 0)
		{
			ClearStars();
			Apply(host);
			return;
		}

		ArrayMesh mesh = BuildStarMesh(stars, out int built);
		if (mesh == null)
		{
			ClearStars();
			Apply(host);
			return;
		}

		MeshInstance3D node = EnsureStarNode(host);
		if (node == null)
		{
			mesh.Dispose();
			return;
		}

		Mesh previous = node.Mesh;
		node.Mesh = mesh;
		if (previous != null && GodotObject.IsInstanceValid(previous))
			previous.Dispose();

		_starCount = built;
		Apply(host);
		ViewerLog.Print("[Galaxy] " + built + " stars" + (built != stars.Count ? " (" + (stars.Count - built) + " without a direction skipped)" : "") + ".");
	}

	/// <summary>The viewport option: draw the galaxy, or the plain sky. Main thread only.</summary>
	public static void SetEnabled(Node host, bool enabled)
	{
		if (_enabled == enabled)
			return;
		_enabled = enabled;
		Apply(host);
	}

	private static void ClearStars()
	{
		_starCount = 0;
		if (_stars != null && GodotObject.IsInstanceValid(_stars))
		{
			Mesh previous = _stars.Mesh;
			_stars.Mesh = null;
			if (previous != null && GodotObject.IsInstanceValid(previous))
				previous.Dispose();
		}
	}

	private static void Apply(Node host)
	{
		bool show = Showing;
		if (_stars != null && GodotObject.IsInstanceValid(_stars))
			_stars.Visible = show;
		if (show)
			UpdateProjection();
		ApplyBackground(host, show);
	}

	/* The galaxy is drawn onto black, as space is in the game; the plain sky is the scene's own. The sky
	   stays the reflection source either way, so nothing lit changes with the background. */
	private static void ApplyBackground(Node host, bool galaxy)
	{
		WorldEnvironment worldEnvironment = FindWorldEnvironment(host);
		Godot.Environment environment = worldEnvironment?.Environment;
		if (environment == null)
			return;

		if (!_backgroundSaved)
		{
			_savedBackgroundMode = environment.BackgroundMode;
			_savedReflectionSource = environment.ReflectedLightSource;
			_backgroundSaved = true;
		}

		if (galaxy)
		{
			if (_savedReflectionSource == Godot.Environment.ReflectionSource.Bg && environment.Sky != null)
				environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
			environment.BackgroundColor = Colors.Black;
			environment.BackgroundMode = Godot.Environment.BGMode.Color;
		}
		else
		{
			environment.BackgroundMode = _savedBackgroundMode;
			environment.ReflectedLightSource = _savedReflectionSource;
		}
	}

	private static WorldEnvironment FindWorldEnvironment(Node host)
	{
		if (_worldEnvironment != null && GodotObject.IsInstanceValid(_worldEnvironment))
			return _worldEnvironment;
		if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
			return null;

		Node root = host.GetTree().CurrentScene ?? host.GetParent();
		_worldEnvironment = root?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");
		return _worldEnvironment;
	}

	/* One node for the life of the viewer, beside the level rather than in it, so nothing that walks the
	   level's nodes (picking, framing, box select, render filters) ever meets it. */
	private static MeshInstance3D EnsureStarNode(Node host)
	{
		if (_stars != null && GodotObject.IsInstanceValid(_stars))
			return _stars;
		if (host == null || !GodotObject.IsInstanceValid(host) || !host.IsInsideTree())
			return null;

		Node parent = host.GetTree().CurrentScene ?? host.GetParent();
		if (parent == null)
			return null;

		Shader shader = new Shader { Code = ShaderCode };
		_material = new ShaderMaterial
		{
			Shader = shader,
			//Under every other transparent thing, so glass and particles blend over the stars
			RenderPriority = (int)Material.RenderPriorityMin,
		};
		_material.SetShaderParameter(ErfTableParam, BuildErfTable(out float peakDensity));
		_material.SetShaderParameter(PeakDensityParam, peakDensity);

		_stars = new MeshInstance3D
		{
			Name = "Galaxy",
			MaterialOverride = _material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			GIMode = GeometryInstance3D.GIModeEnum.Disabled,
			//The shader moves every vertex to the sky, wherever the camera is: never cull it
			CustomAabb = new Aabb(new Vector3(-1e6f, -1e6f, -1e6f), new Vector3(2e6f, 2e6f, 2e6f)),
			Visible = false,
		};
		parent.AddChild(_stars);

		if (!_subscribed)
		{
			RenderingServer.FramePreDraw += UpdateProjection;
			_subscribed = true;
		}
		return _stars;
	}

	/* A star's size on screen is its angular size through the projection, and the quad grows by one
	   render target pixel: both follow the camera and the viewport, so they are refreshed before a frame
	   is drawn whenever either has changed. */
	private static void UpdateProjection()
	{
		if (_stars == null || !GodotObject.IsInstanceValid(_stars) || !_stars.Visible || !_stars.IsInsideTree())
			return;

		Viewport viewport = _stars.GetViewport();
		Camera3D camera = viewport?.GetCamera3D();
		if (camera == null)
			return;

		Vector2 size = viewport.GetVisibleRect().Size;
		if (size.X < 1f || size.Y < 1f)
			return;

		Projection projection = camera.GetCameraProjection();
		Vector2 projectionScale = new Vector2(Mathf.Abs(projection.X.X), Mathf.Abs(projection.Y.Y));
		Vector2 pixelSize = new Vector2(2f / size.X, 2f / size.Y);

		if (projectionScale != _projectionScale)
		{
			_projectionScale = projectionScale;
			_material.SetShaderParameter(ProjectionScaleParam, projectionScale);
		}
		if (pixelSize != _pixelSize)
		{
			_pixelSize = pixelSize;
			_material.SetShaderParameter(PixelSizeParam, pixelSize);
			float coarser = Mathf.Max(1f, ReferenceRows / size.Y);
			_material.SetShaderParameter(PixelGainParam, coarser * coarser);
		}
	}

	private static ArrayMesh BuildStarMesh(List<GalaxyItems.Star> stars, out int built)
	{
		built = 0;
		Vector3[] vertices = new Vector3[stars.Count * 4];
		Vector2[] corners = new Vector2[stars.Count * 4];
		Vector2[] sizes = new Vector2[stars.Count * 4];
		Color[] colours = new Color[stars.Count * 4];
		int[] indices = new int[stars.Count * 6];

		foreach (GalaxyItems.Star star in stars)
		{
			if (star == null)
				continue;

			//The engine draws the direction as stored (retail's are unit vectors, a regenerated galaxy's
			//need not be); only its direction matters, so one with none cannot be placed
			Vector3 direction = CathodeCoordinates.DirectionToGodot(star.Position);
			if (!(direction.LengthSquared() > 1e-12f) || float.IsNaN(direction.X + direction.Y + direction.Z))
				continue;

			Color colour = new Color(
				Mathf.Clamp(star.Colour.X, 0f, 1f),
				Mathf.Clamp(star.Colour.Y, 0f, 1f),
				Mathf.Clamp(star.Colour.Z, 0f, 1f),
				Mathf.Clamp(star.Intensity, 0f, 1f));
			Vector2 size = new Vector2(Mathf.Max(star.Size, 0f), 0f);

			int v = built * 4;
			for (int corner = 0; corner < 4; corner++)
			{
				vertices[v + corner] = direction;
				sizes[v + corner] = size;
				colours[v + corner] = colour;
			}
			corners[v] = new Vector2(0f, 0f);
			corners[v + 1] = new Vector2(1f, 0f);
			corners[v + 2] = new Vector2(1f, 1f);
			corners[v + 3] = new Vector2(0f, 1f);

			int i = built * 6;
			indices[i] = v;
			indices[i + 1] = v + 1;
			indices[i + 2] = v + 2;
			indices[i + 3] = v + 2;
			indices[i + 4] = v + 3;
			indices[i + 5] = v;
			built++;
		}

		if (built == 0)
			return null;
		if (built < stars.Count)
		{
			Array.Resize(ref vertices, built * 4);
			Array.Resize(ref corners, built * 4);
			Array.Resize(ref sizes, built * 4);
			Array.Resize(ref colours, built * 4);
			Array.Resize(ref indices, built * 6);
		}

		Godot.Collections.Array arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices;
		arrays[(int)Mesh.ArrayType.TexUV] = corners;
		arrays[(int)Mesh.ArrayType.TexUV2] = sizes;
		arrays[(int)Mesh.ArrayType.Color] = colours;
		arrays[(int)Mesh.ArrayType.Index] = indices;

		ArrayMesh mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}

	/* The engine's table, value for value: the running sum is truncated to 16 bits as it is written into
	   the engine's 16-bit texture. Also gives the star's brightest point, per unit of its area: the slope
	   of the table at its centre, squared (a pixel wholly inside the middle of a star gets that much). */
	private static ImageTexture BuildErfTable(out float peakDensity)
	{
		float[] sums = new float[ErfTableSize];
		float k = 1f / (2f * ErfTableSigma * ErfTableSigma);
		float sum = 0f;
		for (int i = 0; i < ErfTableSize; i++)
		{
			float x = (i - ErfTableSize / 2) / (ErfTableSize / 2f);
			sum += MathF.Exp(-k * x * x);
			sums[i] = sum;
		}

		peakDensity = (sums[ErfTableSize / 2] - sums[ErfTableSize / 2 - 1]) / sum * ErfTableSize;
		peakDensity *= peakDensity;

		byte[] data = new byte[ErfTableSize * sizeof(float)];
		for (int i = 0; i < ErfTableSize; i++)
		{
			float value = (int)(65535f * sums[i] / sum) / 65535f;
			BitConverter.GetBytes(value).CopyTo(data, i * sizeof(float));
		}

		Image image = Image.CreateFromData(ErfTableSize, 1, false, Image.Format.Rf, data);
		return ImageTexture.CreateFromImage(image);
	}
}
