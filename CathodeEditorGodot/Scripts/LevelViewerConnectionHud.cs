using Godot;

/// <summary>
/// Short-lived toast for OpenCAGE websocket connection state (matches camera HUD styling).
/// </summary>
public partial class LevelViewerConnectionHud : CanvasLayer
{
	private const float DefaultFadeSeconds = 2.5f;

	private Control _root;
	private PanelContainer _panel;
	private Label _label;
	private float _fadeTimer;
	private float _fadeDuration = DefaultFadeSeconds;
	private bool _attached;

	public void AttachTo(Node host)
	{
		if (_attached || host == null || !GodotObject.IsInstanceValid(host))
			return;

		_attached = true;
		Layer = 100;
		Name = "ConnectionHud";
		host.AddChild(this);

		_root = new Control
		{
			Name = "ConnectionHudRoot",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		_label = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
		};
		_label.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 0.98f));
		_label.AddThemeFontSizeOverride("font_size", 14);

		_panel = CreatePanel(_label);
		_panel.MouseFilter = Control.MouseFilterEnum.Ignore;
		_panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
		_panel.OffsetTop = 58f;
		_panel.OffsetBottom = 100f;
		_panel.OffsetLeft = -190f;
		_panel.OffsetRight = 190f;
		_panel.Visible = false;
		_root.AddChild(_panel);
	}

	public void ShowWaiting()
	{
		ShowStatus("Waiting to connect to OpenCAGE...", new Color(1f, 0.82f, 0.42f));
	}

	public void ShowConnected()
	{
		ShowStatus("Connected to OpenCAGE", new Color(0.58f, 0.96f, 0.66f));
	}

	public void ShowDisconnected()
	{
		ShowStatus("Disconnected from OpenCAGE — waiting to reconnect", new Color(1f, 0.58f, 0.42f));
	}

	public void UpdateFade(float deltaSeconds)
	{
		if (_panel == null || _fadeTimer <= 0f)
			return;

		_fadeTimer -= deltaSeconds;
		_panel.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(_fadeTimer / _fadeDuration, 0f, 1f));
		if (_fadeTimer <= 0f)
			_panel.Visible = false;
	}

	private void ShowStatus(string message, Color textColor)
	{
		if (_label == null || _panel == null)
			return;

		_label.Text = message;
		_label.AddThemeColorOverride("font_color", textColor);
		_fadeDuration = DefaultFadeSeconds;
		_fadeTimer = _fadeDuration;
		_panel.Modulate = Colors.White;
		_panel.Visible = true;
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
