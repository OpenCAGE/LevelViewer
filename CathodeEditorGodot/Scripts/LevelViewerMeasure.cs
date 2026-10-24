using CATHODE;
using Godot;

/// <summary>
/// The viewport's ruler (OpenCAGE's Measure, issue 722). While it is on, a left click puts down a point on the level's
/// geometry instead of selecting, a second click puts down another, and the line between them is drawn over the view
/// with the distance and its vertical and horizontal parts; a third click starts a new measurement. Until the second
/// point is down, the line runs to whatever surface is under the cursor. Holding Shift takes the selected entity's
/// origin instead of the point clicked - and follows that entity if it is moved afterwards - and holding V (or having
/// the Transform Snap menu's vertex snap on) the nearest vertex of the mesh clicked. A click on an entity's icon takes
/// that entity's origin, as vertex snapping does.
/// </summary>
/// <remarks>
/// Points are kept in the scene root's space, not Godot's global space: a populate moves the content to sit near the
/// origin (AlienScene.RecenterContentOrigin), and a point kept globally would be left behind. That is the level's space
/// with Z negated (see CathodeCoordinates), and Y is up in both, so the vertical part is the game's as it stands. The
/// line is drawn in 2D on the camera's HUD layer, so the geometry it measures never hides it. Anything that populates
/// the scene again (another composite, a level load) clears it.
/// </remarks>
public sealed class LevelViewerMeasure
{
	private static readonly Color LineColour = new Color(1f, 0.78f, 0.25f);
	private static readonly Color VerticalColour = new Color(0.45f, 0.9f, 0.45f, 0.9f);
	private static readonly Color HorizontalColour = new Color(0.82f, 0.85f, 0.9f, 0.8f);
	private static readonly Color PointColour = new Color(1f, 0.78f, 0.25f);
	private static readonly Color PointEdgeColour = new Color(0.07f, 0.08f, 0.1f);
	private const float PreviewAlpha = 0.6f;
	private const float PointSize = 10f;
	private const float LegMinimumLength = 0.001f; //a leg shorter than a millimetre is not drawn
	private const float LabelGap = 12f;            //on screen, between the label and the line or point it is for
	//Held rather than passed as a literal - see LevelViewerPick.PickableGroupName
	private static readonly StringName PanelStyleName = new StringName("panel");
	private static readonly StringName FontColourName = new StringName("font_color");
	private static readonly StringName FontSizeName = new StringName("font_size");
	private static readonly StringName SeparationName = new StringName("separation");

	private struct MeasurePoint
	{
		public Vector3 Local;  //in the scene root's space
		public Node3D Anchor;  //the entity whose origin it is (Shift), followed while it lasts; null for a point clicked
	}

	private bool _active;
	private int _pointCount; //0, 1 or 2
	private MeasurePoint _first;
	private MeasurePoint _second;
	private Node3D _root;          //the scene root the points are in
	private int _contentGeneration; //and the populate they were put down in

	/* Where the line runs while only the first point is down: the surface under the cursor, looked for at most once every
	   HoverPickIntervalMs. A pick walks every pickable in scope - measured on BSP_TORRENS, 1.5-20 ms once a mesh's
	   triangles are cached and up to 46 ms the first time the cursor crosses one - so not every frame of a camera flight. */
	private const ulong HoverPickIntervalMs = 50;
	private ulong _lastHoverPickMs;
	private bool _hoverPending;
	private Vector2 _hoverScreen;
	private bool _hoverKnown;       //a cursor position has come since measuring started
	private bool _hoverSelectedOrigin;
	private bool _hoverVertexSnap;
	private bool _hoverValid;
	private MeasurePoint _hover;
	private Transform3D _hoverCameraTransform;

	private Control _overlay;
	private Line2D _line;
	private Line2D _verticalLeg;
	private Line2D _horizontalLeg;
	private Panel _firstDot;
	private Panel _secondDot;
	private PanelContainer _labelPanel;
	private Label _distanceLabel;
	private Label _verticalLabel;
	private Label _horizontalLabel;

	/// <summary>Measuring is on: left clicks put down points.</summary>
	public bool Active => _active;

	/// <summary>Measuring came on or went off. Either way, any measurement there was is cleared.</summary>
	public void SetActive(bool active)
	{
		_active = active;
		Clear();
	}

	/// <summary>Forget the points, and take the line off the screen.</summary>
	public void Clear()
	{
		_pointCount = 0;
		_first = default;
		_second = default;
		_root = null;
		_hoverPending = false;
		_hoverKnown = false;
		_hoverValid = false;
		_hover = default;
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			_overlay.Visible = false;
	}

	/// <summary>
	/// A left click while measuring: the first point, the second, or - with both down - the first of a new measurement.
	/// <paramref name="selectedOrigin"/> takes the selected entity's origin (Shift held), and with nothing selected the
	/// point clicked; <paramref name="vertexSnap"/> the nearest vertex of the mesh clicked. False when the click found
	/// nothing to measure from.
	/// </summary>
	public bool Click(Camera3D camera, AlienScene scene, Vector2 screenPosition, bool selectedOrigin, bool vertexSnap)
	{
		if (!_active || !TryGetSceneRoot(scene, out Node3D root))
			return false;

		if (!TryResolvePoint(camera, scene, root, screenPosition, selectedOrigin, vertexSnap, out MeasurePoint point))
			return false;

		if (_pointCount != 1 || _root != root || _contentGeneration != scene.ContentGeneration)
		{
			_first = point;
			_second = default;
			_pointCount = 1;
			_root = root;
			_contentGeneration = scene.ContentGeneration;
			_hoverValid = false;
			return true;
		}

		_second = point;
		_pointCount = 2;
		_hoverValid = false;

		Vector3 first = _first.Local;
		Vector3 second = _second.Local;
		Measure(first, second, out float distance, out float vertical, out float horizontal);
		ViewerLog.Print("[Viewer] Measured " + FormatMetres(distance) + " (vertical " + FormatMetres(vertical)
			+ ", horizontal " + FormatMetres(horizontal) + ") from " + FormatPoint(first) + " to " + FormatPoint(second));
		return true;
	}

	/// <summary>
	/// The cursor moved over the view. With only the first point down, the line runs to what a click here would put
	/// down - looked for once, at a later <see cref="Update"/>, however many moves came in between.
	/// </summary>
	public void Hover(Vector2 screenPosition, bool selectedOrigin, bool vertexSnap)
	{
		if (!_active)
			return;

		//Kept with both points down too: the click that starts the next measurement is where the cursor is
		_hoverScreen = screenPosition;
		_hoverSelectedOrigin = selectedOrigin;
		_hoverVertexSnap = vertexSnap;
		_hoverKnown = true;
		_hoverPending = _pointCount == 1;
	}

	/// <summary>
	/// Once a frame: drop the measurement if the scene it was in has gone, look again for the surface under the cursor
	/// if the cursor or the camera moved, and draw the line where the camera now puts it. Main thread.
	/// </summary>
	public void Update(Camera3D camera, AlienScene scene, CanvasLayer hudLayer)
	{
		if (!_active || _pointCount == 0)
		{
			HideOverlay();
			return;
		}

		if (!TryGetSceneRoot(scene, out Node3D root) || root != _root || scene.ContentGeneration != _contentGeneration)
		{
			Clear();
			return;
		}

		if (camera == null || !GodotObject.IsInstanceValid(camera) || scene.IsPreviewBatchRunning)
		{
			HideOverlay();
			return;
		}

		Transform3D rootTransform = root.GlobalTransform;
		Vector3 firstLocal = FollowAnchor(ref _first, rootTransform);
		Vector3 secondLocal;
		bool preview = _pointCount == 1;
		if (!preview)
		{
			secondLocal = FollowAnchor(ref _second, rootTransform);
		}
		else
		{
			//The camera flying moves what is under a cursor that has not moved
			if (_hoverKnown && !camera.GlobalTransform.IsEqualApprox(_hoverCameraTransform))
				_hoverPending = true;
			ulong now = Time.GetTicksMsec();
			if (_hoverPending && now - _lastHoverPickMs >= HoverPickIntervalMs)
			{
				_hoverPending = false;
				_lastHoverPickMs = now;
				_hoverCameraTransform = camera.GlobalTransform;
				_hoverValid = TryResolvePoint(camera, scene, root, _hoverScreen, _hoverSelectedOrigin, _hoverVertexSnap, out _hover);
			}

			if (!_hoverValid)
			{
				Draw(camera, hudLayer, rootTransform, firstLocal, firstLocal, preview, secondPoint: false);
				return;
			}

			secondLocal = FollowAnchor(ref _hover, rootTransform);
		}

		Draw(camera, hudLayer, rootTransform, firstLocal, secondLocal, preview, secondPoint: true);
	}

	/* What a click (or the cursor) at a point of the screen measures from: the selected entity's origin for Shift (with
	   something selected), else the nearest vertex for vertex snapping, else the surface. In the scene root's space. */
	private static bool TryResolvePoint(Camera3D camera, AlienScene scene, Node3D root, Vector2 screenPosition,
		bool selectedOrigin, bool vertexSnap, out MeasurePoint point)
	{
		point = default;
		if (camera == null || !GodotObject.IsInstanceValid(camera))
			return false;

		if (selectedOrigin && scene.TryGetSelectedEntity(out Node3D selected) && selected.IsInsideTree())
		{
			point.Anchor = selected;
			point.Local = root.GlobalTransform.AffineInverse() * selected.GlobalPosition;
			return true;
		}

		Commands commands = scene.Content?.Level?.Commands;
		Vector3 world;
		if (vertexSnap)
		{
			//An icon has no vertices worth the name: its entity's origin comes back instead
			if (!LevelViewerPick.TryPickNearestVertex(camera, screenPosition, root, commands, null, out world))
				return false;
		}
		else
		{
			LevelViewerPick.PickHit? hit = LevelViewerPick.PickClosest(root, camera, screenPosition, root, commands);
			if (!hit.HasValue)
				return false;

			//An icon is a flat quad facing the camera, so where on it the click landed means nothing: its entity's origin does
			if (hit.Value.HitNode is MeshInstance3D mesh && GodotObject.IsInstanceValid(mesh)
				&& PreviewVisualUtility.IsIconBillboardMaterial(mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)))
				world = mesh.GlobalPosition;
			else
				world = hit.Value.Position;
		}

		point.Local = root.GlobalTransform.AffineInverse() * world;
		return true;
	}

	/* A point taken from an entity's origin goes where the entity goes (a gizmo drag, an edit in OpenCAGE). Once the
	   entity has gone, it stays where it was last seen. */
	private static Vector3 FollowAnchor(ref MeasurePoint point, Transform3D rootTransform)
	{
		if (point.Anchor != null)
		{
			if (GodotObject.IsInstanceValid(point.Anchor) && point.Anchor.IsInsideTree())
				point.Local = rootTransform.AffineInverse() * point.Anchor.GlobalPosition;
			else
				point.Anchor = null;
		}

		return point.Local;
	}

	private static bool TryGetSceneRoot(AlienScene scene, out Node3D root)
	{
		root = null;
		if (scene == null || !GodotObject.IsInstanceValid(scene) || !scene.Content.Loaded)
			return false;

		root = scene.ParentNode;
		return root != null && GodotObject.IsInstanceValid(root) && root.IsInsideTree();
	}

	/// <summary>The distance between two points, and how much of it is up or down and how much along the ground.</summary>
	public static void Measure(Vector3 from, Vector3 to, out float distance, out float vertical, out float horizontal)
	{
		Vector3 delta = to - from;
		distance = delta.Length();
		vertical = Mathf.Abs(delta.Y);
		horizontal = new Vector2(delta.X, delta.Z).Length();
	}

	private static string FormatMetres(float metres)
	{
		return metres.ToString("0.000") + " m";
	}

	//On CATHODE's axes, as an entity's position parameter would show it
	private static string FormatPoint(Vector3 local)
	{
		return "(" + local.X.ToString("0.000") + ", " + local.Y.ToString("0.000") + ", " + (-local.Z).ToString("0.000") + ")";
	}

	// -------------------------------------------------------------------------
	//  Drawing
	// -------------------------------------------------------------------------

	/* The line from the first point to the second, with the right angle it makes with the vertical: down (or up) from
	   the higher point to the lower one's height, then along the ground to it. The label sits beside the middle of the
	   line (by the point when both are in one place), or at the bottom of the view while the line is behind the camera.
	   secondPoint false: only the first point. */
	private void Draw(Camera3D camera, CanvasLayer hudLayer, Transform3D rootTransform, Vector3 firstLocal, Vector3 secondLocal,
		bool preview, bool secondPoint)
	{
		if (!EnsureOverlay(hudLayer))
			return;

		_overlay.Visible = true;
		Vector3 first = rootTransform * firstLocal;
		Vector3 second = rootTransform * secondLocal;
		float alpha = preview ? PreviewAlpha : 1f;

		PlaceDot(_firstDot, camera, first, 1f);
		if (!secondPoint)
		{
			_secondDot.Visible = false;
			_line.Visible = false;
			_verticalLeg.Visible = false;
			_horizontalLeg.Visible = false;
			_labelPanel.Visible = false;
			return;
		}

		PlaceDot(_secondDot, camera, second, alpha);

		bool firstHigher = firstLocal.Y >= secondLocal.Y;
		Vector3 upperLocal = firstHigher ? firstLocal : secondLocal;
		Vector3 lowerLocal = firstHigher ? secondLocal : firstLocal;
		Vector3 cornerLocal = new Vector3(upperLocal.X, lowerLocal.Y, upperLocal.Z);
		Vector3 upper = rootTransform * upperLocal;
		Vector3 lower = rootTransform * lowerLocal;
		Vector3 corner = rootTransform * cornerLocal;

		Measure(firstLocal, secondLocal, out float distance, out float vertical, out float horizontal);
		PlaceLine(_verticalLeg, camera, upper, corner, vertical >= LegMinimumLength, alpha);
		PlaceLine(_horizontalLeg, camera, corner, lower, horizontal >= LegMinimumLength, alpha);
		bool lineOnScreen = PlaceLine(_line, camera, first, second, distance >= LegMinimumLength, alpha);

		_distanceLabel.Text = "Distance " + FormatMetres(distance);
		_verticalLabel.Text = "Vertical " + FormatMetres(vertical);
		_horizontalLabel.Text = "Horizontal " + FormatMetres(horizontal);
		_labelPanel.Visible = true;
		_labelPanel.Modulate = new Color(1f, 1f, 1f, preview ? 0.85f : 1f);
		_labelPanel.ResetSize();

		Vector2 viewportSize = camera.GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
		Vector2 size = _labelPanel.GetCombinedMinimumSize();
		Vector2 position;
		if (lineOnScreen)
		{
			Vector2[] points = _line.Points;
			position = LabelBesideLine(points[0], points[1], size);
		}
		else if (_firstDot.Visible && distance < LegMinimumLength)
		{
			//Both points in one place, so no line: up and to the right of the point
			Vector2 point = _firstDot.Position + new Vector2(PointSize, PointSize) * 0.5f;
			position = point + new Vector2(LabelGap, -LabelGap - size.Y);
		}
		else
		{
			position = new Vector2((viewportSize.X - size.X) * 0.5f, viewportSize.Y - size.Y - 64f);
		}

		if (viewportSize.X > 0f && viewportSize.Y > 0f)
		{
			position.X = Mathf.Clamp(position.X, 8f, Mathf.Max(8f, viewportSize.X - size.X - 8f));
			position.Y = Mathf.Clamp(position.Y, 8f, Mathf.Max(8f, viewportSize.Y - size.Y - 8f));
		}
		_labelPanel.Position = position;
	}

	/* The label's top left beside a line on screen: off its middle on the side its upward normal points to, and grown
	   away from the line from there, so it covers neither the line nor either end of it, whichever way the line runs. */
	private static Vector2 LabelBesideLine(Vector2 from, Vector2 to, Vector2 size)
	{
		Vector2 direction = to - from;
		Vector2 normal = direction.LengthSquared() > 0f ? new Vector2(-direction.Y, direction.X).Normalized() : Vector2.Up;
		if (normal.Y > 0f || (normal.Y == 0f && normal.X < 0f))
			normal = -normal;

		Vector2 anchor = (from + to) * 0.5f + normal * LabelGap;
		return new Vector2(normal.X < 0f ? anchor.X - size.X : anchor.X, anchor.Y - size.Y);
	}

	private void HideOverlay()
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay) && _overlay.Visible)
			_overlay.Visible = false;
	}

	/* A segment of the level on screen, cut where it passes behind the camera: a point behind the camera has no place on
	   screen. False (and hidden) when none of it is in front, or it is not to be drawn at all. */
	private static bool PlaceLine(Line2D line, Camera3D camera, Vector3 from, Vector3 to, bool draw, float alpha)
	{
		if (!draw || !TryProjectSegment(camera, from, to, out Vector2 screenFrom, out Vector2 screenTo))
		{
			line.Visible = false;
			return false;
		}

		Vector2[] points = line.Points;
		if (points.Length != 2 || points[0] != screenFrom || points[1] != screenTo)
			line.Points = new[] { screenFrom, screenTo };
		line.Modulate = new Color(1f, 1f, 1f, alpha);
		line.Visible = true;
		return true;
	}

	private static void PlaceDot(Panel dot, Camera3D camera, Vector3 world, float alpha)
	{
		if (camera.IsPositionBehind(world))
		{
			dot.Visible = false;
			return;
		}

		dot.Position = camera.UnprojectPosition(world) - new Vector2(PointSize, PointSize) * 0.5f;
		dot.Modulate = new Color(1f, 1f, 1f, alpha);
		dot.Visible = true;
	}

	private static bool TryProjectSegment(Camera3D camera, Vector3 from, Vector3 to, out Vector2 screenFrom, out Vector2 screenTo)
	{
		screenFrom = screenTo = Vector2.Zero;
		Transform3D cameraTransform = camera.GetCameraTransform();
		Transform3D view = cameraTransform.AffineInverse();
		Vector3 eyeFrom = view * from;
		Vector3 eyeTo = view * to;

		//Camera space looks down -Z: what is nearer than the near plane is behind the camera, as far as the screen goes
		float near = camera.Near + 0.001f;
		float depthFrom = -eyeFrom.Z;
		float depthTo = -eyeTo.Z;
		if (depthFrom < near && depthTo < near)
			return false;
		if (depthFrom < near)
			eyeFrom = eyeFrom.Lerp(eyeTo, (near - depthFrom) / (depthTo - depthFrom));
		else if (depthTo < near)
			eyeTo = eyeTo.Lerp(eyeFrom, (near - depthTo) / (depthFrom - depthTo));

		screenFrom = camera.UnprojectPosition(cameraTransform * eyeFrom);
		screenTo = camera.UnprojectPosition(cameraTransform * eyeTo);
		return true;
	}

	/* Built the first time there is something to draw, on the camera's HUD layer, styled like the camera's own HUD
	   panels. False while there is no layer yet (the camera makes it a frame after it is ready). */
	private bool EnsureOverlay(CanvasLayer hudLayer)
	{
		if (_overlay != null && GodotObject.IsInstanceValid(_overlay))
			return true;

		_overlay = null;
		if (hudLayer == null || !GodotObject.IsInstanceValid(hudLayer))
			return false;

		_overlay = new Control
		{
			Name = "Measure",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		hudLayer.AddChild(_overlay);

		_horizontalLeg = CreateLine(HorizontalColour, 1.5f);
		_verticalLeg = CreateLine(VerticalColour, 1.5f);
		_line = CreateLine(LineColour, 2.5f);
		_firstDot = CreateDot();
		_secondDot = CreateDot();

		_distanceLabel = CreateLabel(LineColour, 15);
		_verticalLabel = CreateLabel(VerticalColour, 13);
		_horizontalLabel = CreateLabel(HorizontalColour, 13);
		VBoxContainer rows = new VBoxContainer
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		rows.AddThemeConstantOverride(SeparationName, 0);
		rows.AddChild(_distanceLabel);
		rows.AddChild(_verticalLabel);
		rows.AddChild(_horizontalLabel);

		StyleBoxFlat style = new StyleBoxFlat
		{
			BgColor = new Color(0.07f, 0.08f, 0.1f, 0.85f),
			CornerRadiusTopLeft = 4,
			CornerRadiusTopRight = 4,
			CornerRadiusBottomLeft = 4,
			CornerRadiusBottomRight = 4,
			ContentMarginLeft = 10,
			ContentMarginRight = 10,
			ContentMarginTop = 6,
			ContentMarginBottom = 6,
		};
		_labelPanel = new PanelContainer
		{
			Name = "MeasureLabel",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_labelPanel.AddThemeStyleboxOverride(PanelStyleName, style);
		_labelPanel.AddChild(rows);
		_overlay.AddChild(_labelPanel);
		return true;
	}

	private Line2D CreateLine(Color colour, float width)
	{
		Line2D line = new Line2D
		{
			Width = width,
			DefaultColor = colour,
			Antialiased = true,
			Visible = false,
		};
		_overlay.AddChild(line);
		return line;
	}

	private Panel CreateDot()
	{
		StyleBoxFlat style = new StyleBoxFlat
		{
			BgColor = PointColour,
			BorderColor = PointEdgeColour,
			BorderWidthLeft = 2,
			BorderWidthTop = 2,
			BorderWidthRight = 2,
			BorderWidthBottom = 2,
			CornerRadiusTopLeft = (int)(PointSize * 0.5f),
			CornerRadiusTopRight = (int)(PointSize * 0.5f),
			CornerRadiusBottomLeft = (int)(PointSize * 0.5f),
			CornerRadiusBottomRight = (int)(PointSize * 0.5f),
		};
		Panel dot = new Panel
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Size = new Vector2(PointSize, PointSize),
			Visible = false,
		};
		dot.AddThemeStyleboxOverride(PanelStyleName, style);
		_overlay.AddChild(dot);
		return dot;
	}

	private static Label CreateLabel(Color colour, int fontSize)
	{
		Label label = new Label
		{
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.AddThemeColorOverride(FontColourName, colour);
		label.AddThemeFontSizeOverride(FontSizeName, fontSize);
		return label;
	}
}
