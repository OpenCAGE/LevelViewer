using Godot;
using OpenCAGE.UnityConnection;

/// <summary>
/// How the main view smooths its edges: OpenCAGE's Options > Viewport > Anti-aliasing, set on the window's own viewport (the
/// composite preview capture keeps its own MSAA 2x).
///
/// No temporal option (TAA, FSR2): the viewer runs in low processor mode and stops drawing once nothing moves, so a jittered
/// history would be left part-way to settling, and it smears what moves while the camera does.
/// </summary>
public static class LevelViewerAntialiasing
{
	public static LevelViewerAntialiasMode Mode { get; private set; } = LevelViewerViewportDefinitions.DefaultAntialiasMode;

	/// <summary>Sets the mode on <paramref name="viewport"/> (the main thread's to call).</summary>
	public static void Apply(Viewport viewport, LevelViewerAntialiasMode mode)
	{
		Mode = mode;
		if (viewport == null || !GodotObject.IsInstanceValid(viewport))
			return;

		//OpenGL (a machine without Vulkan or D3D12 falls back to it) has neither post filter: MSAA 2x stands in for them
		if ((mode == LevelViewerAntialiasMode.Fxaa || mode == LevelViewerAntialiasMode.Smaa)
			&& RenderingServer.GetCurrentRenderingMethod() == "gl_compatibility")
			mode = LevelViewerAntialiasMode.Msaa2x;

		viewport.Msaa3D = mode switch
		{
			LevelViewerAntialiasMode.Msaa2x => Viewport.Msaa.Msaa2X,
			LevelViewerAntialiasMode.Msaa4x => Viewport.Msaa.Msaa4X,
			LevelViewerAntialiasMode.Msaa8x => Viewport.Msaa.Msaa8X,
			_ => Viewport.Msaa.Disabled,
		};
		viewport.ScreenSpaceAA = mode switch
		{
			LevelViewerAntialiasMode.Fxaa => Viewport.ScreenSpaceAAEnum.Fxaa,
			LevelViewerAntialiasMode.Smaa => Viewport.ScreenSpaceAAEnum.Smaa,
			_ => Viewport.ScreenSpaceAAEnum.Disabled,
		};
		viewport.UseTaa = false;
		//The frame on screen was drawn the old way, and in low processor mode nothing redraws it until something moves
		LevelViewerRenderIdleThrottle.NotifyUserActivity();
	}
}
