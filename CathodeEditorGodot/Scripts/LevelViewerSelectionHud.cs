using Godot;

/// <summary>
/// Short-lived toast for the selected entity name (matches camera HUD styling).
/// </summary>
public partial class LevelViewerSelectionHud : CanvasLayer
{
	private const float DefaultFadeSeconds = 2.5f;

	private Control _root;
	private PanelContainer _panel;
	private Label _label;
	private float _fadeTimer;
	private float _fadeDuration = DefaultFadeSeconds;
	private bool _attached;
	private bool _persistent;

	public void AttachTo(Node host)
	{
		if (_attached || host == null || !GodotObject.IsInstanceValid(host))
			return;

		_attached = true;
		Layer = 100;
		if (Name == null || Name.IsEmpty || Name == "LevelViewerSelectionHud")
			Name = "SelectionHud";
		host.AddChild(this);

		_root = new Control
		{
			Name = "SelectionHudRoot",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		_label = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_label.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
		_label.AddThemeFontSizeOverride("font_size", 14);

		_panel = CreatePanel(_label);
		_panel.MouseFilter = Control.MouseFilterEnum.Ignore;
		_panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_panel.OffsetTop = 12f;
		_panel.OffsetBottom = 80f;
		_panel.OffsetLeft = -320f;
		_panel.OffsetRight = 320f;
		_panel.CustomMinimumSize = new Vector2(200f, 0f);
		_panel.Visible = false;
		_root.AddChild(_panel);
	}

	public void SetPanelTopOffset(float topOffset, float bottomOffset)
	{
		if (_panel == null)
			return;
		_panel.OffsetTop = topOffset;
		_panel.OffsetBottom = bottomOffset;
	}

	public void SetPanelTopRight()
	{
		if (_panel == null || _label == null)
			return;

		_label.HorizontalAlignment = HorizontalAlignment.Center;
		_label.AutowrapMode = TextServer.AutowrapMode.Off;
		_label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;

		_panel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
		_panel.CustomMinimumSize = Vector2.Zero;
		FitPanelTopRight();
	}

	public void ShowPersistent(string text, Color fontColor)
	{
		if (_label == null || _panel == null)
			return;

		_persistent = true;
		_fadeTimer = 0f;
		_label.Text = text;
		_label.AddThemeColorOverride("font_color", fontColor);
		_panel.Modulate = Colors.White;
		_panel.ResetSize();
		FitPanelTopRight();
		_panel.Visible = true;
	}

	private void FitPanelTopRight()
	{
		if (_panel == null)
			return;

		const float margin = 12f;
		Vector2 size = _panel.GetCombinedMinimumSize();
		if (size.X <= 0f || size.Y <= 0f)
			size = _panel.Size;

		_panel.OffsetTop = margin;
		_panel.OffsetRight = -margin;
		_panel.OffsetLeft = -margin - size.X;
		_panel.OffsetBottom = margin + size.Y;
	}

	public void ShowEntity(string displayName)
	{
		if (_label == null || _panel == null)
			return;

		if (string.IsNullOrWhiteSpace(displayName))
		{
			Hide();
			return;
		}

		_persistent = false;
		_label.Text = displayName;
		_fadeDuration = DefaultFadeSeconds;
		_fadeTimer = _fadeDuration;
		_panel.Modulate = Colors.White;
		_panel.Visible = true;
	}

	public void Hide()
	{
		_persistent = false;
		_fadeTimer = 0f;
		if (_panel != null)
			_panel.Visible = false;
	}

	public void UpdateFade(float deltaSeconds)
	{
		if (_persistent || _panel == null || _fadeTimer <= 0f)
			return;

		_fadeTimer -= deltaSeconds;
		_panel.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_fadeTimer / _fadeDuration, 0f, 1f));
		if (_fadeTimer <= 0f)
			_panel.Visible = false;
	}

	private static PanelContainer CreatePanel(Label label)
	{
		var style = new StyleBoxFlat
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

		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", style);
		panel.AddChild(label);
		return panel;
	}
}
