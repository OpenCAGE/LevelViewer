using Godot;
using System;
using System.IO;
using System.Text;

/// <summary>
/// File-backed log for material mapping resolution and remaps.
/// </summary>
public static class MaterialMappingLog
{
	private static readonly object WriteLock = new object();
	private static StreamWriter _writer;
	private static string _logFilePath;

	/// <summary>When false, per-material REMAP lines are skipped (PARAM lines still log).</summary>
	public static bool LogRemaps { get; set; }

	public static string LogFilePath => _logFilePath;

	public static void BeginSession(string levelName)
	{
		lock (WriteLock)
		{
			EndSessionInternal();

			string safeLevelName = SanitizeFileName(levelName);
			string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
			string fileName = "material_mapping_" + safeLevelName + "_" + timestamp + ".log";
			string directory = Path.Combine(OS.GetUserDataDir(), "logs");
			Directory.CreateDirectory(directory);
			_logFilePath = Path.Combine(directory, fileName);

			_writer = new StreamWriter(_logFilePath, append: false, Encoding.UTF8);

			_writer.WriteLine("=== Material mapping log ===");
			_writer.WriteLine("Started: " + DateTime.Now.ToString("O"));
			_writer.WriteLine("Level: " + levelName);
			_writer.WriteLine("Path: " + _logFilePath);
			_writer.WriteLine("LogRemaps: " + LogRemaps);
			_writer.WriteLine();
		}

		ViewerLog.Print("[MaterialMapping] Writing log to: " + _logFilePath);
	}

	public static void EndSession()
	{
		lock (WriteLock)
		{
			EndSessionInternal();
		}
	}

	public static void Write(string message)
	{
		WriteLine(message, isError: false);
	}

	public static void WriteErr(string message)
	{
		WriteLine(message, isError: true);
	}

	private static void WriteLine(string message, bool isError)
	{
		if (string.IsNullOrEmpty(message))
			return;

		lock (WriteLock)
		{
			if (_writer == null)
				return;

			string prefix = DateTime.Now.ToString("HH:mm:ss.fff");
			_writer.WriteLine(prefix + (isError ? " [ERR] " : " ") + message);
		}
	}

	private static void EndSessionInternal()
	{
		if (_writer == null)
			return;

		_writer.WriteLine();
		_writer.WriteLine("=== Material mapping log ended: " + DateTime.Now.ToString("O") + " ===");
		_writer.Flush();
		_writer.Dispose();
		_writer = null;
	}

	private static string SanitizeFileName(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "level";

		char[] invalid = Path.GetInvalidFileNameChars();
		StringBuilder builder = new StringBuilder(value.Length);
		for (int i = 0; i < value.Length; i++)
		{
			char character = value[i];
			bool isInvalid = false;
			for (int j = 0; j < invalid.Length; j++)
			{
				if (character == invalid[j])
				{
					isInvalid = true;
					break;
				}
			}

			builder.Append(isInvalid ? '_' : character);
		}

		return builder.ToString();
	}
}
