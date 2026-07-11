using CathodeLib;
using Godot;
using System;
using static CATHODE.Models;

public static class CathodeLibExtensions
{
	/* Convert a CS2 submesh to Godot ArrayMesh */
	public static ArrayMesh ToArrayMesh(this CS2.Component.LOD.Submesh submesh)
	{
		if (ParsedMeshSurface.TryParse(submesh, out ParsedMeshSurface surface))
			return surface.ToArrayMesh();

		return new ArrayMesh();
	}
}
