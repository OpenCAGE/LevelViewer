using Godot;

/// <summary>
/// High-resolution icons for sound / light / particle / camera billboards.
/// Uses bundled PNGs when present, otherwise generates 256px Unity-style silhouettes.
/// </summary>
internal static class EditorIconTextures
{
	public const int IconResolution = 256;

	private static Texture2D _sound;
	private static Texture2D _light;
	private static Texture2D _particle;
	private static Texture2D _camera;
	private static bool _loaded;

	private const string SoundResourcePath = "res://textures/preview_icons/sound.png";
	private const string LightResourcePath = "res://textures/preview_icons/light.png";
	private const string ParticleResourcePath = "res://textures/preview_icons/particle.png";
	private const string CameraResourcePath = "res://textures/preview_icons/camera.png";

	public static Texture2D Get(IconBillboardPreview.IconKind kind)
	{
		EnsureLoaded();
		switch (kind)
		{
			case IconBillboardPreview.IconKind.Sound:
				return _sound;
			case IconBillboardPreview.IconKind.Light:
				return _light;
			case IconBillboardPreview.IconKind.Particle:
				return _particle;
			case IconBillboardPreview.IconKind.Camera:
				return _camera;
			default:
				return null;
		}
	}

	private static void EnsureLoaded()
	{
		if (_loaded)
			return;

		_sound = LoadIcon(IconBillboardPreview.IconKind.Sound, SoundResourcePath);
		_light = LoadIcon(IconBillboardPreview.IconKind.Light, LightResourcePath);
		_particle = LoadIcon(IconBillboardPreview.IconKind.Particle, ParticleResourcePath);
		_camera = LoadIcon(IconBillboardPreview.IconKind.Camera, CameraResourcePath);
		_loaded = true;
	}

	private static Texture2D LoadIcon(IconBillboardPreview.IconKind kind, string resourcePath)
	{
		Texture2D icon = TryLoadResourceIcon(resourcePath);
		if (icon != null)
			return icon;

		return CreateHighResIcon(kind);
	}

	private static Texture2D TryLoadResourceIcon(string resourcePath)
	{
		if (!ResourceLoader.Exists(resourcePath))
			return null;

		Texture2D texture = ResourceLoader.Load<Texture2D>(resourcePath);
		if (texture == null)
			return null;

		return PrepareIconTexture(CopyToImageTexture(texture));
	}

	private static Texture2D CopyToImageTexture(Texture2D source)
	{
		if (source == null)
			return null;

		if (source is AtlasTexture atlas)
		{
			Texture2D atlasSource = atlas.Atlas;
			if (atlasSource == null)
				return null;

			Image atlasImage = atlasSource.GetImage();
			if (atlasImage == null || atlasImage.IsEmpty())
				return null;

			Rect2 region = atlas.GetRegion();
			var regionRect = new Rect2I(
				(int)region.Position.X,
				(int)region.Position.Y,
				(int)region.Size.X,
				(int)region.Size.Y);
			Image iconImage = atlasImage.GetRegion(regionRect);
			if (iconImage == null || iconImage.IsEmpty())
				return null;

			return ImageTexture.CreateFromImage(iconImage);
		}

		Image image = source.GetImage();
		if (image != null && !image.IsEmpty())
			return ImageTexture.CreateFromImage(image);

		return source;
	}

	private static Texture2D PrepareIconTexture(Texture2D texture)
	{
		if (texture == null)
			return null;

		Image image = texture.GetImage();
		if (image == null || image.IsEmpty())
			return texture;

		image = MakeDarkBackgroundTransparent(image);

		int width = image.GetWidth();
		int height = image.GetHeight();
		if (width < IconResolution || height < IconResolution)
		{
			Image.Interpolation filter = width <= 32
				? Image.Interpolation.Nearest
				: Image.Interpolation.Lanczos;
			image.Resize(IconResolution, IconResolution, filter);
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static Image MakeDarkBackgroundTransparent(Image image)
	{
		image.Convert(Image.Format.Rgba8);
		int width = image.GetWidth();
		int height = image.GetHeight();
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				Color pixel = image.GetPixel(x, y);
				if (pixel.R < 0.08f && pixel.G < 0.08f && pixel.B < 0.08f)
					image.SetPixel(x, y, Colors.Transparent);
			}
		}

		return image;
	}

	/// <summary>256px Unity-style editor silhouettes (AudioSource, Light, ParticleSystem, Camera).</summary>
	private static Texture2D CreateHighResIcon(IconBillboardPreview.IconKind kind)
	{
		Image image = Image.CreateEmpty(IconResolution, IconResolution, false, Image.Format.Rgba8);
		image.Fill(Colors.Transparent);

		switch (kind)
		{
			case IconBillboardPreview.IconKind.Sound:
				DrawSoundIcon(image);
				break;
			case IconBillboardPreview.IconKind.Light:
				DrawLightIcon(image);
				break;
			case IconBillboardPreview.IconKind.Particle:
				DrawParticleIcon(image);
				break;
			case IconBillboardPreview.IconKind.Camera:
				DrawVideoCameraIcon(image);
				break;
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static void DrawSoundIcon(Image image)
	{
		Color orange = new Color(1f, 0.72f, 0.18f);
		image.FillRect(new Rect2I(52, 108, 40, 40), orange);
		image.FillRect(new Rect2I(92, 92, 32, 72), orange);
		image.FillRect(new Rect2I(124, 72, 32, 112), orange);
		image.FillRect(new Rect2I(156, 96, 28, 64), orange);
		int[] waveHeights = { 28, 44, 60, 44 };
		for (int i = 0; i < waveHeights.Length; i++)
		{
			int h = waveHeights[i];
			int x = 196 + i * 14;
			image.FillRect(new Rect2I(x, 128 - h / 2, 10, h), orange);
		}
	}

	private static void DrawLightIcon(Image image)
	{
		Color yellow = new Color(1f, 0.9f, 0.3f);
		Color baseColor = new Color(0.82f, 0.82f, 0.82f);
		FillFilledCircle(image, new Vector2I(128, 104), 56, yellow);
		image.FillRect(new Rect2I(104, 156, 48, 40), baseColor);
		image.FillRect(new Rect2I(112, 196, 32, 16), baseColor);
	}

	private static void DrawParticleIcon(Image image)
	{
		Color cyan = new Color(0.35f, 0.85f, 1f);
		FillFilledCircle(image, new Vector2I(88, 80), 22, cyan);
		FillFilledCircle(image, new Vector2I(168, 80), 22, cyan);
		FillFilledCircle(image, new Vector2I(108, 128), 26, cyan);
		FillFilledCircle(image, new Vector2I(148, 128), 26, cyan);
		FillFilledCircle(image, new Vector2I(128, 176), 32, cyan);
	}

	private static void DrawVideoCameraIcon(Image image)
	{
		Color grey = new Color(0.76f, 0.76f, 0.76f);
		Color lens = new Color(0.24f, 0.24f, 0.24f);
		image.FillRect(new Rect2I(40, 72, 112, 112), grey);
		FillPolygon(image, new Vector2[]
		{
			new Vector2(168, 88),
			new Vector2(220, 64),
			new Vector2(220, 192),
			new Vector2(168, 168),
		}, grey);
		image.FillRect(new Rect2I(56, 88, 80, 80), lens);
	}

#if TOOLS
	/// <summary>
	/// Bakes Godot editor theme icons to 256px PNGs under textures/preview_icons/.
	/// Run once from the editor debugger or a tool script after updating icon sources.
	/// </summary>
	public static void BakeEditorThemeIconsToProject()
	{
		if (!Engine.IsEditorHint())
		{
			GD.PrintErr("BakeEditorThemeIconsToProject requires the Godot editor.");
			return;
		}

		EditorInterface editor = EditorInterface.Singleton;
		Theme theme = editor?.GetEditorTheme();
		if (theme == null)
		{
			GD.PrintErr("Editor theme unavailable.");
			return;
		}

		BakeThemeIcon(theme, "AudioStreamPlayer3D", ProjectSettings.GlobalizePath("res://textures/preview_icons/sound.png"));
		BakeThemeIcon(theme, "OmniLight3D", ProjectSettings.GlobalizePath("res://textures/preview_icons/light.png"));
		BakeThemeIcon(theme, "GPUParticles3D", ProjectSettings.GlobalizePath("res://textures/preview_icons/particle.png"));
		BakeThemeIcon(theme, "Camera3D", ProjectSettings.GlobalizePath("res://textures/preview_icons/camera.png"));
		editor.GetResourceFilesystem().Scan();
		GD.Print("Preview icons baked to textures/preview_icons/");
	}

	private static void BakeThemeIcon(Theme theme, string iconName, string absolutePath)
	{
		if (!theme.HasIcon(iconName, "EditorIcons"))
			return;

		Texture2D themeIcon = theme.GetIcon(iconName, "EditorIcons");
		Texture2D copy = CopyToImageTexture(themeIcon);
		if (copy == null)
			return;

		Image image = copy.GetImage();
		if (image == null || image.IsEmpty())
			return;

		image = MakeDarkBackgroundTransparent(image);
		image.Resize(IconResolution, IconResolution, Image.Interpolation.Nearest);
		Error err = image.SavePng(absolutePath);
		if (err != Error.Ok)
			GD.PrintErr("Failed to save " + absolutePath + ": " + err);
	}
#endif

	private static void FillPolygon(Image image, Vector2[] points, Color color)
	{
		if (points.Length < 3)
			return;

		int minY = (int)points[0].Y;
		int maxY = (int)points[0].Y;
		for (int i = 1; i < points.Length; i++)
		{
			minY = Mathf.Min(minY, (int)points[i].Y);
			maxY = Mathf.Max(maxY, (int)points[i].Y);
		}

		minY = Mathf.Clamp(minY, 0, image.GetHeight() - 1);
		maxY = Mathf.Clamp(maxY, 0, image.GetHeight() - 1);

		for (int y = minY; y <= maxY; y++)
		{
			float scanY = y + 0.5f;
			var intersections = new System.Collections.Generic.List<float>();
			for (int i = 0; i < points.Length; i++)
			{
				Vector2 a = points[i];
				Vector2 b = points[(i + 1) % points.Length];
				if ((a.Y <= scanY && b.Y > scanY) || (b.Y <= scanY && a.Y > scanY))
				{
					float t = (scanY - a.Y) / (b.Y - a.Y);
					intersections.Add(a.X + t * (b.X - a.X));
				}
			}

			if (intersections.Count < 2)
				continue;

			intersections.Sort();
			for (int i = 0; i + 1 < intersections.Count; i += 2)
			{
				int startX = Mathf.Clamp((int)Mathf.Floor(intersections[i]), 0, image.GetWidth() - 1);
				int endX = Mathf.Clamp((int)Mathf.Ceil(intersections[i + 1]), 0, image.GetWidth() - 1);
				for (int x = startX; x <= endX; x++)
					image.SetPixel(x, y, color);
			}
		}
	}

	private static void FillFilledCircle(Image image, Vector2I center, int radius, Color color)
	{
		int radiusSquared = radius * radius;
		int minX = Mathf.Max(0, center.X - radius);
		int maxX = Mathf.Min(image.GetWidth() - 1, center.X + radius);
		int minY = Mathf.Max(0, center.Y - radius);
		int maxY = Mathf.Min(image.GetHeight() - 1, center.Y + radius);

		for (int y = minY; y <= maxY; y++)
		{
			int dy = y - center.Y;
			int dySquared = dy * dy;
			for (int x = minX; x <= maxX; x++)
			{
				int dx = x - center.X;
				if (dx * dx + dySquared <= radiusSquared)
					image.SetPixel(x, y, color);
			}
		}
	}
}
