using System;
using System.Collections.Generic;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using OpenCAGE.UnityConnection;

/// <summary>
/// The spline editor's Edit in Viewport mode, viewer side (SPLINE_EDIT). The SplinePath being edited draws the editor's
/// working points in every placement (SplinePathPreview's edit override); in the placement where it is selected each point
/// gets a numbered handle on the HUD, a click on a handle selects that point, and the transform gizmo moves the selected
/// one. OpenCAGE owns the working copy - a move goes back to it and comes round again as the whole spline - and nothing
/// here writes the level, so leaving the mode at any moment puts the saved spline back.
/// </summary>
public sealed class LevelViewerSplineEdit
{
	private static readonly Color HandleColour = new Color(1f, 0.55f, 0.1f);
	private static readonly Color SelectedColour = new Color(0.25f, 0.85f, 1f);
	private static readonly Color HandleEdgeColour = new Color(0.07f, 0.08f, 0.1f);
	private const float HandleSize = 12f;
	private const float SelectedHandleSize = 16f;
	private const float PickRadius = 14f; //on screen, from a handle's centre
	//Held rather than passed as literals - see LevelViewerPick.PickableGroupName
	private static readonly StringName PanelStyleName = new StringName("panel");
	private static readonly StringName FontColourName = new StringName("font_color");
	private static readonly StringName FontOutlineColourName = new StringName("font_outline_color");
	private static readonly StringName OutlineSizeName = new StringName("outline_size");

	private bool _active;
	private uint _entityId;
	private uint _compositeId;
	private bool _loop;
	private int _selected = -1;
	private readonly List<Vector3> _positions = new List<Vector3>(); //Godot space, local to the entity like the stored points
	private readonly List<Vector3> _rotations = new List<Vector3>(); //Godot euler degrees
	private FunctionEntity _entity;

	private Node3D _host;       //the SplinePath's node in the placement being edited (where it is selected)
	private Node3D _pointProxy; //what the gizmo moves: the selected point, as a child of the host

	private Control _overlay;
	private readonly List<Panel> _dots = new List<Panel>();
	private readonly List<Label> _labels = new List<Label>();
	private StyleBoxFlat _dotStyle;
	private StyleBoxFlat _selectedDotStyle;

	/// <summary>The gizmo should be put on <see cref="GizmoTarget"/> again: the selected point, or where it is, changed.</summary>
	public Action RetargetGizmo;

	/// <summary>Whether the gizmo is mid-drag - the point being dragged follows it rather than the packets.</summary>
	public Func<bool> IsGizmoDragging;

	public bool Active => _active;
	public uint EntityId => _entityId;
	public int Selected => _selected;

	/// <summary>The selected point, for the gizmo to move; null when no point is selected or the spline isn't on screen.</summary>
	public Node3D GizmoTarget => _active && _pointProxy != null && GodotObject.IsInstanceValid(_pointProxy) ? _pointProxy : null;

	/// <summary>
	/// The editor's state, from SPLINE_EDIT: the whole working spline, in Cathode space. Off puts the saved spline back.
	/// Main thread.
	/// </summary>
	public void Apply(AlienScene scene, bool active, uint entityId, uint compositeId, List<SplineEditPoint> points, bool loop, int selected)
	{
		if (!active || entityId == 0)
		{
			End();
			return;
		}

		bool entityChanged = !_active || entityId != _entityId;
		if (entityChanged)
		{
			SplinePathPreview.ClearEditOverride();
			_entity = null;
			_host = null;
		}

		_active = true;
		_entityId = entityId;
		_compositeId = compositeId;
		_loop = loop;

		//Mid-drag the dragged point is the gizmo's, and the move it ends with is what OpenCAGE will send back
		bool dragging = IsGizmoDragging?.Invoke() == true;
		Vector3 draggedPosition = Vector3.Zero, draggedRotation = Vector3.Zero;
		bool keepDragged = dragging && _selected >= 0 && _selected < _positions.Count && _selected == selected;
		if (keepDragged)
		{
			draggedPosition = _positions[_selected];
			draggedRotation = _rotations[_selected];
		}

		_positions.Clear();
		_rotations.Clear();
		if (points != null)
		{
			foreach (SplineEditPoint point in points)
			{
				_positions.Add(CathodeCoordinates.PositionToGodot(ToVector(point?.position)));
				_rotations.Add(CathodeCoordinates.EulerDegreesToGodot(ToVector(point?.rotation)));
			}
		}
		int previousSelected = _selected;
		_selected = selected >= 0 && selected < _positions.Count ? selected : -1;
		if (keepDragged && _selected >= 0)
		{
			_positions[_selected] = draggedPosition;
			_rotations[_selected] = draggedRotation;
		}

		ResolveHost(scene);
		PushOverride();
		bool proxyChanged = SyncProxy(!keepDragged);
		if (proxyChanged || entityChanged || previousSelected != _selected)
			RetargetGizmo?.Invoke();
	}

	/// <summary>Leave the mode: the saved spline is drawn again and the gizmo goes back to the selection. Main thread.</summary>
	public void End()
	{
		if (!_active)
			return;

		_active = false;
		SplinePathPreview.ClearEditOverride();
		FreeProxy();
		HideOverlay();
		_positions.Clear();
		_rotations.Clear();
		_selected = -1;
		_entity = null;
		_host = null;
		_entityId = 0;
		RetargetGizmo?.Invoke();
	}

	/// <summary>
	/// Per frame: find the spline again if the scene was rebuilt or the selection moved, let a drag in progress carry the
	/// point (and every placement's drawing of it) along, and draw the handles where the camera now puts them.
	/// </summary>
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
			_pointProxy = null; //went with the host
			if (ResolveHost(scene))
			{
				PushOverride();
				SyncProxy(true);
				RetargetGizmo?.Invoke();
			}
		}

		if (_selected >= 0 && _selected < _positions.Count && GizmoTarget != null && IsGizmoDragging?.Invoke() == true)
		{
			Vector3 position = _pointProxy.Position;
			Vector3 rotation = _pointProxy.RotationDegrees;
			if (position != _positions[_selected] || rotation != _rotations[_selected])
			{
				_positions[_selected] = position;
				_rotations[_selected] = rotation;
				PushOverride();
			}
		}

		DrawOverlay(camera, hudLayer);
	}

	/// <summary>
	/// The handle under a click, if any - the nearest within <see cref="PickRadius"/> pixels. Never the selected point: its
	/// handle is under the gizmo's centre, and a press there is the gizmo's.
	/// </summary>
	public bool TryPick(Camera3D camera, Vector2 screenPosition, out int index)
	{
		index = -1;
		if (!_active || camera == null || _host == null || !GodotObject.IsInstanceValid(_host))
			return false;

		float best = PickRadius * PickRadius;
		for (int i = 0; i < _positions.Count; i++)
		{
			if (i == _selected)
				continue;
			Vector3 world = _host.ToGlobal(_positions[i]);
			if (camera.IsPositionBehind(world))
				continue;
			float distance = screenPosition.DistanceSquaredTo(camera.UnprojectPosition(world));
			if (distance <= best)
			{
				best = distance;
				index = i;
			}
		}
		return index >= 0;
	}

	/// <summary>
	/// The gizmo let go of the selected point: where it now is, in Cathode space, for OpenCAGE's working copy. The point is
	/// drawn there at once rather than waiting for the copy to come back.
	/// </summary>
	public bool TryCommitMove(Vector3 godotPosition, Vector3 godotRotationDegrees, out int index, out SplineEditPoint point)
	{
		index = _selected;
		point = null;
		if (!_active || _selected < 0 || _selected >= _positions.Count)
			return false;

		_positions[_selected] = godotPosition;
		_rotations[_selected] = godotRotationDegrees;
		PushOverride();

		Vector3 position = CathodeCoordinates.PositionFromGodot(godotPosition);
		Vector3 rotation = CathodeCoordinates.EulerDegreesFromGodot(godotRotationDegrees);
		point = new SplineEditPoint
		{
			position = new float[] { position.X, position.Y, position.Z },
			rotation = new float[] { rotation.X, rotation.Y, rotation.Z },
		};
		return true;
	}

	/* The SplinePath where it is selected: that placement is the one being edited (the others draw the same points). */
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
		if (_host == null)
			return false;

		if (_entity == null)
			_entity = FindEntity(scene);
		return true;
	}

	private FunctionEntity FindEntity(AlienScene scene)
	{
		foreach (Node child in _host.GetChildren())
		{
			if (child is SplinePathPreview preview && preview.Entity != null && preview.Entity.shortGUID.AsUInt32 == _entityId)
				return preview.Entity;
		}
		Composite composite = scene.Content?.Level?.Commands?.GetComposite(new ShortGuid(_compositeId));
		return composite?.GetEntityByID(new ShortGuid(_entityId)) as FunctionEntity;
	}

	private void PushOverride()
	{
		if (_entity != null)
			SplinePathPreview.SetEditOverride(_entity, _positions, _loop);
	}

	/* The proxy the gizmo moves sits on the selected point, under the host so its local transform is the point's own.
	   True when the node itself changed (made, freed or moved to another host), which the gizmo has to be told about. */
	private bool SyncProxy(bool placeOnPoint)
	{
		if (_host == null || !GodotObject.IsInstanceValid(_host) || _selected < 0 || _selected >= _positions.Count)
			return FreeProxy();

		bool changed = false;
		if (_pointProxy == null || !GodotObject.IsInstanceValid(_pointProxy) || _pointProxy.GetParent() != _host)
		{
			FreeProxy();
			_pointProxy = new Node3D { Name = "SplineEditPoint" };
			_host.AddChild(_pointProxy);
			changed = true;
			placeOnPoint = true;
		}
		if (placeOnPoint)
		{
			_pointProxy.Position = _positions[_selected];
			_pointProxy.RotationDegrees = _rotations[_selected];
		}
		return changed;
	}

	private bool FreeProxy()
	{
		if (_pointProxy == null)
			return false;
		if (GodotObject.IsInstanceValid(_pointProxy))
			_pointProxy.QueueFree();
		_pointProxy = null;
		return true;
	}

	private void DrawOverlay(Camera3D camera, CanvasLayer hudLayer)
	{
		if (camera == null || hudLayer == null || _host == null || !GodotObject.IsInstanceValid(_host))
		{
			HideOverlay();
			return;
		}

		EnsureOverlay(hudLayer);
		_overlay.Visible = true;
		while (_dots.Count < _positions.Count)
			AddHandle();

		for (int i = 0; i < _dots.Count; i++)
		{
			Panel dot = _dots[i];
			Label label = _labels[i];
			if (i >= _positions.Count)
			{
				dot.Visible = false;
				label.Visible = false;
				continue;
			}

			Vector3 world = _host.ToGlobal(_positions[i]);
			if (camera.IsPositionBehind(world))
			{
				dot.Visible = false;
				label.Visible = false;
				continue;
			}

			Vector2 screen = camera.UnprojectPosition(world);
			bool selected = i == _selected;
			float size = selected ? SelectedHandleSize : HandleSize;
			dot.AddThemeStyleboxOverride(PanelStyleName, selected ? _selectedDotStyle : _dotStyle);
			dot.Size = new Vector2(size, size);
			dot.Position = screen - dot.Size * 0.5f;
			dot.Visible = true;

			label.Text = i.ToString();
			label.AddThemeColorOverride(FontColourName, selected ? SelectedColour : HandleColour);
			label.Position = screen + new Vector2(size * 0.5f + 2f, -size - 6f);
			label.Visible = true;
		}
	}

	private void EnsureOverlay(CanvasLayer hudLayer)
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.GetParent() == hudLayer)
			return;

		_dots.Clear();
		_labels.Clear();
		_overlay = new Control { Name = "SplineEditHandles", MouseFilter = Control.MouseFilterEnum.Ignore };
		_overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		hudLayer.AddChild(_overlay);

		_dotStyle = MakeDotStyle(HandleColour, HandleSize);
		_selectedDotStyle = MakeDotStyle(SelectedColour, SelectedHandleSize);
	}

	private static StyleBoxFlat MakeDotStyle(Color colour, float size)
	{
		int radius = (int)Mathf.Ceil(size * 0.5f);
		StyleBoxFlat style = new StyleBoxFlat { BgColor = colour, BorderColor = HandleEdgeColour };
		style.SetBorderWidthAll(2);
		style.SetCornerRadiusAll(radius);
		return style;
	}

	private void AddHandle()
	{
		Panel dot = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
		_overlay.AddChild(dot);
		_dots.Add(dot);

		Label label = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
		label.AddThemeColorOverride(FontOutlineColourName, HandleEdgeColour);
		label.AddThemeConstantOverride(OutlineSizeName, 4);
		_overlay.AddChild(label);
		_labels.Add(label);
	}

	private void HideOverlay()
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			_overlay.Visible = false;
	}

	private static Vector3 ToVector(float[] values)
	{
		if (values == null || values.Length < 3)
			return Vector3.Zero;
		return new Vector3(values[0], values[1], values[2]);
	}
}
