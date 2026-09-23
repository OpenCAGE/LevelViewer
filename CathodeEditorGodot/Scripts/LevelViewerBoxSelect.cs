using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using System.Collections.Generic;

/// <summary>
/// Box (marquee) selection in the viewport. A left press that isn't on a gizmo handle waits to see
/// whether it is a click or a drag; once the mouse has moved <see cref="DragThresholdPixels"/> it is a
/// box, drawn over the view until the button comes up, and letting go selects what the box holds.
/// </summary>
/// <remarks>
/// <para>What a box takes is worked out per entity a click would select - the entity in the composite
/// on screen, or with advanced deep select the entity the click would land on - not per mesh, so a
/// composite instance is judged by all of its geometry together. An entity is taken when the middle of
/// its on-screen bounds is inside the box and those bounds would fit inside the box. A prop then only
/// has to be mostly boxed, while level geometry bigger than the box (a floor, a wall, an environment
/// composite whose meshes run through every room) is left alone unless the box is big enough to hold
/// all of it. Anything reaching behind the camera can't be held by a box at all, which also keeps out
/// the room the camera is standing in.</para>
/// <para>The candidates are the pick registry's in-scope owners, so a box leaves out everything a click
/// can't land on (outside the composite focus, filtered out, hidden with H), and an entity also needs
/// some of its geometry drawn inside the box. Like most editors' marquee, it does not test occlusion.</para>
/// </remarks>
public sealed class LevelViewerBoxSelect
{
	/// <summary>How far the mouse has to move from the press before it is a box rather than a click.</summary>
	public const float DragThresholdPixels = 6f;

	private static readonly Color FillColour = new Color(0.55f, 0.75f, 1f, 0.12f);
	private static readonly Color EdgeColour = new Color(0.62f, 0.8f, 1f, 0.95f);
	//Held rather than passed as a literal - see LevelViewerPick.PickableGroupName
	private static readonly StringName PanelStyleName = new StringName("panel");

	private bool _armed;
	private bool _active;
	private Vector2 _pressPosition;
	private Vector2 _currentPosition;
	private CommandsEditorConnection.SelectionChange _change;
	private Panel _panel;

	/// <summary>The left button went down on something that isn't a gizmo handle: a click or a box, not known yet.</summary>
	public bool IsArmed => _armed;

	/// <summary>The press has turned into a box.</summary>
	public bool IsActive => _active;

	public Vector2 PressPosition => _pressPosition;

	/// <summary>What the press does to the selection, from the modifiers held when it went down.</summary>
	public CommandsEditorConnection.SelectionChange Change => _change;

	public void Arm(Vector2 pressPosition, CommandsEditorConnection.SelectionChange change)
	{
		End();
		_armed = true;
		_pressPosition = pressPosition;
		_currentPosition = pressPosition;
		_change = change;
	}

	/// <summary>Follow the mouse. True once the press is a box.</summary>
	public bool Update(Vector2 position, Viewport viewport, CanvasLayer layer)
	{
		if (!_armed)
			return false;

		_currentPosition = position;
		if (!_active && position.DistanceTo(_pressPosition) < DragThresholdPixels)
			return false;

		_active = true;
		Draw(GetBox(viewport), layer);
		return true;
	}

	/// <summary>The box as it stands, corner to corner, cut to the view.</summary>
	public Rect2 GetBox(Viewport viewport)
	{
		Vector2 min = _pressPosition.Min(_currentPosition);
		Vector2 max = _pressPosition.Max(_currentPosition);
		if (viewport != null)
		{
			Vector2 size = viewport.GetVisibleRect().Size;
			min = min.Clamp(Vector2.Zero, size);
			max = max.Clamp(Vector2.Zero, size);
		}

		return new Rect2(min, max - min);
	}

	/// <summary>Forget the press, and take the box off the screen if one was drawn.</summary>
	public void End()
	{
		_armed = false;
		_active = false;
		if (_panel != null && GodotObject.IsInstanceValid(_panel))
			_panel.Visible = false;
	}

	private void Draw(Rect2 box, CanvasLayer layer)
	{
		if (_panel == null || !GodotObject.IsInstanceValid(_panel))
		{
			_panel = null;
			if (layer == null || !GodotObject.IsInstanceValid(layer))
				return;

			StyleBoxFlat style = new StyleBoxFlat
			{
				BgColor = FillColour,
				BorderColor = EdgeColour,
				BorderWidthLeft = 1,
				BorderWidthTop = 1,
				BorderWidthRight = 1,
				BorderWidthBottom = 1,
			};
			_panel = new Panel
			{
				Name = "BoxSelect",
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			_panel.AddThemeStyleboxOverride(PanelStyleName, style);
			layer.AddChild(_panel);
		}

		_panel.Position = box.Position;
		_panel.Size = box.Size;
		_panel.Visible = true;
	}

	/// <summary>One entity a box takes: what a click on it would pick, and how far its middle is from the box's.</summary>
	public readonly struct Hit
	{
		public Hit(LevelViewerPick.SelectionTarget target, float distanceFromCentre)
		{
			Target = target;
			DistanceFromCentre = distanceFromCentre;
		}

		public LevelViewerPick.SelectionTarget Target { get; }
		public float DistanceFromCentre { get; }
	}

	private sealed class Candidate
	{
		public Vector2 Min = new Vector2(float.MaxValue, float.MaxValue);
		public Vector2 Max = new Vector2(float.MinValue, float.MinValue);
		public bool ReachesBehindCamera;
		public bool DrawnInBox;
	}

	/// <summary>
	/// Everything <paramref name="box"/> takes, nearest the middle of it first. <paramref name="deepest"/>
	/// is advanced deep select: each entity is the one a click would land on, rather than the entity in
	/// the composite on screen that holds it. <paramref name="ownerCount"/> is how many pick owners
	/// were looked at, for the log.
	/// </summary>
	public static void CollectHits(
		AlienScene scene,
		Camera3D camera,
		Rect2 box,
		uint activeCompositeId,
		bool deepest,
		List<Hit> hits,
		out int ownerCount)
	{
		ownerCount = 0;
		if (scene == null || camera == null || hits == null || activeCompositeId == 0
			|| box.Size.X <= 0f || box.Size.Y <= 0f)
		{
			return;
		}

		Node3D contentRoot = scene.ParentNode;
		IReadOnlyDictionary<Node3D, Entity> nodeEntities = scene.NodeEntities;
		Commands commands = scene.Content?.Level?.Commands;
		Viewport viewport = camera.GetViewport();
		if (contentRoot == null || !GodotObject.IsInstanceValid(contentRoot) || nodeEntities == null
			|| commands == null || viewport == null)
		{
			return;
		}

		List<(Node3D Owner, Aabb Bounds)> owners = new List<(Node3D Owner, Aabb Bounds)>();
		LevelViewerPick.CollectScopedPickOwnerBounds(contentRoot, commands, owners);
		ownerCount = owners.Count;

		Vector2 viewportSize = viewport.GetVisibleRect().Size;
		Projection projection = camera.GetCameraProjection();
		Transform3D view = camera.GetCameraTransform().AffineInverse();
		float near = camera.Near;

		/* Every owner joins the entity a click on it would select, so an entity's bounds on screen are
		   those of all its geometry, in the box or not - an environment composite has meshes all over
		   the level, and each is what tells the box it's far too big to hold. Owners are walked up to
		   their entity through the tree, which a level shares a great deal of, so each node's answer
		   is kept for the owners after it. */
		Dictionary<Node3D, Candidate> candidates = new Dictionary<Node3D, Candidate>();
		Dictionary<Node, Node3D> resolved = new Dictionary<Node, Node3D>();
		List<Node> walked = new List<Node>();
		for (int i = 0; i < owners.Count; i++)
		{
			(Node3D owner, Aabb bounds) = owners[i];
			Node3D key = deepest
				? LevelViewerPick.ResolveNearestEntityNode(owner, nodeEntities) ?? owner
				: ResolveActiveCompositeEntityNode(owner, contentRoot, nodeEntities, activeCompositeId, resolved, walked);
			if (key == null)
				continue;

			if (!candidates.TryGetValue(key, out Candidate candidate))
			{
				candidate = new Candidate();
				candidates.Add(key, candidate);
			}

			if (candidate.ReachesBehindCamera)
				continue;

			if (!TryProjectBounds(bounds, projection, view, near, viewportSize, out Vector2 min, out Vector2 max))
			{
				candidate.ReachesBehindCamera = true;
				continue;
			}

			candidate.Min = candidate.Min.Min(min);
			candidate.Max = candidate.Max.Max(max);
			if (!candidate.DrawnInBox
				&& new Rect2(min, max - min).Intersects(box, includeBorders: true)
				&& LevelViewerPick.IsOwnerDrawn(owner))
			{
				candidate.DrawnInBox = true;
			}
		}

		Vector2 centre = box.GetCenter();
		foreach (KeyValuePair<Node3D, Candidate> entry in candidates)
		{
			Candidate candidate = entry.Value;
			if (candidate.ReachesBehindCamera || !candidate.DrawnInBox)
				continue;

			Vector2 size = candidate.Max - candidate.Min;
			Vector2 middle = (candidate.Min + candidate.Max) * 0.5f;
			if (size.X > box.Size.X || size.Y > box.Size.Y || !box.HasPoint(middle))
				continue;

			LevelViewerPick.SelectionTarget? target = LevelViewerPick.BuildSelectionTarget(entry.Key, contentRoot, nodeEntities);
			if (!target.HasValue)
				continue;

			hits.Add(new Hit(target.Value, middle.DistanceTo(centre)));
		}

		hits.Sort((a, b) => a.DistanceFromCentre.CompareTo(b.DistanceFromCentre));
	}

	/* The entity node in the active composite that a click on this owner selects - the same one
	   LevelViewerPick.TryBuildActiveCompositeSelectionPath settles on: the deepest entity on the way up
	   that the active composite owns. A composite never contains itself, so that is also the first one
	   met walking up. Null when the owner isn't under the active composite at all. */
	private static Node3D ResolveActiveCompositeEntityNode(
		Node3D owner,
		Node contentRoot,
		IReadOnlyDictionary<Node3D, Entity> nodeEntities,
		uint activeCompositeId,
		Dictionary<Node, Node3D> resolved,
		List<Node> walked)
	{
		walked.Clear();
		Node3D found = null;
		Node current = LevelViewerPick.ResolveNearestEntityNode(owner, nodeEntities) ?? owner;
		while (current != null && current != contentRoot)
		{
			if (resolved.TryGetValue(current, out found))
				break;

			walked.Add(current);
			if (current is Node3D node3D
				&& nodeEntities.ContainsKey(node3D)
				&& AlienScene.TryGetOwnerCompositeId(node3D, out uint ownerCompositeId)
				&& ownerCompositeId == activeCompositeId)
			{
				found = node3D;
				break;
			}

			current = current.GetParent();
		}

		for (int i = 0; i < walked.Count; i++)
			resolved[walked[i]] = found;

		return found;
	}

	/* The screen rectangle around an AABB's eight corners, worked out here the way
	   Camera3D.UnprojectPosition does rather than asking it eight times per owner across a whole level.
	   False when any corner is at or behind the near plane: such bounds have no rectangle on screen. */
	private static bool TryProjectBounds(
		Aabb bounds,
		Projection projection,
		Transform3D view,
		float near,
		Vector2 viewportSize,
		out Vector2 min,
		out Vector2 max)
	{
		min = new Vector2(float.MaxValue, float.MaxValue);
		max = new Vector2(float.MinValue, float.MinValue);
		for (int i = 0; i < 8; i++)
		{
			//Camera space looks down -Z
			Vector3 eye = view * bounds.GetEndpoint(i);
			if (-eye.Z < near)
				return false;

			Vector4 clip = projection * new Vector4(eye.X, eye.Y, eye.Z, 1f);
			if (Mathf.IsZeroApprox(clip.W))
				return false;

			Vector2 screen = new Vector2(
				(clip.X / clip.W * 0.5f + 0.5f) * viewportSize.X,
				(-clip.Y / clip.W * 0.5f + 0.5f) * viewportSize.Y);
			min = min.Min(screen);
			max = max.Max(screen);
		}

		return true;
	}
}
