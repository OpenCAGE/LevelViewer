using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using System.Collections.Generic;

public static class EntityNodeUtil
{
    public const string PointedMetaKey = "pointed";

    public static bool IsPointed(Node3D node)
    {
        return node != null && node.HasMeta(PointedMetaKey) && (bool)node.GetMeta(PointedMetaKey);
    }

    public static void SetPointed(Node3D node, bool pointed)
    {
        if (node == null)
            return;

        if (pointed)
            node.SetMeta(PointedMetaKey, true);
        else if (node.HasMeta(PointedMetaKey))
            node.RemoveMeta(PointedMetaKey);
    }

    public static EntityOverride GetEntityOverride(Node node)
    {
        if (node is EntityOverride entityOverride)
            return entityOverride;

        Node current = node;
        while (current != null)
        {
            if (current is EntityOverride eo)
                return eo;
            current = current.GetParent();
        }

        return null;
    }

    public static T[] FindPreviews<T>(Node root) where T : FunctionEntityPreview
    {
        List<T> results = new List<T>();
        CollectPreviews(root, results);
        return results.ToArray();
    }

    private static void CollectPreviews<T>(Node node, List<T> results) where T : FunctionEntityPreview
    {
        var pending = new Stack<Node>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current == null || !GodotObject.IsInstanceValid(current))
                continue;

            if (current is T preview)
                results.Add(preview);

            foreach (Node child in current.GetChildren())
                pending.Push(child);
        }
    }

    public static FunctionEntityPreview[] FindAllPreviews(Node root)
    {
        List<FunctionEntityPreview> results = new List<FunctionEntityPreview>();
        if (root == null)
            return results.ToArray();

        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current == null || !GodotObject.IsInstanceValid(current))
                continue;

            if (current is FunctionEntityPreview preview)
                results.Add(preview);

            foreach (Node child in current.GetChildren())
                pending.Push(child);
        }

        return results.ToArray();
    }
}
