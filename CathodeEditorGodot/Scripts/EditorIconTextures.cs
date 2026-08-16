using Godot;

/// <summary>
/// High-resolution icons for sound / light / particle / camera billboards.
/// Uses bundled PNGs when present, otherwise generates 256px Unity-style silhouettes.
/// </summary>
internal static class EditorIconTextures
{
	public const int IconResolution = 256;

	private static Texture2D _sound;
	private static Texture2D _soundObject;
	private static Texture2D _light;
	private static Texture2D _particle;
	private static Texture2D _camera;
	private static Texture2D _uiIcon;
	private static bool _loaded;

	private const string SoundResourcePath = "res://textures/preview_icons/sound.png";
	private const string SoundObjectResourcePath = "res://textures/preview_icons/sound_obj.png";
	private const string LightResourcePath = "res://textures/preview_icons/light.png";
	private const string ParticleResourcePath = "res://textures/preview_icons/particle.png";
	private const string CameraResourcePath = "res://textures/preview_icons/camera.png";
	private const string UIIconResourcePath = "res://textures/preview_icons/button_a.png";

	public static Texture2D Get(IconBillboardPreview.IconKind kind)
	{
		EnsureLoaded();
		switch (kind)
		{
			case IconBillboardPreview.IconKind.Sound:
				return _sound;
			case IconBillboardPreview.IconKind.SoundObject:
				return _soundObject;
			case IconBillboardPreview.IconKind.Light:
				return _light;
			case IconBillboardPreview.IconKind.Particle:
				return _particle;
			case IconBillboardPreview.IconKind.Camera:
				return _camera;
			case IconBillboardPreview.IconKind.UIIcon:
				return _uiIcon;
			default:
				return null;
		}
	}

	private static void EnsureLoaded()
	{
		if (_loaded)
			return;

		_sound = TryLoadResourceIcon(SoundResourcePath);
		_soundObject = TryLoadResourceIcon(SoundObjectResourcePath);
		_light = TryLoadResourceIcon(LightResourcePath);
		_particle = TryLoadResourceIcon(ParticleResourcePath);
		_camera = TryLoadResourceIcon(CameraResourcePath);
		_uiIcon = TryLoadResourceIcon(UIIconResourcePath);
		_loaded = true;
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
}
