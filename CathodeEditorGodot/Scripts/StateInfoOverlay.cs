using CATHODE;
using Godot;
using System;
using System.Collections.Generic;
using NavigationMesh = CATHODE.NavigationMesh; //Godot has a NavigationMesh of its own
using Level = CathodeLib.Level;

/// <summary>
/// Draws a level state's generated navigation data: the Detour navmesh from STATE_x/NAV_MESH, and the
/// cover segments from STATE_x/COVER.
///
/// Both are rebuilt by an instanced save, and the viewer re-reads the level when it repopulates, so
/// nothing is cached across a reload - the overlay is rebuilt from whatever is on disk at that point.
/// </summary>
public partial class StateInfoOverlay : Node3D
{
	private static readonly Color NavMeshColour = new Color(0.25f, 0.75f, 1f, 0.55f);
	private static readonly Color NavMeshEdgeColour = new Color(0.1f, 0.35f, 0.6f, 0.9f);
	private static readonly Color CoverColour = new Color(1f, 0.65f, 0.15f, 0.6f);

	private LevelContent _content;
	private Node3D _navMeshRoot;
	private Node3D _coverRoot;
	private int _builtNavMeshState = -1;
	private int _builtCoverState = -1;

	public void Setup(LevelContent content)
	{
		_content = content;
		ClearNavMesh();
		ClearCover();
	}

	/// <summary>Show the requested states, building each the first time it's asked for.</summary>
	public void Apply(int navMeshState, int coverState)
	{
		if (navMeshState != _builtNavMeshState)
		{
			ClearNavMesh();
			if (navMeshState >= 0)
				BuildNavMesh(navMeshState);
			_builtNavMeshState = navMeshState;
		}

		if (coverState != _builtCoverState)
		{
			ClearCover();
			if (coverState >= 0)
				BuildCover(coverState);
			_builtCoverState = coverState;
		}

		Visible = navMeshState >= 0 || coverState >= 0;
	}

	private void ClearNavMesh()
	{
		if (_navMeshRoot != null && GodotObject.IsInstanceValid(_navMeshRoot))
			_navMeshRoot.QueueFree();
		_navMeshRoot = null;
		_builtNavMeshState = -1;
	}

	private void ClearCover()
	{
		if (_coverRoot != null && GodotObject.IsInstanceValid(_coverRoot))
			_coverRoot.QueueFree();
		_coverRoot = null;
		_builtCoverState = -1;
	}

	private Level.State GetState(int index)
	{
		List<Level.State> states = _content?.Level?.StateResources;
		if (states == null || index < 0 || index >= states.Count)
			return null;
		return states[index];
	}

	#region Navmesh

	private void BuildNavMesh(int stateIndex)
	{
		NavigationMesh navMesh = GetState(stateIndex)?.NavMesh;
		if (navMesh == null || !navMesh.Loaded || navMesh.Polygons == null || navMesh.DetailMeshes == null)
		{
			ViewerLog.Print("[StateInfo] State " + stateIndex + " has no navmesh to draw.");
			return;
		}

		List<Vector3> positions = new List<Vector3>();
		List<int> indices = new List<int>();
		BuildDetourTriangles(navMesh, positions, indices);

		if (indices.Count == 0)
		{
			ViewerLog.Print("[StateInfo] State " + stateIndex + " navmesh produced no triangles.");
			return;
		}

		_navMeshRoot = new Node3D { Name = "NavMesh_" + stateIndex };
		AddChild(_navMeshRoot);

		MeshInstance3D surface = new MeshInstance3D
		{
			Name = "Surface",
			Mesh = BuildTriangleMesh(positions, indices),
			MaterialOverride = BuildOverlayMaterial(NavMeshColour, transparent: true),
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(surface);
		_navMeshRoot.AddChild(surface);

		MeshInstance3D wire = new MeshInstance3D
		{
			Name = "Edges",
			Mesh = BuildWireMesh(positions, indices),
			MaterialOverride = BuildOverlayMaterial(NavMeshEdgeColour, transparent: false),
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(wire);
		_navMeshRoot.AddChild(wire);

		ViewerLog.Print("[StateInfo] Navmesh state " + stateIndex + ": " + (indices.Count / 3) + " triangles.");
	}

	/// <summary>
	/// Detour stores a coarse polygon plus a detail sub-mesh per polygon. A detail triangle indexes the
	/// polygon's own verts when the index is below its vertex count, and the detail vertex pool above it.
	/// </summary>
	private static void BuildDetourTriangles(NavigationMesh navMesh, List<Vector3> positions, List<int> indices)
	{
		int polyCount = Math.Min(navMesh.Polygons.Length, navMesh.DetailMeshes.Length);
		for (int p = 0; p < polyCount; p++)
		{
			NavigationMesh.dtPoly poly = navMesh.Polygons[p];
			NavigationMesh.dtPolyDetail detail = navMesh.DetailMeshes[p];
			if (poly.verts == null)
				continue;

			for (int t = 0; t < detail.triCount; t++)
			{
				int triangleOffset = (detail.triBase + t) * 4;
				if (triangleOffset + 2 >= navMesh.DetailIndices.Length)
					break;

				int start = positions.Count;
				bool valid = true;
				for (int corner = 0; corner < 3; corner++)
				{
					int local = navMesh.DetailIndices[triangleOffset + corner];
					Vector3 point;
					if (local < poly.vertCount)
					{
						int vertexIndex = poly.verts[local];
						if (vertexIndex < 0 || vertexIndex >= navMesh.Vertices.Length)
						{
							valid = false;
							break;
						}
						point = navMesh.Vertices[vertexIndex];
					}
					else
					{
						int detailIndex = detail.vertBase + (local - poly.vertCount);
						if (detailIndex < 0 || detailIndex >= navMesh.DetailVertices.Length)
						{
							valid = false;
							break;
						}
						point = navMesh.DetailVertices[detailIndex];
					}

					positions.Add(CathodeCoordinates.PositionToGodot(point));
				}

				if (!valid)
				{
					positions.RemoveRange(start, positions.Count - start);
					continue;
				}

				indices.Add(start);
				indices.Add(start + 1);
				indices.Add(start + 2);
			}
		}
	}

	#endregion

	#region Cover

	private void BuildCover(int stateIndex)
	{
		Cover cover = GetState(stateIndex)?.Cover;
		if (cover == null || !cover.Loaded || cover.Entries.Count == 0)
		{
			ViewerLog.Print("[StateInfo] State " + stateIndex + " has no cover to draw.");
			return;
		}

		//Each segment is a wall run from Left to Right, standing Height tall
		List<Vector3> positions = new List<Vector3>();
		List<int> indices = new List<int>();

		for (int i = 0; i < cover.Entries.Count; i++)
		{
			Cover.CoverSegment segment = cover.Entries[i];
			float height = segment.Height <= 0f ? 1f : segment.Height;

			Vector3 left = CathodeCoordinates.PositionToGodot(segment.Left);
			Vector3 right = CathodeCoordinates.PositionToGodot(segment.Right);
			Vector3 up = new Vector3(0f, height, 0f);

			int start = positions.Count;
			positions.Add(left);
			positions.Add(right);
			positions.Add(right + up);
			positions.Add(left + up);

			//Two triangles per quad, wound both ways so the run reads from either side
			indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
			indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
			indices.Add(start); indices.Add(start + 2); indices.Add(start + 1);
			indices.Add(start); indices.Add(start + 3); indices.Add(start + 2);
		}

		if (indices.Count == 0)
			return;

		_coverRoot = new Node3D { Name = "Cover_" + stateIndex };
		AddChild(_coverRoot);

		MeshInstance3D mesh = new MeshInstance3D
		{
			Name = "Segments",
			Mesh = BuildTriangleMesh(positions, indices),
			MaterialOverride = BuildOverlayMaterial(CoverColour, transparent: true),
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(mesh);
		_coverRoot.AddChild(mesh);

		ViewerLog.Print("[StateInfo] Cover state " + stateIndex + ": " + cover.Entries.Count + " segments.");
	}

	#endregion

	#region Mesh helpers

	private static ArrayMesh BuildTriangleMesh(List<Vector3> positions, List<int> indices)
	{
		Godot.Collections.Array surface = new Godot.Collections.Array();
		surface.Resize((int)Mesh.ArrayType.Max);
		surface[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
		surface[(int)Mesh.ArrayType.Index] = indices.ToArray();

		ArrayMesh mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surface);
		return mesh;
	}

	/// <summary>Triangle outlines, so individual navmesh polys stay readable against the floor.</summary>
	private static ArrayMesh BuildWireMesh(List<Vector3> positions, List<int> indices)
	{
		List<int> lines = new List<int>(indices.Count * 2);
		for (int i = 0; i + 2 < indices.Count; i += 3)
		{
			lines.Add(indices[i]); lines.Add(indices[i + 1]);
			lines.Add(indices[i + 1]); lines.Add(indices[i + 2]);
			lines.Add(indices[i + 2]); lines.Add(indices[i]);
		}

		Godot.Collections.Array surface = new Godot.Collections.Array();
		surface.Resize((int)Mesh.ArrayType.Max);
		surface[(int)Mesh.ArrayType.Vertex] = positions.ToArray();
		surface[(int)Mesh.ArrayType.Index] = lines.ToArray();

		ArrayMesh mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, surface);
		return mesh;
	}

	private static StandardMaterial3D BuildOverlayMaterial(Color colour, bool transparent)
	{
		return new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = colour,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
			//Sits on top of the floor it was baked against, which would otherwise z-fight
			NoDepthTest = false,
			RenderPriority = 1,
		};
	}

	#endregion
}
