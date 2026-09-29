using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Answers other threads' SendMessage calls part way through long work on the main thread.
/// </summary>
/// <remarks>
/// Hosted, this window is a child of OpenCAGE's, so the two UI threads share input state, and the system sends this
/// thread messages synchronously when OpenCAGE's window changes state - minimising or restoring it, or switching away,
/// sends activation messages to this thread's windows. OpenCAGE then waits until this thread next looks at its queue,
/// which a populate or a big entity batch does not do for seconds at a time: minimising OpenCAGE while the viewport
/// loaded HAB_ShoppingCentre froze it for up to 49 s in the stress run, and restoring it for 16 s. Peeking for sent
/// messages only delivers those calls - input and posted messages stay queued for the frame loop - so the window
/// procedures learn of an activation or focus change a little early, and nothing else runs.
/// </remarks>
public static class LevelViewerSentMessages
{
	private const uint PM_NOREMOVE = 0x0000;
	private const uint PM_QS_SENDMESSAGE = 0x0040 << 16;
	private const long IntervalMs = 50;

	private static readonly Stopwatch _sinceLast = Stopwatch.StartNew();
	private static readonly bool _enabled = OperatingSystem.IsWindows() && LevelViewerEmbeddedFocus.IsEmbedded;
	//Set by Initialise, on the main thread; 0 until then, and nothing is pumped
	private static int _mainThreadId;

	[StructLayout(LayoutKind.Sequential)]
	private struct MSG
	{
		public IntPtr hwnd;
		public uint message;
		public IntPtr wParam;
		public IntPtr lParam;
		public uint time;
		public int ptX;
		public int ptY;
		public uint lPrivate;
	}

	[DllImport("user32.dll")]
	private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

	/// <summary>On the main thread, at startup: the thread whose windows are pumped.</summary>
	public static void Initialise() => _mainThreadId = Environment.CurrentManagedThreadId;

	/// <summary>Call as often as convenient from a long loop on the main thread: it does anything at most every 50 ms.</summary>
	public static void PumpIfDue()
	{
		if (!_enabled || _mainThreadId == 0 || _sinceLast.ElapsedMilliseconds < IntervalMs || Environment.CurrentManagedThreadId != _mainThreadId)
			return;
		_sinceLast.Restart();
		PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE | PM_QS_SENDMESSAGE);
	}
}
