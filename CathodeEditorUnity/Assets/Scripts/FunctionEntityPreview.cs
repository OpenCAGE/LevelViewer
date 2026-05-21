using CATHODE.Scripting;
using UnityEngine;

/// <summary>
/// Base for function-entity scene previews. Add a derived component per FunctionType that needs a visual.
/// </summary>
public abstract class FunctionEntityPreview : MonoBehaviour
{
    public FunctionEntity Entity { get; private set; }
    public uint OwnerCompositeId { get; private set; }

    public virtual void Setup(FunctionEntity entity, uint ownerCompositeId = 0)
    {
        Entity = entity;
        OwnerCompositeId = ownerCompositeId;
        Refresh();
    }

    public abstract void Refresh();

    /// <summary>
    /// Fast path for hide-nested / active-composite changes — toggles visibility without rebuilding visuals.
    /// </summary>
    public virtual void RefreshVisibility()
    {
        if (Entity == null)
            return;

        bool visible = PreviewVisualUtility.IsPreviewVisible(Entity, OwnerCompositeId);
        if (SyncVisibility(visible, GetVisibilityRoot()))
            return;

        if (visible)
            Refresh();
    }

    protected abstract GameObject GetVisibilityRoot();

    /// <summary>
    /// Returns true when no further refresh work is needed.
    /// </summary>
    protected bool SyncVisibility(bool visible, GameObject root)
    {
        if (root == null)
            return !visible;

        if (root.activeSelf == visible && visible)
            return true;

        root.SetActive(visible);
        return !visible;
    }
}
