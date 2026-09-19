using Godot;

/// <summary>
/// Keeps WorldEnvironment settings appropriate for a lightweight level viewer (no SDFGI).
/// </summary>
public static class LevelViewerEnvironment
{
	//A NodePath literal is a temporary whose native handle the binding gives the engine with nothing
	//keeping it alive (see LevelViewerPick); made once.
	private static readonly NodePath WorldEnvironmentPath = new NodePath("WorldEnvironment");

	public static void EnsureViewerEnvironment(Node fromNode)
	{
		if (fromNode == null)
			return;

		WorldEnvironment worldEnvironment = fromNode.GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath)
			?? fromNode.GetParent()?.GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);

		if (worldEnvironment?.Environment == null)
			return;

		Godot.Environment environment = worldEnvironment.Environment;
		environment.SdfgiEnabled = false;
	}
}
