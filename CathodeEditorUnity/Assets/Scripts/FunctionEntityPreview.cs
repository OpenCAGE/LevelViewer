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
}
