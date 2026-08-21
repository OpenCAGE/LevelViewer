using CATHODE.Scripting;
using OpenCAGE;
using System.Collections.Generic;
using Godot;

public static class RenderFilters
{
    private static readonly object _lock = new object();
    private static Dictionary<uint, bool> _enabledByFunctionType = new Dictionary<uint, bool>();
    private static Dictionary<string, bool> _enabledSceneFilters = new Dictionary<string, bool>();

    /// <summary>Apply the scene geometry filters (occlusion / collision). True when any changed.</summary>
    public static bool ApplySceneFiltersFromPacket(Dictionary<string, bool> filters)
    {
        if (filters == null)
            return false;

        lock (_lock)
        {
            bool changed = false;
            foreach (KeyValuePair<string, bool> entry in filters)
            {
                if (_enabledSceneFilters.TryGetValue(entry.Key, out bool existing) && existing == entry.Value)
                    continue;

                changed = true;
                break;
            }

            if (!changed)
                return false;

            _enabledSceneFilters = new Dictionary<string, bool>(filters);
            return true;
        }
    }

    public static bool IsSceneFilterEnabled(SceneFilterKind kind)
    {
        lock (_lock)
        {
            if (_enabledSceneFilters.TryGetValue(kind.ToString(), out bool enabled))
                return enabled;
            return false;
        }
    }

    public static bool ApplyFromPacket(Dictionary<uint, bool> filters, out HashSet<uint> changedFunctionTypes)
    {
        changedFunctionTypes = null;
        if (filters == null || filters.Count == 0)
            return false;

        lock (_lock)
        {
            if (!HasFilterChanges(filters))
                return false;

            changedFunctionTypes = GetChangedFunctionTypes(filters);
            _enabledByFunctionType = new Dictionary<uint, bool>(filters);
            return true;
        }
    }

    public static bool ApplyFromPacket(Dictionary<uint, bool> filters)
    {
        return ApplyFromPacket(filters, out _);
    }

    public static bool IsEnabled(FunctionType functionType)
    {
        return IsEnabled((uint)functionType);
    }

    public static bool IsEnabled(uint functionType)
    {
        lock (_lock)
        {
            if (_enabledByFunctionType.TryGetValue(functionType, out bool enabled))
                return enabled;
            return false;
        }
    }

    public static Color GetPreviewColor(FunctionEntity entity)
    {
        return PreviewVisualUtility.GetPreviewColor(entity);
    }

    public static bool IsVisible(FunctionEntity entity)
    {
        return PreviewVisualUtility.IsVisible(entity);
    }

    private static HashSet<uint> GetChangedFunctionTypes(Dictionary<uint, bool> filters)
    {
        HashSet<uint> changed = new HashSet<uint>();
        foreach (KeyValuePair<uint, bool> entry in filters)
        {
            if (!_enabledByFunctionType.TryGetValue(entry.Key, out bool existing) || existing != entry.Value)
                changed.Add(entry.Key);
        }

        foreach (KeyValuePair<uint, bool> entry in _enabledByFunctionType)
        {
            if (!filters.ContainsKey(entry.Key))
                changed.Add(entry.Key);
        }

        return changed;
    }

    private static bool HasFilterChanges(Dictionary<uint, bool> filters)
    {
        if (_enabledByFunctionType.Count != filters.Count)
            return true;

        foreach (KeyValuePair<uint, bool> entry in filters)
        {
            if (!_enabledByFunctionType.TryGetValue(entry.Key, out bool existing) || existing != entry.Value)
                return true;
        }

        foreach (KeyValuePair<uint, bool> entry in _enabledByFunctionType)
        {
            if (!filters.ContainsKey(entry.Key))
                return true;
        }

        return false;
    }
}
