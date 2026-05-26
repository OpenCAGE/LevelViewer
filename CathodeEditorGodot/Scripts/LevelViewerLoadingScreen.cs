using Godot;

/// <summary>
/// Full-screen overlay shown while Cathode level/composite data is loading.
/// </summary>
public partial class LevelViewerLoadingScreen : CanvasLayer
{
	private Label _messageLabel;
	private PanelContainer _panel;
	private Control _root;

	public bool IsUiReady => IsInsideTree() && _messageLabel != null;

	public void AttachTo(Node host)
	{
		if (host == null || !GodotObject.IsInstanceValid(host))
			return;

		if (!IsInsideTree())
		{
			Layer = 200;
			Name = "LevelViewerLoadingScreen";
			host.AddChild(this);
		}

		if (_root != null)
			return;

		_root = new Control
		{
			Name = "LoadingRoot",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		AddChild(_root);

		var dimmer = new ColorRect
		{
			Name = "Dimmer",
			Color = new Color(0.04f, 0.05f, 0.07f, 0.72f),
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		dimmer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		_root.AddChild(dimmer);

		_messageLabel = new Label
		{
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
		};
		_messageLabel.AddThemeColorOverride("font_color", new Color(0.93f, 0.95f, 0.98f));
		_messageLabel.AddThemeFontSizeOverride("font_size", 18);

		_panel = CreatePanel(_messageLabel);
		_panel.MouseFilter = Control.MouseFilterEnum.Ignore;
		_panel.SetAnchorsPreset(Control.LayoutPreset.Center);
		_panel.OffsetLeft = -220f;
		_panel.OffsetRight = 220f;
		_panel.OffsetTop = -36f;
		_panel.OffsetBottom = 36f;
		_root.AddChild(_panel);

		Visible = false;
	}

	public void ShowMessage(string message)
	{
		if (_messageLabel != null)
			_messageLabel.Text = message;
		Visible = true;
		_root?.QueueRedraw();
	}

	public void HideScreen()
	{
		Visible = false;
	}

	private static PanelContainer CreatePanel(Label label)
	{
		var style = new StyleBoxFlat
		{
			BgColor = new Color(0.08f, 0.09f, 0.12f, 0.94f),
			CornerRadiusTopLeft = 6,
			CornerRadiusTopRight = 6,
			CornerRadiusBottomLeft = 6,
			CornerRadiusBottomRight = 6,
			ContentMarginLeft = 18,
			ContentMarginRight = 18,
			ContentMarginTop = 14,
			ContentMarginBottom = 14,
		};

		var panel = new PanelContainer();
		panel.AddThemeStyleboxOverride("panel", style);
		panel.AddChild(label);
		return panel;
	}
}
