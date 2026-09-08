using Godot;
using OpenCAGE.UnityConnection;
using System.Collections.Generic;

/// <summary>
/// Animation Mode's preview: the values a CAGEAnimation being edited in OpenCAGE holds at its
/// playhead, painted on top of the scene.
/// </summary>
/// <remarks>
/// Nothing here writes to the level. The transforms are held on the scene nodes only, with what each
/// one was before kept beside it, so leaving the mode - or a target simply dropping out of the set -
/// puts it back exactly where it was. That is what makes the mode safe to leave at any point: the
/// entity's real position is the one it had all along, and the animation is the only thing that moved.
///
/// Only transforms. A CAGEAnimation can drive any float parameter, but everything else would have to
/// be written into the entity for the viewer to show it, which is the one thing this must not do.
/// </remarks>
public static class AnimationPreview
{
    private class Held
    {
        public Node3D Node;
        public Vector3 Position;
        public Vector3 RotationDegrees;
    }

    private static readonly Dictionary<ulong, Held> _held = new Dictionary<ulong, Held>();

    /// <summary>Is a CAGEAnimation currently being previewed?</summary>
    public static bool Active { get; private set; }

    /// <summary>
    /// Take a preview packet: either the full set of targets at the playhead, or the end of the mode.
    /// Must run on the main thread - it moves scene nodes.
    /// </summary>
    public static void Apply(AlienScene scene, bool active, List<SyncedAnimationTarget> targets)
    {
        if (scene == null || !GodotObject.IsInstanceValid(scene))
            return;

        if (!active)
        {
            Clear();
            return;
        }

        Active = true;

        HashSet<ulong> stillDriven = new HashSet<ulong>();
        if (targets != null)
        {
            foreach (SyncedAnimationTarget target in targets)
            {
                if (target?.path == null || target.path.Count == 0)
                    continue;

                Node3D node = scene.TryResolveInstancePathNode(target.path);
                if (node == null || !GodotObject.IsInstanceValid(node))
                    continue;

                ulong id = node.GetInstanceId();
                stillDriven.Add(id);

                //Remembered the first time the animation reaches for it, before anything has moved it
                if (!_held.ContainsKey(id))
                {
                    _held.Add(id, new Held()
                    {
                        Node = node,
                        Position = node.Position,
                        RotationDegrees = node.RotationDegrees,
                    });
                }

                node.Position = CathodeCoordinates.PositionToGodot(ParameterSync.ToVector3(target.vector3_a));
                node.RotationDegrees = CathodeCoordinates.EulerDegreesToGodot(ParameterSync.ToVector3(target.vector3_b));
                LevelViewerPick.InvalidatePickBounds(node);
            }
        }

        //A target that has stopped being animated (its track was deleted) goes back where it was
        List<ulong> released = null;
        foreach (KeyValuePair<ulong, Held> entry in _held)
        {
            if (stillDriven.Contains(entry.Key))
                continue;
            (released ??= new List<ulong>()).Add(entry.Key);
        }
        if (released != null)
        {
            foreach (ulong id in released)
            {
                Restore(_held[id]);
                _held.Remove(id);
            }
        }
    }

    /// <summary>Put everything the preview moved back, and stop previewing.</summary>
    public static void Clear()
    {
        foreach (KeyValuePair<ulong, Held> entry in _held)
            Restore(entry.Value);
        _held.Clear();
        Active = false;
    }

    /// <summary>
    /// The scene these nodes belonged to has gone (a level load, a repopulate). Nothing to put back -
    /// the nodes no longer exist, and the level was never written to in the first place.
    /// </summary>
    public static void Forget()
    {
        _held.Clear();
        Active = false;
    }

    private static void Restore(Held held)
    {
        if (held?.Node == null || !GodotObject.IsInstanceValid(held.Node))
            return;
        held.Node.Position = held.Position;
        held.Node.RotationDegrees = held.RotationDegrees;
        LevelViewerPick.InvalidatePickBounds(held.Node);
    }
}
