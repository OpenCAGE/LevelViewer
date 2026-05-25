using Godot;

/// <summary>
/// Converts Cathode / Unity-style coordinates (Y-up, left-handed) to Godot (Y-up, right-handed).
/// </summary>
public static class CathodeCoordinates
{
    public static Vector3 PositionToGodot(Vector3 position)
    {
        return new Vector3(position.X, position.Y, -position.Z);
    }

    public static Vector3 EulerDegreesToGodot(Vector3 eulerDegrees)
    {
        return new Vector3(-eulerDegrees.X, -eulerDegrees.Y, eulerDegrees.Z);
    }

    public static Vector3 DirectionToGodot(Vector3 direction)
    {
        return new Vector3(direction.X, direction.Y, -direction.Z);
    }

    /// <summary>
    /// Mirror mesh positions/normals into Godot space (Z flip only — do not swap indices; that inverts front faces).
    /// </summary>
    public static void ConvertMeshVerticesToGodotHandedness(Vector3[] vertices, Vector3[] normals)
    {
        if (vertices != null)
        {
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = PositionToGodot(vertices[i]);
        }

        if (normals != null)
        {
            for (int i = 0; i < normals.Length; i++)
                normals[i] = DirectionToGodot(normals[i]);
        }
    }

    /// <summary>Reverses each triangle's winding (Godot 4.3 ArrayMesh has no SurfaceFlipFaces).</summary>
    public static void FlipTriangleWinding(int[] indices)
    {
        if (indices == null)
            return;

        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int swap = indices[t + 1];
            indices[t + 1] = indices[t + 2];
            indices[t + 2] = swap;
        }
    }
}
