/// <summary>
/// Notifies OpenCAGE when the level viewer begins or finishes scene population.
/// </summary>
public static class ViewerPopulateBridge
{
	private static CommandsEditorConnection _connection;
	private static uint _populateTokenCounter;
	private static uint _activePopulateToken;

	public static void RegisterConnection(CommandsEditorConnection connection)
	{
		_connection = connection;
	}

	public static void ClearConnection()
	{
		_connection = null;
	}

	public static void NotifyStarted(string levelName)
	{
		if (_connection == null)
			return;

		_activePopulateToken = ++_populateTokenCounter;
		_connection.NotifyViewerPopulateStarted(levelName, _activePopulateToken);
	}

	public static void NotifyFinished()
	{
		if (_connection == null || _activePopulateToken == 0)
			return;

		_connection.NotifyViewerPopulateFinished(_activePopulateToken);
		_activePopulateToken = 0;
	}

	/// <summary>Population was skipped (already loaded, same composite, etc.).</summary>
	public static void NotifySkipped()
	{
		if (_connection == null || _activePopulateToken != 0)
			return;

		_connection.NotifyViewerPopulateFinished(0);
	}
}
