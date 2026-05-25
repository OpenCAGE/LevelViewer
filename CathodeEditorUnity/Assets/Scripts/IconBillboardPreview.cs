using CATHODE.Scripting;
using UnityEngine;

/// <summary>
/// Lightweight preview using Unity component icons on camera-facing billboards.
/// </summary>
public class IconBillboardPreview : FunctionEntityPreview
{
    public enum IconKind
    {
        Sound,
        Light,
        Particle,
        Camera,
    }

    private IconKind _iconKind;
    private bool _visible;
    private GameObject _billboard;

    public void Setup(FunctionEntity entity, IconKind iconKind, uint ownerCompositeId = 0)
    {
        _iconKind = iconKind;
        base.Setup(entity, ownerCompositeId);
    }

    protected override GameObject GetVisibilityRoot() => _billboard;

    public override void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyObject(_billboard);
        _billboard = null;
    }

    public override void Refresh()
    {
        if (Entity == null)
            return;

        _visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (SyncVisibility(_visible, _billboard))
            return;

        EnsureBillboard();
    }

    private void EnsureBillboard()
    {
        if (_billboard != null)
            return;

        Texture2D icon = EditorIconTextures.Get(_iconKind);
        if (icon == null)
            return;

        _billboard = PreviewVisualUtility.CreateIconBillboard("IconBillboard", transform, icon);
    }
}

internal static class EditorIconTextures
{
    private static Texture2D _sound;
    private static Texture2D _light;
    private static Texture2D _particle;
    private static Texture2D _camera;
    private static bool _loaded;

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

#if UNITY_EDITOR
        _sound = LoadComponentIcon(typeof(UnityEngine.AudioSource));
        _light = LoadComponentIcon(typeof(UnityEngine.Light));
        _particle = LoadComponentIcon(typeof(UnityEngine.ParticleSystem));
        _camera = LoadComponentIcon(typeof(UnityEngine.Camera));
#endif
        _loaded = true;
    }

#if UNITY_EDITOR
    private static Texture2D LoadComponentIcon(System.Type componentType)
    {
        GUIContent content = UnityEditor.EditorGUIUtility.ObjectContent(null, componentType);
        if (content?.image is Texture2D texture)
            return texture;

        string typeName = componentType.Name;
        string[] names =
        {
            typeName + " Icon",
            "d_" + typeName + " Icon",
        };

        for (int i = 0; i < names.Length; i++)
        {
            content = UnityEditor.EditorGUIUtility.IconContent(names[i]);
            if (content?.image is Texture2D fallback)
                return fallback;
        }

        return null;
    }
#endif
}
