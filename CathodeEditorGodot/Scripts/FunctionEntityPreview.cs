using CATHODE.Scripting;
using Godot;

/// <summary>
/// Base for function-entity scene previews. Add a derived node per FunctionType that needs a visual.
/// </summary>
public abstract partial class FunctionEntityPreview : Node3D
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

    protected abstract Node3D GetVisibilityRoot();

    /// <summary>Root object created by the preview, if any (used for cleanup guards).</summary>
    public Node3D PreviewVisualRoot => GetVisibilityRoot();

    /// <summary>
    /// Destroys spawned preview geometry. Called when the preview node or scene is torn down.
    /// </summary>
    public virtual void CleanupPreviewVisuals()
    {
        PreviewVisualUtility.DestroyNode(GetVisibilityRoot());
    }

    public override void _ExitTree()
    {
        CleanupPreviewVisuals();
        base._ExitTree();
    }

    /// <summary>
    /// Returns true when no further refresh work is needed.
    /// </summary>
    protected bool SyncVisibility(bool visible, Node3D root)
    {
        if (root == null)
            return !visible;

        if (root.Visible == visible && visible)
            return true;

        root.Visible = visible;
        return !visible;
    }
}
