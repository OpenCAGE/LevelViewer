using Godot;
using System;
using System.Runtime.InteropServices;

/// <summary>
/// Win32 focus/capture helpers for the Godot viewer embedded inside OpenCAGE.
/// </summary>
public static class LevelViewerEmbeddedFocus
{
	public static bool IsEmbedded =>
		OS.GetEnvironment("OPENCAGE_EMBEDDED") == "1" || HasEmbeddedCommandLineArg();

	private static bool HasEmbeddedCommandLineArg()
	{
		foreach (string arg in OS.GetCmdlineArgs())
		{
			if (arg == "--opencage-embedded")
				return true;
		}

		return false;
	}

	public static void ConfigureEmbeddedStartup()
	{
		if (!IsEmbedded)
			return;

		ProjectSettings.SetSetting("application/run/low_processor_mode", false);
		LevelViewerRenderIdleThrottle.ConfigureEmbeddedMode(true);
	}

	/// <summary>
	/// Return keyboard focus to the WinForms host so OpenCAGE stays responsive while the viewer idles.
	/// </summary>
	public static void ReleaseFocusAndCaptureToHost()
	{
		if (!IsEmbedded)
			return;

		IntPtr hwnd = GetMainWindowHandle();
		if (hwnd == IntPtr.Zero)
			return;

		if (GetCapture() == hwnd)
			ReleaseCapture();

		IntPtr host = GetParent(hwnd);
		if (host == IntPtr.Zero || GetFocus() != hwnd)
			return;

		SetFocus(host);
	}

	public static bool IsMouseOverMainWindow()
	{
		IntPtr hwnd = GetMainWindowHandle();
		if (hwnd == IntPtr.Zero)
			return false;

		if (!GetCursorPos(out POINT screenPoint))
			return false;

		if (!GetWindowRect(hwnd, out RECT windowRect))
			return false;

		return screenPoint.X >= windowRect.Left
			&& screenPoint.X < windowRect.Right
			&& screenPoint.Y >= windowRect.Top
			&& screenPoint.Y < windowRect.Bottom;
	}

	private static IntPtr GetMainWindowHandle()
	{
		SceneTree tree = Engine.GetMainLoop() as SceneTree;
		Window window = tree?.Root?.GetWindow();
		if (window == null)
			return IntPtr.Zero;

		return (IntPtr)DisplayServer.WindowGetNativeHandle(
			DisplayServer.HandleType.WindowHandle,
			window.GetWindowId());
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll")]
	private static extern IntPtr GetFocus();

	[DllImport("user32.dll")]
	private static extern IntPtr SetFocus(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetParent(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern IntPtr GetCapture();

	[DllImport("user32.dll")]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll")]
	private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
