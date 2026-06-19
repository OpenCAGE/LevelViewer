using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>
/// High-resolution load timing written to user://level_viewer_load_profile.log after each populate.
/// </summary>
public static class LevelViewerLoadProfiler
{
	private const string LogFileName = "level_viewer_load_profile.log";

	private static readonly Stopwatch _session = new Stopwatch();
	private static readonly List<Entry> _entries = new List<Entry>();
	private static string _sessionLabel = "";
	private static double _lastMs;
	private static bool _active;

	private readonly struct Entry
	{
		public Entry(string phase, string detail, double totalMs, double deltaMs)
		{
			Phase = phase;
			Detail = detail;
			TotalMs = totalMs;
			DeltaMs = deltaMs;
		}

		public string Phase { get; }
		public string Detail { get; }
		public double TotalMs { get; }
		public double DeltaMs { get; }
	}

	public static bool IsActive => _active;

	public static string LogFilePath =>
		ProjectSettings.GlobalizePath("user://" + LogFileName);

	public static void BeginSession(string label)
	{
		_entries.Clear();
		_sessionLabel = string.IsNullOrWhiteSpace(label) ? "load" : label.Trim();
		_lastMs = 0;
		_active = true;
		_session.Restart();
		Mark("session_start");
	}

	public static void Mark(string phase, string detail = null)
	{
		if (!_active || string.IsNullOrWhiteSpace(phase))
			return;

		double totalMs = _session.Elapsed.TotalMilliseconds;
		_entries.Add(new Entry(phase, detail, totalMs, totalMs - _lastMs));
		_lastMs = totalMs;
	}

	public static void EndSession()
	{
		if (!_active)
			return;

		Mark("session_end");
		_active = false;
		_session.Stop();

		string logText = BuildLogText();
		WriteLogFile(logText);
		ViewerLog.Print("Load profile written to: " + LogFilePath);
	}

	public static void CancelSession()
	{
		_active = false;
		_session.Stop();
		_entries.Clear();
		_sessionLabel = "";
		_lastMs = 0;
	}

	private static string BuildLogText()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine("=== Level Viewer Load Profile ===");
		sb.AppendLine("Session: " + _sessionLabel);
		sb.AppendLine("Logged: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
		sb.AppendLine("Log file: " + LogFilePath);
		sb.AppendLine();
		sb.AppendLine("Phase                          Total(ms)   Delta(ms)  Detail");
		sb.AppendLine(new string('-', 96));

		for (int i = 0; i < _entries.Count; i++)
		{
			Entry entry = _entries[i];
			sb.Append(entry.Phase.PadRight(30));
			sb.Append(entry.TotalMs.ToString("F1", CultureInfo.InvariantCulture).PadLeft(12));
			sb.Append(entry.DeltaMs.ToString("F1", CultureInfo.InvariantCulture).PadLeft(12));
			if (!string.IsNullOrWhiteSpace(entry.Detail))
				sb.Append("  ").Append(entry.Detail);
			sb.AppendLine();
		}

		if (_entries.Count > 0)
		{
			sb.AppendLine(new string('-', 96));
			sb.Append("TOTAL".PadRight(30));
			sb.Append(_entries[^1].TotalMs.ToString("F1", CultureInfo.InvariantCulture).PadLeft(12));
			sb.AppendLine();
		}

		return sb.ToString();
	}

	private static void WriteLogFile(string logText)
	{
		string userPath = "user://" + LogFileName;
		using Godot.FileAccess file = Godot.FileAccess.Open(userPath, Godot.FileAccess.ModeFlags.Write);
		if (file == null)
		{
			ViewerLog.PrintErr("Load profile: failed to open " + userPath + " (" + Godot.FileAccess.GetOpenError() + ")");
			return;
		}

		file.StoreString(logText);
	}
}
