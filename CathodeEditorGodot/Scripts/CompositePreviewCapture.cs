using Godot;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// Takes a square preview of one subtree of the scene - whatever it draws, framed in the middle of a
/// transparent square - for the composite previews OpenCAGE shows beside its composite list.
/// </summary>
/// <remarks>
/// The preview comes from a second camera in an off-screen SubViewport that renders the SAME world as
/// the viewport on screen, so nothing is copied to be captured: whatever is in the scene is captured
/// where it stands (the composite previews build each composite as the scene, one after another). What
/// keeps the two cameras apart is a render layer. The subtree being captured is tagged with layer 21
/// for the duration; the capture camera draws only that layer, and the main camera (Godot's default
/// 20-layer mask) never draws it - so tagging changes nothing on screen, and the capture camera never
/// sees the galaxy or an overlay. The level's shaders are fullbright with a fixed light direction, so
/// a camera anywhere sees the same shading as the viewport does.
///
/// The square around the content is transparent, so the browser can lay the preview on whatever it
/// likes: the viewport clears to nothing at all and the preview keeps that alpha all the way to the
/// file (an RGBA PNG). A pixel's alpha is how much of it the content covers - all of it inside a mesh,
/// none outside, part of it along an anti-aliased edge or through a transparent material.
///
/// Rendered at twice the output size and brought down with a Lanczos filter: at 256 pixels across the
/// renderer's own MSAA is not enough on its own for edges to read cleanly. The scaling works on
/// premultiplied colour in linear light (see FinishCapture); scaled as the file holds it, the black of
/// the empty pixels would bleed into every edge and leave a dark rim round the content.
/// </remarks>
public partial class CompositePreviewCapture : Node3D
{
	/// <summary>Render layer 21: outside the main camera's default cull mask (layers 1-20).</summary>
	public const uint CaptureLayer = 1u << 20;

	public const int DefaultOutputSize = 256;

	private const string NodeName = "CompositePreviewCapture";
	private const int RenderScale = 2;
	private const float FovDegrees = 40f;
	//Air around the content, so nothing touches the edge of the square
	private const float FramingMargin = 1.08f;
	//A lone billboard or a single point has next to no extent; frame it as if it were this big
	private const float MinimumRadius = 0.05f;
	/* Where the camera stands, relative to the content: first the viewer's own "above and to the
	   front-right" framing offset (LevelViewerView), then, only for a preview that came back blank,
	   the other seven sign variants of it. A single-sided surface facing away from the first camera
	   is culled and shows nothing - a paper on a wall, a number decal, a ceiling light seen from
	   above - and with all eight octants covered, whatever way it faces, one of them looks at its
	   front from no worse than 65 degrees off. Ordered by how often each rescues something: the
	   mirrored side first, then from below for the ceiling-mounted things. A mesh with every face
	   inward, or a transparent effect that draws nothing, is blank from all eight and is reported
	   empty rather than captured as nothing. */
	private static readonly Vector3[] ViewDirections =
	{
		new Vector3(0.85f, 0.55f, 0.85f).Normalized(),
		new Vector3(-0.85f, 0.55f, -0.85f).Normalized(),
		new Vector3(0.85f, 0.55f, -0.85f).Normalized(),
		new Vector3(-0.85f, 0.55f, 0.85f).Normalized(),
		new Vector3(0.85f, -0.55f, 0.85f).Normalized(),
		new Vector3(-0.85f, -0.55f, -0.85f).Normalized(),
		new Vector3(0.85f, -0.55f, -0.85f).Normalized(),
		new Vector3(-0.85f, -0.55f, 0.85f).Normalized(),
	};
	//The ambient light scenes/main.tscn gives the viewport, over nothing instead of the sky
	private static readonly Color AmbientColour = new Color(0.85f, 0.88f, 0.92f);
	private const float AmbientEnergy = 0.35f;

	/// <summary>
	/// Frames to wait after asking for the render before reading the texture back. The mode change is
	/// picked up by the next draw and the readback waits for the GPU, so one is enough (checked by
	/// comparing consecutive previews of different composites; a render not waited for would come
	/// back blank from every direction and show up in <see cref="BlankCaptures"/>).
	/// </summary>
	private const int FramesToAwait = 1;

	/// <summary>
	/// Subtrees that had visuals to draw but gave a blank preview from every one of the eight camera
	/// directions, and were reported empty instead of captured as nothing.
	/// </summary>
	public int BlankCaptures { get; private set; }

	/// <summary>Previews that were blank from the first camera direction and taken from another.</summary>
	public int RescuedCaptures { get; private set; }

	private SubViewport _viewport;
	private Camera3D _camera;
	private readonly List<VisualInstance3D> _tagged = new List<VisualInstance3D>();
	private readonly Stack<Node> _walk = new Stack<Node>();

	/// <summary>The capture node under <paramref name="host"/>, made the first time it is asked for.</summary>
	public static CompositePreviewCapture Ensure(Node host)
	{
		if (host == null || !GodotObject.IsInstanceValid(host))
			return null;

		CompositePreviewCapture existing = host.GetNodeOrNull<CompositePreviewCapture>(NodeName);
		if (existing != null && GodotObject.IsInstanceValid(existing))
			return existing;

		CompositePreviewCapture capture = new CompositePreviewCapture { Name = NodeName };
		host.AddChild(capture);
		return capture;
	}

	/// <summary>
	/// Preview what is drawn under <paramref name="root"/> into <paramref name="pngPath"/>, a square
	/// <paramref name="outputSize"/> pixels across (0 = the default). <paramref name="exclude"/> names
	/// nodes whose subtrees are no part of the preview: overlays that live inside the level's own tree.
	/// </summary>
	public async Task<CompositePreviewStatus> CaptureSubtreeAsync(Node3D root, string pngPath, int outputSize, Func<Node, bool> exclude = null, string label = null)
	{
		if (root == null || !GodotObject.IsInstanceValid(root) || !root.IsInsideTree())
			return CompositePreviewStatus.Failed;

		if (outputSize <= 0)
			outputSize = DefaultOutputSize;

		_tagged.Clear();
		Image image = null;
		Image best = null;
		try
		{
			if (!root.IsVisibleInTree() || !TryCollectVisuals(root, exclude, out Aabb bounds))
				return CompositePreviewStatus.Empty;

			EnsureViewport(outputSize * RenderScale);

			for (int i = 0; i < _tagged.Count; i++)
				_tagged[i].Layers |= CaptureLayer;

			string what = label ?? root.Name.ToString();

			/* The usual side first, and that is the preview when it shows a fair part of the composite.
			   When it shows little or nothing, every other side is tried and the one that covers the most
			   pixels is kept: the first side that shows something may be looking at a flat surface
			   edge-on, and the most pixels means the most face-on view of it. */
			int bestLit = 0;
			int bestDirection = 0;
			for (int direction = 0; direction < ViewDirections.Length; direction++)
			{
				FrameCamera(bounds, ViewDirections[direction]);

				_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
				for (int i = 0; i < FramesToAwait; i++)
					await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

				image?.Dispose();
				image = _viewport.GetTexture().GetImage();
				if (image == null || image.IsEmpty())
				{
					ViewerLog.PrintErr("[Preview] The capture viewport gave no image for " + what + ".");
					return CompositePreviewStatus.Failed;
				}

				Image finished = FinishCapture(image, outputSize);
				image.Dispose();
				image = finished;

				int lit = CountLitPixels(image);
				//The usual side is kept outright when it shows a fair part of the composite. A sliver is not -
				//a single-sided ceiling seen from above is a few trim lines - so the other seven sides get
				//their turn and the most covered one wins, which for the ceiling is its underside.
				if (direction == 0 && lit >= FullViewPixels(outputSize))
				{
					SavePng(image, pngPath);
					return CompositePreviewStatus.Captured;
				}

				if (lit >= MinimumLitPixels && lit > bestLit)
				{
					best?.Dispose();
					best = image;
					image = null;
					bestLit = lit;
					bestDirection = direction;
				}
			}

			if (best == null)
			{
				BlankCaptures++;
				ViewerLog.Print("[Preview detail] " + what + " is blank from every direction and is reported empty: " + DescribeTagged(bounds));
				return CompositePreviewStatus.Empty;
			}

			if (bestDirection != 0)
			{
				RescuedCaptures++;
				//Tagged apart from the batch's own lines, which a driver picks out of the log by "[Preview]"
				ViewerLog.Print("[Preview detail] " + what + " showed little from the usual side and is captured from direction "
					+ bestDirection + " (" + ViewDirections[bestDirection] + ", " + bestLit + " covered pixels).");
			}
			SavePng(best, pngPath);
			return CompositePreviewStatus.Captured;
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Capturing " + root.Name + " failed: " + e);
			return CompositePreviewStatus.Failed;
		}
		finally
		{
			UntagVisuals();
			image?.Dispose();
			best?.Dispose();
		}
	}

	private void EnsureViewport(int renderSize)
	{
		if (_viewport != null && GodotObject.IsInstanceValid(_viewport))
		{
			if (_viewport.Size.X != renderSize || _viewport.Size.Y != renderSize)
				_viewport.Size = new Vector2I(renderSize, renderSize);
			return;
		}

		_viewport = new SubViewport
		{
			Name = "CaptureViewport",
			Size = new Vector2I(renderSize, renderSize),
			//The same world as the viewport on screen: the content is captured in place, never copied
			World3D = GetViewport().FindWorld3D(),
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
			//Cleared to nothing, so the alpha of the render is the content's cover and the square around it is see-through
			TransparentBg = true,
			HandleInputLocally = false,
			GuiDisableInput = true,
			Msaa3D = Viewport.Msaa.Msaa2X,
		};
		AddChild(_viewport);

		_camera = new Camera3D
		{
			Name = "CaptureCamera",
			CullMask = CaptureLayer,
			Fov = FovDegrees,
			KeepAspect = Camera3D.KeepAspectEnum.Height,
			//The viewport's own look (scenes/main.tscn), over nothing instead of the sky
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				//Fully transparent: with the viewport cleared to nothing, this is what every uncovered pixel holds
				BackgroundColor = new Color(0f, 0f, 0f, 0f),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = AmbientColour,
				AmbientLightEnergy = AmbientEnergy,
				TonemapMode = Godot.Environment.ToneMapper.Filmic,
			},
			Current = true,
		};
		_viewport.AddChild(_camera);
	}

	/// <summary>
	/// Every visual drawn under the root, remembered for tagging, and the world box around the lot.
	/// False when nothing under it draws.
	/// </summary>
	private bool TryCollectVisuals(Node3D root, Func<Node, bool> exclude, out Aabb bounds)
	{
		bounds = default;
		bool any = false;
		_walk.Clear();
		_walk.Push(root);
		while (_walk.Count > 0)
		{
			Node node = _walk.Pop();
			if (node == null || !GodotObject.IsInstanceValid(node))
				continue;

			if (node != root && exclude != null && exclude(node))
				continue;

			//A hidden node hides everything under it, so the walk stops there (the root's own visibility was checked)
			if (node != root && node is Node3D node3D && !node3D.Visible)
				continue;

			if (node is VisualInstance3D visual && TryGetGlobalBounds(visual, out Aabb global))
			{
				bounds = any ? bounds.Merge(global) : global;
				any = true;
				_tagged.Add(visual);
			}

			int childCount = node.GetChildCount();
			for (int i = 0; i < childCount; i++)
				_walk.Push(node.GetChild(i));
		}

		return any;
	}

	/* LevelViewerView's rules for a usable box, except that a flat one counts: the camera framing there
	   wants solid geometry, but an icon billboard is a flat quad and a spline a line, and a composite of
	   nothing but lights or triggers should still get a preview of its icons. */
	private static bool TryGetGlobalBounds(VisualInstance3D visual, out Aabb global)
	{
		global = default;
		if (!GodotObject.IsInstanceValid(visual) || !visual.IsInsideTree())
			return false;

		try
		{
			Aabb local;
			if (visual is MeshInstance3D meshInstance)
			{
				Mesh mesh = meshInstance.Mesh;
				if (mesh == null || !GodotObject.IsInstanceValid(mesh))
					return false;
				local = mesh.GetAabb();
			}
			else
			{
				local = visual.GetAabb();
			}

			if (!IsUsableBounds(local))
				return false;

			global = visual.GlobalTransform * local;
			return IsUsableBounds(global);
		}
		catch
		{
			return false;
		}
	}

	private static bool IsUsableBounds(Aabb aabb)
	{
		Vector3 position = aabb.Position;
		Vector3 size = aabb.Size;
		if (size.LengthSquared() <= 0.000001f)
			return false;

		return Mathf.IsFinite(position.X) && Mathf.IsFinite(position.Y) && Mathf.IsFinite(position.Z)
			&& Mathf.IsFinite(size.X) && Mathf.IsFinite(size.Y) && Mathf.IsFinite(size.Z);
	}

	/* The camera looks at the centre of the box from the viewer's usual direction, from as close as still
	   fits every corner of the box in view: for each corner, how far back along the view axis the camera
	   has to be for that corner's sideways offset to fall inside the field of view. A sphere around the
	   box would do, but a room is wide and flat, and a sphere around it leaves most of the square empty. */
	private void FrameCamera(Aabb bounds, Vector3 viewDirection)
	{
		Vector3 centre = bounds.GetCenter();
		Vector3 size = bounds.Size;
		float radius = Mathf.Max(size.Length() * 0.5f, MinimumRadius);
		float halfFov = Mathf.DegToRad(FovDegrees * 0.5f);
		float halfFovTan = Mathf.Tan(halfFov);
		Basis look = Basis.LookingAt(-viewDirection, Vector3.Up);

		float distance = 0f;
		float nearestTowards = float.NegativeInfinity;
		for (int i = 0; i < 8; i++)
		{
			Vector3 corner = bounds.Position + new Vector3(
				(i & 1) == 0 ? 0f : size.X,
				(i & 2) == 0 ? 0f : size.Y,
				(i & 4) == 0 ? 0f : size.Z);
			Vector3 local = corner - centre;
			float towards = local.Dot(viewDirection);
			float sideways = Mathf.Max(Mathf.Abs(local.Dot(look.X)), Mathf.Abs(local.Dot(look.Y)));
			distance = Mathf.Max(distance, towards + sideways / halfFovTan);
			nearestTowards = Mathf.Max(nearestTowards, towards);
		}

		distance = Mathf.Max(distance, MinimumRadius / Mathf.Sin(halfFov)) * FramingMargin;

		_camera.GlobalPosition = centre + viewDirection * distance;
		_camera.LookAt(centre, Vector3.Up);
		_camera.Near = Mathf.Max(0.02f, (distance - nearestTowards) * 0.5f);
		_camera.Far = distance + radius * 2f + 1f;
	}

	private void UntagVisuals()
	{
		for (int i = 0; i < _tagged.Count; i++)
		{
			VisualInstance3D visual = _tagged[i];
			if (visual != null && GodotObject.IsInstanceValid(visual))
				visual.Layers &= ~CaptureLayer;
		}

		_tagged.Clear();
	}

	/// <summary>
	/// The finished preview from the render: brought down to <paramref name="outputSize"/> square, with
	/// straight colour and alpha as a PNG holds them.
	/// </summary>
	/// <remarks>
	/// The render's colour is premultiplied - each pixel's colour is the content's colour times the
	/// pixel's alpha - because the renderer averages its samples over the clear of (0,0,0,0) along every
	/// edge, and blends a transparent material over the same nothing. It is also tonemapped and sRGB
	/// encoded, the capture environment tonemapping as the viewport does. So: decode the bytes back to
	/// linear light (a table over the 256 values undoes both curves), scale the four channels as they
	/// stand - premultiplied colour averages correctly, a half-covered edge pixel keeping half the colour
	/// and half the alpha - then divide the colour by the alpha, which gives the straight colour the file
	/// wants and whoever draws the preview blends with, and encode again. A pixel with no cover is left
	/// at zero rather than the noise the division would make of it.
	/// </remarks>
	private static Image FinishCapture(Image render, int outputSize)
	{
		if (render.GetFormat() != Image.Format.Rgba8)
			render.Convert(Image.Format.Rgba8);

		int width = render.GetWidth();
		int height = render.GetHeight();
		byte[] bytes = render.GetData();
		float[] linear = new float[width * height * 4];
		for (int p = 0; p < linear.Length; p += 4)
		{
			linear[p] = ByteToLinear[bytes[p]];
			linear[p + 1] = ByteToLinear[bytes[p + 1]];
			linear[p + 2] = ByteToLinear[bytes[p + 2]];
			linear[p + 3] = bytes[p + 3] * (1f / 255f);
		}

		if (width != outputSize || height != outputSize)
		{
			byte[] floats = new byte[linear.Length * sizeof(float)];
			Buffer.BlockCopy(linear, 0, floats, 0, floats.Length);
			using Image working = Image.CreateFromData(width, height, false, Image.Format.Rgbaf, floats);
			working.Resize(outputSize, outputSize, Image.Interpolation.Lanczos);
			byte[] scaled = working.GetData();
			linear = new float[outputSize * outputSize * 4];
			Buffer.BlockCopy(scaled, 0, linear, 0, scaled.Length);
		}

		byte[] result = new byte[linear.Length];
		for (int p = 0; p < linear.Length; p += 4)
		{
			//The filter overshoots either side of a hard edge, and nothing covers less than none of a pixel or more than all of it
			float alpha = Mathf.Clamp(linear[p + 3], 0f, 1f);
			byte cover = (byte)Mathf.RoundToInt(alpha * 255f);
			if (cover == 0)
				continue;

			float inverse = 1f / alpha;
			result[p] = LinearToByte(linear[p] * inverse);
			result[p + 1] = LinearToByte(linear[p + 1] * inverse);
			result[p + 2] = LinearToByte(linear[p + 2] * inverse);
			result[p + 3] = cover;
		}

		return Image.CreateFromData(outputSize, outputSize, false, Image.Format.Rgba8, result);
	}

	/* What a byte of the render means in linear light, and the way back. The renderer draws in linear
	   light, tonemaps (the capture environment's Filmic, as the viewport's) and sRGB-encodes what it
	   hands back; the scaling and the un-premultiplying in FinishCapture need the linear value, so both
	   curves are undone by this table and done again by LinearToByte. The tonemap is Godot's
	   tonemap_filmic (Uncharted 2's operator with its exposure bias of 2) at the default white of 1,
	   inverted by bisection since it rises from 0 at 0 to 1 at 1. Were the curve ever to differ from the
	   renderer's, a fully covered pixel would still come back as it went in (the two here are exact
	   inverses of each other); only the colour along an edge would be off by the difference. */
	private static readonly float[] ByteToLinear = BuildByteToLinear();

	private static float[] BuildByteToLinear()
	{
		float[] table = new float[256];
		for (int value = 0; value < 256; value++)
		{
			float target = SrgbToLinear(value / 255f);
			float low = 0f;
			float high = 1f;
			for (int i = 0; i < 40; i++)
			{
				float mid = (low + high) * 0.5f;
				if (Filmic(mid) < target)
					low = mid;
				else
					high = mid;
			}

			table[value] = (low + high) * 0.5f;
		}

		return table;
	}

	private static byte LinearToByte(float linear)
	{
		float encoded = LinearToSrgb(Filmic(Mathf.Clamp(linear, 0f, 1f)));
		return (byte)Mathf.Clamp(Mathf.RoundToInt(encoded * 255f), 0, 255);
	}

	private static float Filmic(float x)
	{
		const float A = 0.22f * 2f * 2f;
		const float B = 0.30f * 2f;
		const float C = 0.10f;
		const float D = 0.20f;
		const float E = 0.01f;
		const float F = 0.30f;
		const float White = 1f;
		float mapped = (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
		float white = (White * (A * White + C * B) + D * E) / (White * (A * White + B) + D * F) - E / F;
		return mapped / white;
	}

	private static float SrgbToLinear(float value)
	{
		return value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);
	}

	private static float LinearToSrgb(float value)
	{
		return value <= 0.0031308f ? value * 12.92f : 1.055f * Mathf.Pow(value, 1f / 2.4f) - 0.055f;
	}

	/* A pixel counts as covered once the content covers this much of it (alpha, out of 255): an
	   anti-aliased edge runs through a pixel or two of partial cover and the scaling filter rings a
	   pixel or two beyond a hard edge, and neither is a preview of anything. */
	private const int LitAlphaThreshold = 16;

	/* And a preview needs this many covered pixels (a tenth of a percent of a 256-square) before it
	   counts as showing something. Set when a preview was judged by its brightness on black - a black
	   container lit one pixel, the dimmest real thing (a dark VDU) 421 - and kept as it was: judged by
	   cover, anything with a face to the camera clears it by far, and what stays under it is a sliver
	   seen edge-on. A black object on the transparent square is a preview now. */
	private const int MinimumLitPixels = 64;

	/* The usual side is taken without looking further once it covers this much of the square (two
	   percent: 1,310 pixels of a 256-square). Under that it is most likely a face-on surface seen
	   edge-on - on BSP_TORRENS 17 of 928 captures covered less, ceilings and trim among them - and the
	   other sides are worth the seven extra frames. */
	private static int FullViewPixels(int outputSize) => outputSize * outputSize / 50;

	//How many pixels of the finished preview (RGBA8, so every fourth byte is the alpha) the content covers
	private static int CountLitPixels(Image image)
	{
		byte[] data = image.GetData();
		int lit = 0;
		for (int i = 3; i < data.Length; i += 4)
		{
			if (data[i] > LitAlphaThreshold)
				lit++;
		}

		return lit;
	}

	//What was tagged for a preview that came back blank, for the log: enough to see what kind of thing drew nothing
	private string DescribeTagged(Aabb bounds)
	{
		const int MostListed = 6;
		System.Text.StringBuilder text = new System.Text.StringBuilder();
		text.Append(_tagged.Count).Append(" visual(s), bounds at ").Append(bounds.Position).Append(" size ").Append(bounds.Size)
			.Append(", camera at ").Append(_camera.GlobalPosition).Append(" near ").Append(_camera.Near.ToString("0.###"))
			.Append(" far ").Append(_camera.Far.ToString("0.#"));
		for (int i = 0; i < _tagged.Count && i < MostListed; i++)
		{
			VisualInstance3D visual = _tagged[i];
			if (visual == null || !GodotObject.IsInstanceValid(visual))
				continue;

			text.Append("; [").Append(i).Append("] ").Append(visual.GetParent()?.Name).Append('/').Append(visual.Name)
				.Append(' ').Append(visual.GetType().Name).Append(" layers ").Append(visual.Layers)
				.Append(visual.IsVisibleInTree() ? "" : " HIDDEN");
			if (visual is GeometryInstance3D geometry)
			{
				text.Append(" range ").Append(geometry.VisibilityRangeBegin.ToString("0.#")).Append('-').Append(geometry.VisibilityRangeEnd.ToString("0.#"))
					.Append(" transparency ").Append(geometry.Transparency.ToString("0.##"));
				Material material = geometry.MaterialOverride;
				if (material is ShaderMaterial shaderMaterial && shaderMaterial.Shader != null)
					text.Append(" shader ").Append(shaderMaterial.Shader.ResourcePath);
				else if (material != null)
					text.Append(" material ").Append(material.GetType().Name);
			}
			if (visual is MeshInstance3D meshInstance && meshInstance.Mesh != null)
				text.Append(" mesh ").Append(meshInstance.Mesh.ResourceName).Append(" surfaces ").Append(meshInstance.Mesh.GetSurfaceCount())
					.Append(" aabb ").Append(meshInstance.Mesh.GetAabb().Size).Append(" custom ").Append(meshInstance.CustomAabb.Size);
			text.Append(" global ").Append(visual.GlobalTransform.Origin).Append(" scale ").Append(visual.GlobalTransform.Basis.Scale);
		}

		return text.ToString();
	}

	private static void SavePng(Image image, string pngPath)
	{
		string directory = Path.GetDirectoryName(pngPath);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		if (image.SavePng(pngPath.Replace('\\', '/')) == Error.Ok)
			return;

		//Godot's own file layer refused the path; the bytes are the same either way
		File.WriteAllBytes(pngPath, image.SavePngToBuffer());
	}
}
