#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Copies lookup tables into exported builds (beside the .exe and under data_* .NET folders).
/// </summary>
[Tool]
public partial class StreamingAssetsExportPlugin : EditorExportPlugin
{
    private static readonly string[] RequiredFiles = { "info.dat", "sound_names.bin" };

    private string _exportExecutablePath;

    public override string _GetName() => "zz_streaming_assets_copy";

    public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
    {
        _ = features;
        _ = isDebug;
        _ = flags;
        _exportExecutablePath = path;
    }

    public override void _ExportEnd()
    {
        if (string.IsNullOrWhiteSpace(_exportExecutablePath))
            return;

        string exportDirectory = Path.GetDirectoryName(_exportExecutablePath);
        if (string.IsNullOrWhiteSpace(exportDirectory))
            return;

        // Run after the current export pass so Godot's .NET publish step has finished.
        Callable.From(() => CopyStreamingAssetsToExport(exportDirectory)).CallDeferred();
    }

    private static void CopyStreamingAssetsToExport(string exportDirectory)
    {
        string sourceDirectory = ResolveStreamingAssetsSourceDirectory();
        bool hasCompleteSource = !string.IsNullOrWhiteSpace(sourceDirectory)
            && GetMissingRequiredFiles(sourceDirectory).Count == 0;
        bool hasEmbedded = HasEmbeddedStreamingAssets();

        if (!hasCompleteSource && !hasEmbedded)
        {
            GD.PushError(
                "Export: could not find info.dat / sound_names.bin on disk or as embedded CathodeLib resources. " +
                "Place them in CathodeEditorGodot/streaming_assets or Source/CathodeLib/CathodeLib/Resources, then rebuild the editor project before exporting.");
            return;
        }

        string exportStreamingAssets = Path.Combine(exportDirectory, "streaming_assets");
        if (hasCompleteSource)
            CopyStreamingAssets(sourceDirectory, exportStreamingAssets);
        WriteEmbeddedStreamingAssetsIfMissing(exportStreamingAssets);

        foreach (string dataDirectory in Directory.GetDirectories(exportDirectory, "data_*"))
        {
            string dataStreamingAssets = Path.Combine(dataDirectory, "streaming_assets");
            if (hasCompleteSource)
                CopyStreamingAssets(sourceDirectory, dataStreamingAssets);
            WriteEmbeddedStreamingAssetsIfMissing(dataStreamingAssets);
        }

        string sourceLabel = hasCompleteSource ? sourceDirectory : "embedded CathodeLib resources";
        GD.Print($"Export: streaming_assets written under {exportDirectory} (from {sourceLabel})");
    }

    private static bool HasEmbeddedStreamingAssets()
    {
        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            if (!CathodeLib.Utilities.TryReadEmbeddedStreamingAsset(RequiredFiles[i], out _))
                return false;
        }

        return true;
    }

    private static string ResolveStreamingAssetsSourceDirectory()
    {
        List<string> candidates = new List<string>();
        string projectDirectory = ProjectSettings.GlobalizePath("res://");
        if (!string.IsNullOrWhiteSpace(projectDirectory))
        {
            candidates.Add(Path.Combine(projectDirectory, "streaming_assets"));
            candidates.Add(Path.GetFullPath(Path.Combine(projectDirectory, "..", "..", "..", "CathodeLib", "CathodeLib", "Resources")));
            candidates.Add(Path.GetFullPath(Path.Combine(projectDirectory, "..", "CathodeEditorUnity", "Assets", "StreamingAssets")));
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            string candidate = candidates[i];
            if (Directory.Exists(candidate) && GetMissingRequiredFiles(candidate).Count == 0)
                return candidate;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            if (Directory.Exists(candidates[i]))
                return candidates[i];
        }

        return null;
    }

    private static List<string> GetMissingRequiredFiles(string sourceDirectory)
    {
        List<string> missing = new List<string>();
        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            if (!File.Exists(Path.Combine(sourceDirectory, RequiredFiles[i])))
                missing.Add(RequiredFiles[i]);
        }

        return missing;
    }

    private static void CopyStreamingAssets(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        Directory.CreateDirectory(destinationDirectory);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(sourceFile);
            if (string.Equals(fileName, "README.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            string relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            string destinationFile = Path.Combine(destinationDirectory, relativePath);
            string destinationFolder = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            File.Copy(sourceFile, destinationFile, overwrite: true);
        }
    }

    private static void WriteEmbeddedStreamingAssetsIfMissing(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        for (int i = 0; i < RequiredFiles.Length; i++)
        {
            string fileName = RequiredFiles[i];
            string destinationFile = Path.Combine(destinationDirectory, fileName);
            if (File.Exists(destinationFile))
                continue;

            if (!CathodeLib.Utilities.TryReadEmbeddedStreamingAsset(fileName, out byte[] content))
                continue;

            File.WriteAllBytes(destinationFile, content);
        }
    }
}

[Tool]
public partial class StreamingAssetsEditorPlugin : EditorPlugin
{
    private StreamingAssetsExportPlugin _exportPlugin;

    public override void _EnterTree()
    {
        _exportPlugin = new StreamingAssetsExportPlugin();
        AddExportPlugin(_exportPlugin);
    }

    public override void _ExitTree()
    {
        if (_exportPlugin != null)
            RemoveExportPlugin(_exportPlugin);
    }
}
#endif
