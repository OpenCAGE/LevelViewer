using Godot;

/// <summary>
/// Keeps WorldEnvironment settings appropriate for a lightweight level viewer (no SDFGI).
/// </summary>
public static class LevelViewerEnvironment
{
	public static void EnsureViewerEnvironment(Node fromNode)
	{
		if (fromNode == null)
			return;

		WorldEnvironment worldEnvironment = fromNode.GetNodeOrNull<WorldEnvironment>("WorldEnvironment")
			?? fromNode.GetParent()?.GetNodeOrNull<WorldEnvironment>("WorldEnvironment");

		if (worldEnvironment?.Environment == null)
			return;

		Godot.Environment environment = worldEnvironment.Environment;
		environment.SdfgiEnabled = false;
	}
}
