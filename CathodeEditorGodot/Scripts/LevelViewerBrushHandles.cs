using System;
using System.Collections.Generic;
using Godot;

/// <summary>
/// Resize handles on a brush (BRUSH_EDIT): a box or plane OpenCAGE made and rebuilds from its bounds. Each face of a box (each
/// edge of a plane) has a square handle on the HUD at its centre; dragging one pushes that face in or out along its normal,
/// Hammer-style, landing on the transform grid when grid snapping is on, with the new bounds drawn as a wire box while it moves.
/// Letting go hands the bounds to OpenCAGE (BRUSH_EDIT_RESIZED), which rebuilds the brush's model as one undo step and sends
/// the handles again. A mesh brush (kind 2) has a handle on each vertex instead, and its edges drawn over it: a vertex drags
/// across the plane facing the camera (onto the grid when snapping), and letting go sends every vertex (BRUSH_EDIT_VERTICES).
/// Nothing here writes the level.
/// </summary>
public sealed class LevelViewerBrushHandles
{
	private static readonly Color HandleColour = new Color(1f, 0.78f, 0.15f);
	private static readonly Color ActiveColour = new Color(0.25f, 0.85f, 1f);
	private static readonly Color HandleEdgeColour = new Color(0.07f, 0.08f, 0.1f);
	private const float HandleSize = 11f;
	private const float PickRadius = 10f; //on screen, from a handle's centre
	private static readonly StringName PanelStyleName = new StringName("panel");

	//Outward normals of the faces, in Cathode space: +X -X +Y -Y +Z -Z
	private static readonly Vector3[] Normals =
	{
		new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
	};
	private const float MinimumSize = 0.01f;

	private bool _active;
	private uint _entityId;
	private int _kind; //0 box, 1 plane, 2 mesh
	private Vector3 _min, _max; //Cathode space, the brush's own
	private Vector3[] _vertices = new Vector3[0]; //a mesh's, Cathode space
	private int[] _edges = new int[0];
	private readonly List<Panel> _vertexDots = new List<Panel>();
	private const float VertexHandleSize = 9f;

	//A vertex drag
	private int _dragVertex = -1;
	private Vector3[] _dragVertices;
	private Vector3 _dragPlaneNormal, _dragPlanePoint, _dragStartHit;

	private Node3D _host;
	private Control _overlay;
	private readonly Panel[] _dots = new Panel[6];
	private StyleBoxFlat _dotStyle, _activeStyle;
	private MeshInstance3D _wire;
	private ImmediateMesh _wireMesh;

	//A drag
	private int _dragFace = -1;
	private Vector3 _dragAxisWorld, _dragOriginWorld;
	private float _dragStartT, _dragScale = 1f;
	private Vector3 _dragMin, _dragMax;
	private DateTime _sentAt = DateTime.MinValue;

	public bool Active => _active;
	public bool Dragging => _dragFace >= 0 || _dragVertex >= 0;
	public bool DraggingVertex => _dragVertex >= 0;
	public bool IsMesh => _kind == 2;
	public uint EntityId => _entityId;

	/// <summary>The brush's bounds and kind from BRUSH_EDIT (Cathode space) - and a mesh's vertices and edges. Off takes the handles away. Main thread.</summary>
	public void Apply(AlienScene scene, bool active, uint entityId, float[] min, float[] max, int kind, float[] vertices = null, int[] edges = null)
	{
		if (!active || entityId == 0 || min == null || max == null || min.Length < 3 || max.Length < 3 || (kind == 2 && (vertices == null || vertices.Length < 3)))
		{
			End();
			return;
		}
		if (_active && entityId != _entityId)
			CancelDrag();
		_active = true;
		_entityId = entityId;
		_kind = kind;
		_min = new Vector3(min[0], min[1], min[2]);
		_max = new Vector3(max[0], max[1], max[2]);
		_vertices = new Vector3[kind == 2 ? vertices.Length / 3 : 0];
		for (int i = 0; i < _vertices.Length; i++)
			_vertices[i] = new Vector3(vertices[i * 3], vertices[i * 3 + 1], vertices[i * 3 + 2]);
		_edges = kind == 2 && edges != null ? edges : new int[0];
		_sentAt = DateTime.MinValue;
		_host = null;
		ResolveHost(scene);
		if (!Dragging)
		{
			HideWire();
			if (_kind == 2) DrawEdges(_vertices);
		}
	}

	public void End()
	{
		_active = false;
		_entityId = 0;
		_host = null;
		_dragFace = -1;
		_dragVertex = -1;
		_vertices = new Vector3[0];
		_edges = new int[0];
		HideWire();
		HideOverlay();
	}

	/// <summary>Per frame: find the brush again if the scene was rebuilt, and draw the handles where the camera now puts them.</summary>
	public void Update(Camera3D camera, AlienScene scene, CanvasLayer hudLayer)
	{
		if (!_active)
		{
			HideOverlay();
			return;
		}
		if (_host == null || !GodotObject.IsInstanceValid(_host))
		{
			_host = null;
			if (!ResolveHost(scene))
			{
				HideOverlay();
				return;
			}
			if (_kind == 2 && !Dragging) DrawEdges(_vertices);
		}
		//The rebuilt brush comes round as a new BRUSH_EDIT; the wire box drawn at the release stands in for it until then
		if (!Dragging && _sentAt != DateTime.MinValue && (DateTime.Now - _sentAt).TotalSeconds > 5)
		{
			_sentAt = DateTime.MinValue;
			HideWire();
			if (_kind == 2) DrawEdges(_vertices);
		}
		DrawOverlay(camera, hudLayer);
	}

	/// <summary>A mesh brush's vertex whose handle is under a press, if any (the nearest within the pick radius).</summary>
	public bool TryPickVertex(Camera3D camera, Vector2 screenPosition, out int vertex)
	{
		vertex = -1;
		if (!_active || _kind != 2 || camera == null || _host == null || !GodotObject.IsInstanceValid(_host))
			return false;
		float best = PickRadius * PickRadius;
		for (int v = 0; v < _vertices.Length; v++)
		{
			Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(_vertices[v]));
			if (camera.IsPositionBehind(world))
				continue;
			float distance = screenPosition.DistanceSquaredTo(camera.UnprojectPosition(world));
			if (distance <= best)
			{
				best = distance;
				vertex = v;
			}
		}
		return vertex >= 0;
	}

	/// <summary>Where a mesh brush's vertex handle is on screen.</summary>
	public bool TryGetVertexScreen(Camera3D camera, int vertex, out Vector2 screen)
	{
		screen = Vector2.Zero;
		if (!_active || _kind != 2 || camera == null || _host == null || !GodotObject.IsInstanceValid(_host) || vertex < 0 || vertex >= _vertices.Length)
			return false;
		Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(_vertices[vertex]));
		if (camera.IsPositionBehind(world))
			return false;
		screen = camera.UnprojectPosition(world);
		return true;
	}

	/// <summary>Start dragging a mesh brush's vertex from a press: it moves across the plane through it that faces the camera.</summary>
	public void BeginVertexDrag(Camera3D camera, int vertex, Vector2 screenPosition)
	{
		if (!_active || _kind != 2 || _host == null || vertex < 0 || vertex >= _vertices.Length)
			return;
		_dragVertex = vertex;
		_dragVertices = (Vector3[])_vertices.Clone();
		_dragPlanePoint = _host.ToGlobal(CathodeCoordinates.PositionToGodot(_vertices[vertex]));
		_dragPlaneNormal = -camera.GlobalBasis.Z.Normalized();
		_dragStartHit = OnDragPlane(camera, screenPosition) ?? _dragPlanePoint;
		DrawEdges(_dragVertices);
	}

	/// <summary>Let go of a vertex: every vertex where it is now, when the dragged one moved.</summary>
	public bool EndVertexDrag(out int vertex, out float[] vertices)
	{
		vertex = _dragVertex;
		vertices = null;
		Vector3[] moved = _dragVertices;
		_dragVertex = -1;
		_dragVertices = null;
		if (vertex < 0 || moved == null || (moved[vertex] - _vertices[vertex]).LengthSquared() < 1e-10f)
		{
			if (_kind == 2) DrawEdges(_vertices);
			return false;
		}
		vertices = new float[moved.Length * 3];
		for (int i = 0; i < moved.Length; i++)
		{
			vertices[i * 3] = moved[i].X;
			vertices[i * 3 + 1] = moved[i].Y;
			vertices[i * 3 + 2] = moved[i].Z;
		}
		//Shown where it was let go until the rebuilt brush comes back
		_vertices = moved;
		_sentAt = DateTime.Now;
		return true;
	}

	/* The cursor's ray against the vertex drag's camera-facing plane, in world space */
	private Vector3? OnDragPlane(Camera3D camera, Vector2 screenPosition)
	{
		Vector3 o = camera.ProjectRayOrigin(screenPosition);
		Vector3 d = camera.ProjectRayNormal(screenPosition);
		float denominator = _dragPlaneNormal.Dot(d);
		if (Mathf.Abs(denominator) < 1e-5f)
			return null;
		float t = _dragPlaneNormal.Dot(_dragPlanePoint - o) / denominator;
		return t < 0 ? (Vector3?)null : o + d * t;
	}

	private void VertexDragTo(Camera3D camera, Vector2 screenPosition)
	{
		Vector3? hit = OnDragPlane(camera, screenPosition);
		if (hit == null)
			return;
		//The move in the brush's own Cathode space; each coordinate it changes lands on the grid when snapping
		Vector3 from = CathodeCoordinates.PositionFromGodot(_host.ToLocal(_dragStartHit));
		Vector3 to = CathodeCoordinates.PositionFromGodot(_host.ToLocal(hit.Value));
		Vector3 start = _vertices[_dragVertex], delta = to - from;
		float snap = LevelViewerTransformSnap.GridSize;
		float Axis(float s, float by) => Mathf.Abs(by) < 1e-4f ? s : LevelViewerTransformSnap.SnapValue(s + by, snap);
		_dragVertices[_dragVertex] = new Vector3(Axis(start.X, delta.X), Axis(start.Y, delta.Y), Axis(start.Z, delta.Z));
		DrawEdges(_dragVertices);
	}

	/// <summary>The face whose handle is under a press, if any (the nearest within the pick radius).</summary>
	public bool TryPick(Camera3D camera, Vector2 screenPosition, out int face)
	{
		face = -1;
		if (!_active || camera == null || _host == null || !GodotObject.IsInstanceValid(_host))
			return false;
		float best = PickRadius * PickRadius;
		foreach (int f in Faces())
		{
			Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(FaceCentre(f, _min, _max)));
			if (camera.IsPositionBehind(world))
				continue;
			float distance = screenPosition.DistanceSquaredTo(camera.UnprojectPosition(world));
			if (distance <= best)
			{
				best = distance;
				face = f;
			}
		}
		return face >= 0;
	}

	/// <summary>Where a face's handle is on screen - or, with <paramref name="along"/>, the point that far out along its normal (metres).</summary>
	public bool TryGetHandleScreen(Camera3D camera, int face, float along, out Vector2 screen)
	{
		screen = Vector2.Zero;
		if (!_active || camera == null || _host == null || !GodotObject.IsInstanceValid(_host) || face < 0 || face > 5)
			return false;
		Vector3 local = FaceCentre(face, _min, _max) + Normals[face] * along;
		Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(local));
		if (camera.IsPositionBehind(world))
			return false;
		screen = camera.UnprojectPosition(world);
		return true;
	}

	/// <summary>Start dragging a face's handle from a press at <paramref name="screenPosition"/>.</summary>
	public void BeginDrag(Camera3D camera, int face, Vector2 screenPosition)
	{
		if (!_active || _host == null || face < 0 || face > 5)
			return;
		Vector3 axisLocal = CathodeCoordinates.DirectionToGodot(Normals[face]);
		Vector3 axisWorld = _host.GlobalBasis * axisLocal;
		_dragScale = Mathf.Max(axisWorld.Length(), 1e-4f);
		_dragAxisWorld = axisWorld / _dragScale;
		_dragOriginWorld = _host.ToGlobal(CathodeCoordinates.PositionToGodot(FaceCentre(face, _min, _max)));
		_dragFace = face;
		_dragMin = _min;
		_dragMax = _max;
		_dragStartT = AlongAxis(camera, screenPosition, 0f);
		DrawWire(_dragMin, _dragMax);
	}

	/// <summary>The face follows the cursor along its normal (onto the grid when snapping).</summary>
	public void DragTo(Camera3D camera, Vector2 screenPosition)
	{
		if (!Dragging || _host == null || !GodotObject.IsInstanceValid(_host))
			return;
		if (_dragVertex >= 0)
		{
			VertexDragTo(camera, screenPosition);
			return;
		}
		float t = AlongAxis(camera, screenPosition, _dragStartT) - _dragStartT;
		float by = t / _dragScale;
		MoveFace(_min, _max, _dragFace, by, LevelViewerTransformSnap.GridSize, out _dragMin, out _dragMax);
		DrawWire(_dragMin, _dragMax);
	}

	/// <summary>Let go: the new bounds when the face moved, for OpenCAGE to rebuild the brush with.</summary>
	public bool EndDrag(out int face, out Vector3 min, out Vector3 max)
	{
		face = _dragFace;
		min = _dragMin;
		max = _dragMax;
		_dragFace = -1;
		if (face < 0)
			return false;
		bool moved = (min - _min).LengthSquared() > 1e-8f || (max - _max).LengthSquared() > 1e-8f;
		if (!moved)
		{
			HideWire();
			return false;
		}
		//Shown at the new size until the rebuilt brush comes back
		_min = min;
		_max = max;
		_sentAt = DateTime.Now;
		return true;
	}

	/// <summary>Escape, or the selection moving on, mid-drag: nothing changes.</summary>
	public void CancelDrag()
	{
		_dragFace = -1;
		_dragVertex = -1;
		_dragVertices = null;
		HideWire();
		if (_active && _kind == 2) DrawEdges(_vertices);
	}

	/* Where along the dragged face's axis the cursor's ray passes closest, in world metres from the face centre at the press */
	private float AlongAxis(Camera3D camera, Vector2 screenPosition, float fallback)
	{
		Vector3 o = camera.ProjectRayOrigin(screenPosition);
		Vector3 d = camera.ProjectRayNormal(screenPosition);
		Vector3 w = _dragOriginWorld - o;
		float b = _dragAxisWorld.Dot(d);
		float denominator = 1f - b * b;
		//Looking straight down the axis: it cannot be told where along it the cursor is
		if (Mathf.Abs(denominator) < 1e-4f)
			return fallback;
		float dd = _dragAxisWorld.Dot(w), e = d.Dot(w);
		return (b * e - dd) / denominator;
	}

	/* A box face pushed out along its normal by `by` metres (negative pulls it in), the moved coordinate onto the grid, never past
	   the opposite face - as OpenCAGE's BrushEditing.MoveFace does it */
	private static void MoveFace(Vector3 min, Vector3 max, int face, float by, float snap, out Vector3 newMin, out Vector3 newMax)
	{
		newMin = min;
		newMax = max;
		switch (face)
		{
			case 0: newMax.X = Mathf.Max(min.X + MinimumSize, LevelViewerTransformSnap.SnapValue(max.X + by, snap)); break;
			case 1: newMin.X = Mathf.Min(max.X - MinimumSize, LevelViewerTransformSnap.SnapValue(min.X - by, snap)); break;
			case 2: newMax.Y = Mathf.Max(min.Y + MinimumSize, LevelViewerTransformSnap.SnapValue(max.Y + by, snap)); break;
			case 3: newMin.Y = Mathf.Min(max.Y - MinimumSize, LevelViewerTransformSnap.SnapValue(min.Y - by, snap)); break;
			case 4: newMax.Z = Mathf.Max(min.Z + MinimumSize, LevelViewerTransformSnap.SnapValue(max.Z + by, snap)); break;
			case 5: newMin.Z = Mathf.Min(max.Z - MinimumSize, LevelViewerTransformSnap.SnapValue(min.Z - by, snap)); break;
		}
	}

	private IEnumerable<int> Faces()
	{
		//A plane has no height to change: its four edges. A mesh has vertex handles instead
		return _kind == 2 ? new int[0] : _kind == 1 ? new[] { 0, 1, 4, 5 } : new[] { 0, 1, 2, 3, 4, 5 };
	}

	private static Vector3 FaceCentre(int face, Vector3 min, Vector3 max)
	{
		Vector3 c = (min + max) * 0.5f;
		switch (face)
		{
			case 0: c.X = max.X; break;
			case 1: c.X = min.X; break;
			case 2: c.Y = max.Y; break;
			case 3: c.Y = min.Y; break;
			case 4: c.Z = max.Z; break;
			case 5: c.Z = min.Z; break;
		}
		return c;
	}

	private bool ResolveHost(AlienScene scene)
	{
		_host = null;
		if (scene == null)
			return false;
		List<Node3D> nodes = new List<Node3D>();
		List<uint> ids = new List<uint>();
		scene.GetSelectedEntities(nodes, ids);
		for (int i = 0; i < nodes.Count && i < ids.Count; i++)
		{
			if (ids[i] != _entityId)
				continue;
			_host = nodes[i];
			break;
		}
		return _host != null;
	}

	#region Drawing
	private void DrawOverlay(Camera3D camera, CanvasLayer hudLayer)
	{
		if (camera == null || hudLayer == null || _host == null || !GodotObject.IsInstanceValid(_host))
		{
			HideOverlay();
			return;
		}
		EnsureOverlay(hudLayer);
		_overlay.Visible = true;
		HashSet<int> shown = new HashSet<int>(Faces());
		Vector3 min = Dragging ? _dragMin : _min, max = Dragging ? _dragMax : _max;
		for (int f = 0; f < 6; f++)
		{
			Panel dot = _dots[f];
			if (!shown.Contains(f))
			{
				dot.Visible = false;
				continue;
			}
			Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(FaceCentre(f, min, max)));
			if (camera.IsPositionBehind(world))
			{
				dot.Visible = false;
				continue;
			}
			dot.AddThemeStyleboxOverride(PanelStyleName, f == _dragFace ? _activeStyle : _dotStyle);
			dot.Size = new Vector2(HandleSize, HandleSize);
			dot.Position = camera.UnprojectPosition(world) - dot.Size * 0.5f;
			dot.Visible = true;
		}

		//A mesh brush: a handle on each vertex, where the drag has it
		Vector3[] vertices = _kind == 2 ? (_dragVertices ?? _vertices) : new Vector3[0];
		while (_vertexDots.Count < vertices.Length)
		{
			Panel dot = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_overlay.AddChild(dot);
			_vertexDots.Add(dot);
		}
		for (int v = 0; v < _vertexDots.Count; v++)
		{
			Panel dot = _vertexDots[v];
			if (v >= vertices.Length)
			{
				dot.Visible = false;
				continue;
			}
			Vector3 world = _host.ToGlobal(CathodeCoordinates.PositionToGodot(vertices[v]));
			if (camera.IsPositionBehind(world))
			{
				dot.Visible = false;
				continue;
			}
			dot.AddThemeStyleboxOverride(PanelStyleName, v == _dragVertex ? _activeStyle : _dotStyle);
			dot.Size = new Vector2(VertexHandleSize, VertexHandleSize);
			dot.Position = camera.UnprojectPosition(world) - dot.Size * 0.5f;
			dot.Visible = true;
		}
	}

	private void EnsureOverlay(CanvasLayer hudLayer)
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.GetParent() == hudLayer)
			return;
		_overlay = new Control { Name = "BrushHandles", MouseFilter = Control.MouseFilterEnum.Ignore };
		_overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		hudLayer.AddChild(_overlay);
		_vertexDots.Clear();
		_dotStyle = MakeStyle(HandleColour);
		_activeStyle = MakeStyle(ActiveColour);
		for (int f = 0; f < 6; f++)
		{
			_dots[f] = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
			_overlay.AddChild(_dots[f]);
		}
	}

	private static StyleBoxFlat MakeStyle(Color colour)
	{
		StyleBoxFlat style = new StyleBoxFlat { BgColor = colour, BorderColor = HandleEdgeColour };
		style.SetBorderWidthAll(2);
		return style;
	}

	private void HideOverlay()
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			_overlay.Visible = false;
	}

	/* The bounds a drag has reached, as a wire box over everything, under the brush's node so it moves and turns with it */
	private void DrawWire(Vector3 min, Vector3 max)
	{
		if (!EnsureWire())
			return;
		Vector3[] c = new Vector3[8];
		for (int i = 0; i < 8; i++)
			c[i] = CathodeCoordinates.PositionToGodot(new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
		int[] edges = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
		_wireMesh.ClearSurfaces();
		_wireMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
		foreach (int e in edges)
			_wireMesh.SurfaceAddVertex(c[e]);
		_wireMesh.SurfaceEnd();
	}

	/* A mesh brush's edges (as a drag has its vertices), drawn over everything while it is selected */
	private void DrawEdges(Vector3[] vertices)
	{
		if (vertices == null || _edges.Length < 2 || !EnsureWire())
			return;
		_wireMesh.ClearSurfaces();
		_wireMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
		for (int i = 0; i + 1 < _edges.Length; i += 2)
		{
			if (_edges[i] < 0 || _edges[i] >= vertices.Length || _edges[i + 1] < 0 || _edges[i + 1] >= vertices.Length)
				continue;
			_wireMesh.SurfaceAddVertex(CathodeCoordinates.PositionToGodot(vertices[_edges[i]]));
			_wireMesh.SurfaceAddVertex(CathodeCoordinates.PositionToGodot(vertices[_edges[i + 1]]));
		}
		_wireMesh.SurfaceEnd();
	}

	private bool EnsureWire()
	{
		if (_host == null || !GodotObject.IsInstanceValid(_host))
			return false;
		if (_wire == null || !GodotObject.IsInstanceValid(_wire) || _wire.GetParent() != _host)
		{
			if (_wire != null && GodotObject.IsInstanceValid(_wire))
				_wire.QueueFree();
			_wireMesh = new ImmediateMesh();
			_wire = new MeshInstance3D { Name = "BrushResizeWire", Mesh = _wireMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
			_wire.MaterialOverride = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				NoDepthTest = true,
				AlbedoColor = ActiveColour,
			};
			_host.AddChild(_wire);
		}
		_wire.Visible = true;
		return true;
	}

	private void HideWire()
	{
		if (_wire != null && GodotObject.IsInstanceValid(_wire))
			_wire.Visible = false;
	}
	#endregion
}
