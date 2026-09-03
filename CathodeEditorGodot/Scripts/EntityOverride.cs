using Godot;

public partial class EntityOverride : Node3D
{
    private Node3D _pointedEntity;

    /// <summary>
    /// Raised after <see cref="PointedEntity"/> changes, with the previous target. AlienScene uses it to keep its
    /// render-target -> alias index current as aliases are wired, so an index miss can be trusted without scanning
    /// every node in the scene.
    /// </summary>
    public static System.Action<EntityOverride, Node3D> PointedEntityChanged;

    public Node3D PointedEntity
    {
        get => _pointedEntity;
        set
        {
            if (ReferenceEquals(_pointedEntity, value))
                return;

            Node3D previous = _pointedEntity;
            _pointedEntity = value;
            PointedEntityChanged?.Invoke(this, previous);
        }
    }
}
