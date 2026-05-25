using CathodeLib;
using Godot;
using System;
using static CATHODE.Models;

namespace CathodeLib
{
    /// <summary>Euler helpers for Godot <see cref="Vector3"/> used by CathodeLib scripting types.</summary>
    public static class GodotMathExtensions
    {
        public static Vector3 AddEulerAngles(this Vector3 euler1, Vector3 euler2)
        {
            Vector3 result = euler1 + euler2;
            result.X = NormalizeAngle(result.X);
            result.Y = NormalizeAngle(result.Y);
            result.Z = NormalizeAngle(result.Z);
            return result;
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f)
                angle -= 360f;
            if (angle < -180f)
                angle += 360f;
            return angle;
        }
    }
}

public static class CathodeLibExtensions
{
    /* Convert a CS2 submesh to Godot ArrayMesh */
    public static ArrayMesh ToArrayMesh(this CS2.Component.LOD.Submesh submesh)
    {
        ArrayMesh mesh = new ArrayMesh();

        if (submesh == null || submesh.Data == null || submesh.Data.Length == 0)
            return mesh;

        cMesh cathodeMesh = ModelUtility.ToMesh(submesh);
        if (cathodeMesh.Vertices.Count == 0 || cathodeMesh.Indices.Count < 3)
            return mesh;

        Vector3[] vertices = new Vector3[cathodeMesh.Vertices.Count];
        for (int i = 0; i < cathodeMesh.Vertices.Count; i++)
            vertices[i] = cathodeMesh.Vertices[i];

        int[] indices = new int[cathodeMesh.Indices.Count];
        for (int i = 0; i < cathodeMesh.Indices.Count; i++)
            indices[i] = cathodeMesh.Indices[i];

        Vector3[] normals = null;
        if (cathodeMesh.Normals.Count == cathodeMesh.Vertices.Count)
        {
            normals = new Vector3[cathodeMesh.Normals.Count];
            for (int i = 0; i < cathodeMesh.Normals.Count; i++)
                normals[i] = cathodeMesh.Normals[i];
        }

        CathodeCoordinates.ConvertMeshVerticesToGodotHandedness(vertices, normals);

        Godot.Collections.Array arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        if (normals != null)
            arrays[(int)Mesh.ArrayType.Normal] = normals;

        if (cathodeMesh.UVs != null
            && cathodeMesh.UVs.Length > 0
            && cathodeMesh.UVs[0] != null
            && cathodeMesh.UVs[0].Count == cathodeMesh.Vertices.Count)
        {
            Vector2[] uvs = new Vector2[cathodeMesh.UVs[0].Count];
            for (int i = 0; i < cathodeMesh.UVs[0].Count; i++)
                uvs[i] = cathodeMesh.UVs[0][i];
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
