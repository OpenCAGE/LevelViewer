using CathodeLib;
using Godot;
using System;
using static CATHODE.Models;

/// <summary>CPU-parsed mesh arrays ready for main-thread ArrayMesh creation.</summary>
public sealed class ParsedMeshSurface
{
	public Vector3[] Vertices;
	public int[] Indices;
	public Vector3[] Normals;
	public Vector2[] Uvs;

	public bool IsValid =>
		Vertices != null && Vertices.Length > 0
		&& Indices != null && Indices.Length >= 3;

	public static bool TryParse(CS2.Component.LOD.Submesh submesh, out ParsedMeshSurface surface)
	{
		surface = null;
		if (submesh?.Data == null || submesh.Data.Length == 0)
			return false;

		cMesh cathodeMesh = ModelUtility.ToMesh(submesh);
		if (cathodeMesh.Vertices.Count == 0 || cathodeMesh.Indices.Count < 3)
			return false;

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

		Vector2[] uvs = null;
		if (cathodeMesh.UVs != null
			&& cathodeMesh.UVs.Length > 0
			&& cathodeMesh.UVs[0] != null
			&& cathodeMesh.UVs[0].Count == cathodeMesh.Vertices.Count)
		{
			uvs = new Vector2[cathodeMesh.UVs[0].Count];
			for (int i = 0; i < cathodeMesh.UVs[0].Count; i++)
				uvs[i] = cathodeMesh.UVs[0][i];
		}

		surface = new ParsedMeshSurface
		{
			Vertices = vertices,
			Indices = indices,
			Normals = normals,
			Uvs = uvs,
		};
		return true;
	}

	public ArrayMesh ToArrayMesh()
	{
		ArrayMesh mesh = new ArrayMesh();
		if (!IsValid)
			return mesh;

		using Godot.Collections.Array arrays = new Godot.Collections.Array(); //alive across the native call - see CollisionMeshOverlay.BuildMesh
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
		arrays[(int)Mesh.ArrayType.Index] = Indices;

		if (Normals != null)
			arrays[(int)Mesh.ArrayType.Normal] = Normals;

		if (Uvs != null)
			arrays[(int)Mesh.ArrayType.TexUV] = Uvs;

		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}
}
