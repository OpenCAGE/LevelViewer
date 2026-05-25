using Godot;

/// <summary>
/// Icons for sound / light / particle billboards. Prefers Godot editor node icons when running from the editor;
/// falls back to bundled textures under res://textures/preview_icons/.
/// </summary>
internal static class EditorIconTextures
{
    private const string IconThemeType = "EditorIcons";

    private static Texture2D _sound;
    private static Texture2D _light;
    private static Texture2D _particle;
    private static bool _loaded;

    private static readonly string[] SoundEditorIconNames = { "AudioStreamPlayer3D", "AudioStreamPlayer" };
    private static readonly string[] LightEditorIconNames = { "OmniLight3D", "SpotLight3D", "DirectionalLight3D", "Light3D" };
    private static readonly string[] ParticleEditorIconNames = { "GPUParticles3D", "CPUParticles3D", "Particles3D" };

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
            default:
                return null;
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;

        _sound = LoadIcon(IconBillboardPreview.IconKind.Sound, SoundEditorIconNames, "res://textures/preview_icons/sound.png");
        _light = LoadIcon(IconBillboardPreview.IconKind.Light, LightEditorIconNames, "res://textures/preview_icons/light.png");
        _particle = LoadIcon(IconBillboardPreview.IconKind.Particle, ParticleEditorIconNames, "res://textures/preview_icons/particle.png");
        _loaded = true;
    }

    private static Texture2D LoadIcon(IconBillboardPreview.IconKind kind, string[] editorIconNames, string resourcePath)
    {
        Texture2D icon = TryLoadGodotEditorIcons(editorIconNames);
        if (icon != null)
            return icon;

        icon = TryLoadResourceIcon(resourcePath);
        if (icon != null)
            return MakeDarkBackgroundTransparent(icon);

        return CreateGizmoFallbackIcon(kind);
    }

#if TOOLS
    private static Texture2D TryLoadGodotEditorIcons(string[] iconNames)
    {
        if (!OS.HasFeature("editor"))
            return null;

        EditorInterface editor = EditorInterface.Singleton;
        if (editor == null)
            return null;

        Theme theme = editor.GetEditorTheme();
        if (theme == null)
            return null;

        for (int i = 0; i < iconNames.Length; i++)
        {
            StringName iconName = iconNames[i];
            if (!theme.HasIcon(iconName, IconThemeType))
                continue;

            Texture2D themeIcon = theme.GetIcon(iconName, IconThemeType);
            Texture2D copy = CopyThemeIcon(themeIcon);
            if (copy != null)
                return copy;
        }

        return null;
    }
#else
    private static Texture2D TryLoadGodotEditorIcons(string[] iconNames) => null;
#endif

    private static Texture2D TryLoadResourceIcon(string resourcePath)
    {
        if (!ResourceLoader.Exists(resourcePath))
            return null;

        Texture2D texture = ResourceLoader.Load<Texture2D>(resourcePath);
        return texture != null ? CopyThemeIcon(texture) : null;
    }

    private static Texture2D CopyThemeIcon(Texture2D source)
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

    private static Texture2D MakeDarkBackgroundTransparent(Texture2D texture)
    {
        Image image = texture.GetImage();
        if (image == null || image.IsEmpty())
            return texture;

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

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>Simple gizmo-style icons when editor theme and PNG fallbacks are unavailable.</summary>
    private static Texture2D CreateGizmoFallbackIcon(IconBillboardPreview.IconKind kind)
    {
        const int size = 64;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);

        switch (kind)
        {
            case IconBillboardPreview.IconKind.Sound:
            {
                Color orange = new Color(1f, 0.72f, 0.18f);
                image.FillRect(new Rect2I(10, 24, 10, 16), orange);
                image.FillRect(new Rect2I(20, 20, 8, 24), orange);
                image.FillRect(new Rect2I(28, 16, 8, 32), orange);
                image.FillRect(new Rect2I(36, 22, 6, 20), orange);
                break;
            }
            case IconBillboardPreview.IconKind.Light:
            {
                Color yellow = new Color(1f, 0.9f, 0.3f);
                Color baseColor = new Color(0.82f, 0.82f, 0.82f);
                FillFilledCircle(image, new Vector2I(32, 26), 15, yellow);
                image.FillRect(new Rect2I(26, 40, 12, 12), baseColor);
                image.FillRect(new Rect2I(28, 50, 8, 4), baseColor);
                break;
            }
            case IconBillboardPreview.IconKind.Particle:
            {
                Color cyan = new Color(0.35f, 0.85f, 1f);
                FillFilledCircle(image, new Vector2I(22, 20), 6, cyan);
                FillFilledCircle(image, new Vector2I(42, 20), 6, cyan);
                FillFilledCircle(image, new Vector2I(28, 32), 7, cyan);
                FillFilledCircle(image, new Vector2I(38, 32), 7, cyan);
                FillFilledCircle(image, new Vector2I(32, 46), 9, cyan);
                break;
            }
        }

        return ImageTexture.CreateFromImage(image);
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
