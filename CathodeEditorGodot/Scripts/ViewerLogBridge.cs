using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

/// <summary>
/// Forwards Level Viewer log lines to OpenCAGE over the websocket.
/// </summary>
public static class ViewerLogBridge
{
	private struct PendingLog
	{
		public string Message;
		public bool IsError;
	}

	private static CommandsEditorConnection _connection;
	private static readonly ConcurrentQueue<PendingLog> _pending = new ConcurrentQueue<PendingLog>();
	private static bool _hooksInstalled;

	public static void RegisterConnection(CommandsEditorConnection connection)
	{
		_connection = connection;
		InstallHooksOnce();
	}

	public static void ClearConnection()
	{
		_connection = null;
	}

	public static void NotifyConnected()
	{
		FlushPending();
	}

	public static void TryForward(string message, bool isError)
	{
		if (string.IsNullOrWhiteSpace(message))
			return;

		if (_connection == null || !_connection.IsWebSocketConnected)
		{
			_pending.Enqueue(new PendingLog { Message = message, IsError = isError });
			return;
		}

		_connection.SendViewerLog(message, isError);
	}

	private static void FlushPending()
	{
		if (_connection == null || !_connection.IsWebSocketConnected)
			return;

		while (_pending.TryDequeue(out PendingLog pending))
			_connection.SendViewerLog(pending.Message, pending.IsError);
	}

	private static void InstallHooksOnce()
	{
		if (_hooksInstalled)
			return;

		_hooksInstalled = true;
		AppDomain.CurrentDomain.UnhandledException += (_, e) =>
		{
			TryForward("Unhandled exception: " + e.ExceptionObject, true);
		};
		TaskScheduler.UnobservedTaskException += (_, e) =>
		{
			TryForward("Unobserved task exception: " + e.Exception, true);
			e.SetObserved();
		};
	}
}
