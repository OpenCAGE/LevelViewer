using Godot;

/// <summary>
/// Frustum / occlusion culling tweaks for dynamically spawned level meshes.
/// </summary>
public static class LevelViewerMeshUtil
{
    public static void ConfigureMeshInstance(MeshInstance3D meshInstance, Vector3[] sourceVertices = null)
    {
        if (meshInstance == null)
            return;

        meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        meshInstance.IgnoreOcclusionCulling = true;

        Aabb bounds = meshInstance.Mesh != null ? meshInstance.Mesh.GetAabb() : new Aabb();
        if (bounds.Size.LengthSquared() < 1e-8f && sourceVertices != null && sourceVertices.Length > 0)
            bounds = ComputeAabb(sourceVertices);

        float grow = Mathf.Max(bounds.Size.Length() * 0.5f, 32f);
        meshInstance.ExtraCullMargin = grow;

        if (bounds.HasVolume())
            meshInstance.CustomAabb = bounds.Grow(grow * 0.25f);
    }

    public static Aabb ComputeAabb(Vector3[] vertices)
    {
        if (vertices == null || vertices.Length == 0)
            return new Aabb();

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            min.X = Mathf.Min(min.X, v.X);
            min.Y = Mathf.Min(min.Y, v.Y);
            min.Z = Mathf.Min(min.Z, v.Z);
            max.X = Mathf.Max(max.X, v.X);
            max.Y = Mathf.Max(max.Y, v.Y);
            max.Z = Mathf.Max(max.Z, v.Z);
        }

        return new Aabb(min, max - min);
    }
}
