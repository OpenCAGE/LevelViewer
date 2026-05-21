using CATHODE.Scripting;
using UnityEngine;

public class CharacterPreview : FunctionEntityPreview
{
    private static readonly Color CharacterColor = new Color(0.25f, 0.85f, 0.35f, 1f);

    private const float BodyRadius = 0.28f;
    private const float BodyHeight = 1.55f;
    private const float HeadRadius = 0.18f;
    private const float HeadOffset = 1.65f;

    private GameObject _root;
    private MaterialPropertyBlock _propertyBlock;

    public override void Refresh()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (_root != null)
            _root.SetActive(visible);
        if (!visible)
            return;

        EnsureVisual();

        for (int i = 0; i < _root.transform.childCount; i++)
        {
            MeshRenderer renderer = _root.transform.GetChild(i).GetComponent<MeshRenderer>();
            if (renderer != null)
                PreviewVisualUtility.ApplyColor(renderer, CharacterColor, ref _propertyBlock, opaque: true);
        }
    }

    private void EnsureVisual()
    {
        if (_root != null)
            return;

        _root = new GameObject("CharacterPreview");
        _root.transform.SetParent(transform, false);

        GameObject body = PreviewVisualUtility.CreatePrimitivePreview("Body", _root.transform, PrimitiveType.Capsule, CharacterColor, ref _propertyBlock, opaque: true);
        body.transform.localPosition = new Vector3(0f, BodyHeight * 0.5f, 0f);
        body.transform.localScale = new Vector3(BodyRadius * 2f, BodyHeight * 0.5f, BodyRadius * 2f);

        GameObject head = PreviewVisualUtility.CreatePrimitivePreview("Head", _root.transform, PrimitiveType.Sphere, CharacterColor, ref _propertyBlock, opaque: true);
        head.transform.localPosition = new Vector3(0f, HeadOffset, 0f);
        head.transform.localScale = Vector3.one * (HeadRadius * 2f);
    }
}
