using CATHODE.Scripting;
using OpenCAGE;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Box render filter state synced from OpenCAGE. Supported types are hardcoded in BoxRenderFilterDefinitions.
/// </summary>
public static class BoxRenderFilters
{
    private static Dictionary<uint, bool> _enabledByFunctionType = new Dictionary<uint, bool>();

    public static bool ApplyFromPacket(Dictionary<uint, bool> filters)
    {
        if (filters == null || filters.Count == 0)
            return false;

        if (_enabledByFunctionType.Count == filters.Count)
        {
            bool unchanged = true;
            foreach (KeyValuePair<uint, bool> entry in filters)
            {
                if (!_enabledByFunctionType.TryGetValue(entry.Key, out bool existing) || existing != entry.Value)
                {
                    unchanged = false;
                    break;
                }
            }
            if (unchanged)
                return false;
        }

        _enabledByFunctionType = new Dictionary<uint, bool>(filters);
        return true;
    }

    public static bool IsEnabled(FunctionType functionType)
    {
        return IsEnabled((uint)functionType);
    }

    public static bool IsEnabled(uint functionType)
    {
        if (_enabledByFunctionType.TryGetValue(functionType, out bool enabled))
            return enabled;
        return true;
    }

    public static bool ShouldShowBoxPreview(FunctionEntity entity, CommandsUtils utils)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        return BoxRenderFilterDefinitions.IsSupported(entity.function.AsFunctionType);
    }

    public static bool IsVisible(FunctionEntity entity, CommandsUtils utils)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return true;

        FunctionType functionType = entity.function.AsFunctionType;
        if (!BoxRenderFilterDefinitions.IsSupported(functionType))
            return true;

        return IsEnabled(functionType);
    }

    public static Color GetPreviewColor(FunctionEntity entity)
    {
        FunctionType functionType = entity.function.AsFunctionType;
        BoxRenderFilterDefinitions.BoxRenderFilterColor color = BoxRenderFilterDefinitions.GetColor(functionType);
        return new Color(color.R, color.G, color.B, color.A);
    }
}
