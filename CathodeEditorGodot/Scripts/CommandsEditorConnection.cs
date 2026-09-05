using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using Godot;
using Newtonsoft.Json;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public partial class CommandsEditorConnection : Node3D
{
    private const int DefaultWebSocketPort = 1702;

    private ClientWebSocket _client;
    private AlienScene _scene;
    private CancellationTokenSource _connectionCts;
    private readonly int _webSocketPort = ResolveWebSocketPort();

    private readonly object _lock = new object();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

    private string _levelName = "";
    private string _pathToAI = "";

    private List<uint> _pathComposites;
    private List<uint> _pathEntities;
    private bool _compositeLoaded;
    private bool _entitySelected;
    private uint _currentComposite;
    private uint _currentEntity;

    private uint _currentEntityGOID = 0;

    private bool _didLoadLevel = true;

    private struct ParameterSyncKey : IEquatable<ParameterSyncKey>
    {
        public uint CompositeId;
        public uint EntityId;
        public uint ParameterName;

        public bool Equals(ParameterSyncKey other)
        {
            return CompositeId == other.CompositeId
                && EntityId == other.EntityId
                && ParameterName == other.ParameterName;
        }

        public override bool Equals(object obj)
        {
            return obj is ParameterSyncKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)CompositeId;
                hash = (hash * 397) ^ (int)EntityId;
                hash = (hash * 397) ^ (int)ParameterName;
                return hash;
            }
        }
    }

    private class PendingParameterSync
    {
        public ShortGuid DataCompositeID;
        public ShortGuid DataEntityID;
        public ShortGuid VisualCompositeID;
        public ShortGuid VisualEntityID;
        public SyncedParameter Sync;
        public bool FromPointer;
        public bool PointedOverride;
        public Node3D VisualLimitNode;
    }

    private readonly Dictionary<ParameterSyncKey, PendingParameterSync> _pendingParameterSyncs = new Dictionary<ParameterSyncKey, PendingParameterSync>();
    private readonly ConcurrentQueue<string> _incomingMessages = new ConcurrentQueue<string>();

    private Tuple<ShortGuid, ShortGuid> _addedEntity = null;
    private Tuple<ShortGuid, ShortGuid> _removedEntity = null;
    private ShortGuid _removedComposite = ShortGuid.Invalid;

	public bool ShowCameraPosition => _showCameraPosition;
	public bool FocusOnSelected => _focusOnSelected;
	public bool FixCameraToSelected => _fixCameraToSelected;
	private bool _showCameraPosition = true;
	private bool _focusOnSelected = false;
	private bool _fixCameraToSelected = false;
    private bool _hideNestedScriptEntities = false;
    private bool _renderFiltersDirty = false;
    private bool _nestedVisibilityDirty = false;
    private uint _nestedVisibilityPreviousCompositeId = 0;
    private int _renderFiltersGeneration = 0;
    private HashSet<uint> _renderFiltersChangedFunctionTypes = null;

    /// <summary>Composite path depth last seen from OpenCAGE packets (detect editor navigating back).</summary>
    private int _syncedCompositePathDepth;

    private LevelViewerTransformGizmo _transformGizmo;
    public LevelViewerTransformGizmo TransformGizmo => _transformGizmo;

    /// <summary>Entity creation mode: FunctionType (uint) placed on viewport click, 0 = off.</summary>
    private uint _createFunctionType;
    private static Texture2D _penCursorTexture;
    public bool CreateModeActive => _createFunctionType != 0;

    /// <summary>Block resync echo from our own outbound transform packets.</summary>
    private bool _suppressParameterResync;
    private bool _compositeFocusDirty;
    private bool _compositeFocusRefreshScheduled;
    private bool _forceSelectionApply;
    /// <summary>Where the selection waiting in _pathEntities came from; consumed by ApplySelectionNow.</summary>
    private AlienScene.SelectionOrigin _pendingSelectionOrigin = AlienScene.SelectionOrigin.Remote;
    private readonly HashSet<uint> _viewerOriginatedEntityAdds = new HashSet<uint>();
    private readonly HashSet<uint> _releasedEphemeralAliases = new HashSet<uint>();
    private uint _progressiveDeepSelectLeafId;
    private int _progressiveDeepSelectDepth;
    private uint _progressiveDeepSelectActiveComposite;
    private uint[] _progressiveDeepSelectInstancePath = System.Array.Empty<uint>();
    private List<uint> _progressiveDeepSelectEntityIds;
    private List<uint> _progressiveDeepSelectCompositeIds;
    private uint _ephemeralDeepSelectAliasCompositeId;
    private uint _ephemeralDeepSelectAliasEntityId;
    private uint _pendingEphemeralDeepSelectReleaseCompositeId;
    private uint _pendingEphemeralDeepSelectReleaseEntityId;

    public bool IsWebSocketConnected => _client != null && _client.State == WebSocketState.Open;

    //OpenCAGE launches (and owns) this process, so once it's gone there's nothing for us to do. Without this
    //the reconnect loop would retry forever and leave an orphaned viewer running in the background whenever
    //OpenCAGE is force-closed. Ports are per-instance, so this only ever reacts to our own editor going away.
    private const double EditorLostShutdownSeconds = 30.0;

    /// <summary>How long the polite shutdown gets before the process is taken down by force.</summary>
    private const int HardExitGraceMilliseconds = 5000;

    /// <summary>
    /// When the editor was last known to be there, as UTC ticks. Starts at process start, so a viewer
    /// that never manages to connect at all still times out rather than retrying forever.
    /// </summary>
    private long _lastConnectedTicks;

    private volatile bool _shutdownRequested;
    private Thread _watchdogThread;

    public override void _Ready()
    {
        ViewerLog.InstallGlobalExceptionHandlers();
        LevelViewerEmbeddedFocus.ConfigureEmbeddedStartup();
        ViewerLog.Print("Level Viewer starting. Viewer log: " + (ViewerLog.LogFilePath ?? "<unavailable>"));
        _scene = GetNode<AlienScene>("../AlienScene");
        _connectionCts = new CancellationTokenSource();
        ViewerLogBridge.RegisterConnection(this);
        ViewerPopulateBridge.RegisterConnection(this);
        if (_scene != null)
            _scene.OnSelectionChanged += OnSceneSelectionChanged;
        Callable.From(EnsureTransformGizmo).CallDeferred();
        SetPhysicsProcess(false);
        Interlocked.Exchange(ref _lastConnectedTicks, DateTime.UtcNow.Ticks);
        StartEditorWatchdog();
        _ = ReconnectLoopAsync(_connectionCts.Token);
    }

    /// <summary>
    /// Watches for the editor going away, on a thread of its own.
    ///
    /// Deliberately not part of the reconnect loop. That loop is async, so every await resumes on
    /// Godot's synchronisation context - the main thread - which means it only makes progress while the
    /// main loop is turning. The situation this guard exists for is exactly the one where that can't be
    /// relied on: the viewer's stdout is a pipe owned by OpenCAGE and its window is a child of an
    /// OpenCAGE window, so an editor that dies badly can leave the main loop wedged and the timeout
    /// never reached.
    /// </summary>
    private void StartEditorWatchdog()
    {
        _watchdogThread = new Thread(EditorWatchdogLoop)
        {
            Name = "OpenCAGE editor watchdog",
            IsBackground = true,
        };
        _watchdogThread.Start();
    }

    private void EditorWatchdogLoop()
    {
        while (!_shutdownRequested)
        {
            Thread.Sleep(1000);

            CancellationTokenSource cts = _connectionCts;
            if (cts == null || cts.IsCancellationRequested)
                return;

            //Socket state, not loop progress: a long level load blocks the main thread for a while, and
            //that must not read as a lost editor while the connection is still up
            if (IsWebSocketConnectedSafe())
            {
                Interlocked.Exchange(ref _lastConnectedTicks, DateTime.UtcNow.Ticks);
                continue;
            }

            DateTime lastConnected = new DateTime(Interlocked.Read(ref _lastConnectedTicks), DateTimeKind.Utc);
            if ((DateTime.UtcNow - lastConnected).TotalSeconds < EditorLostShutdownSeconds)
                continue;

            RequestEditorLostShutdown("no Commands Editor for " + EditorLostShutdownSeconds + "s");
            return;
        }
    }

    private bool IsWebSocketConnectedSafe()
    {
        try
        {
            ClientWebSocket client = _client;
            return client != null && client.State == WebSocketState.Open;
        }
        catch
        {
            //Raced with the reconnect loop disposing and replacing it
            return false;
        }
    }

    public void EnsureTransformGizmo()
    {
        if (_transformGizmo != null && GodotObject.IsInstanceValid(_transformGizmo))
            return;

        _transformGizmo = new LevelViewerTransformGizmo();
        _transformGizmo.Name = "TransformGizmo";
        _transformGizmo.OnTransformChanged = OnGizmoTransformChanged;
        _transformGizmo.OnDragCommitted = OnGizmoDragCommitted;
        GetTree().CurrentScene?.AddChild(_transformGizmo);
    }

    private Camera3D FindCamera()
    {
        // Node is named "Camera3D" in the main scene (see main.tscn).
        return GetNodeOrNull<Camera3D>("../Camera3D")
            ?? GetTree()?.Root?.FindChild("Camera3D", true, false) as Camera3D;
    }

    private void OnSceneSelectionChanged(Node3D selectedNode, AlienScene.SelectionOrigin origin)
    {
        Callable.From(() =>
        {
            SyncTransformGizmoToSelection();
            ApplyCameraSelectionBehavior(selectedNode, origin);
        }).CallDeferred();
    }

    /// <summary>
    /// "Focus on selected" reframes the camera for a selection that arrives from OpenCAGE. A viewport
    /// pick is left alone - the user is already looking at what they clicked - though "fix camera to
    /// selected" still starts following it from where the camera is. Either way the thing framed or
    /// followed is what <see cref="AlienScene.TryResolveFocusTarget"/> says, and nothing wider than the
    /// camera's focus range is framed at all: the environment instance is the whole level, and framing
    /// it is the zoom-out in issue 634.
    /// </summary>
    private void ApplyCameraSelectionBehavior(Node3D selectedNode, AlienScene.SelectionOrigin origin)
    {
        LevelViewerCamera camera = FindCamera() as LevelViewerCamera;
        if (camera == null)
            return;

        if (selectedNode == null || !GodotObject.IsInstanceValid(selectedNode) || !_focusOnSelected || _scene == null
            || !_scene.TryResolveFocusTarget(selectedNode, camera.FocusMaxDistance, out Node3D target))
        {
            camera.ClearSelectionFollow();
            return;
        }

        if (origin == AlienScene.SelectionOrigin.ViewportPick)
        {
            if (_fixCameraToSelected)
                camera.FollowSelectionWithoutFraming(target);
            else
                camera.ClearSelectionFollow();
            return;
        }

        camera.HandleSelectionFocus(target, _fixCameraToSelected);
    }

    private void ApplyCameraSettingsFollowState()
    {
        LevelViewerCamera camera = FindCamera() as LevelViewerCamera;
        if (camera == null)
            return;

        if (!_fixCameraToSelected || !_focusOnSelected)
        {
            camera.ClearSelectionFollow();
            return;
        }

        if (_scene != null && _scene.TryGetSelectedEntity(out Node3D selected)
            && _scene.TryResolveFocusTarget(selected, camera.FocusMaxDistance, out Node3D target))
            camera.HandleSelectionFocus(target, fixCamera: true);
        else
            camera.ClearSelectionFollow();
    }

    public void SyncTransformGizmoToSelection(Camera3D camera = null)
    {
        if (_transformGizmo == null || !GodotObject.IsInstanceValid(_transformGizmo))
            EnsureTransformGizmo();

        if (_transformGizmo == null)
            return;

        //Creation mode disables the transform gizmo entirely
        if (CreateModeActive)
        {
            _transformGizmo.ClearTarget();
            return;
        }

        Camera3D activeCamera = camera ?? FindCamera();
        if (_scene != null
            && _scene.TryGetSelectedEntity(out Node3D selected)
            && _scene.SupportsTransformGizmo(selected))
        {
            _transformGizmo.SetTarget(selected, activeCamera);
        }
        else
        {
            _transformGizmo.ClearTarget();
        }
    }

    public override void _ExitTree()
    {
        ViewerLogBridge.ClearConnection();
        ViewerPopulateBridge.ClearConnection();
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

        if (_scene != null)
            _scene.OnSelectionChanged -= OnSceneSelectionChanged;

        if (_scene == null)
            return;

        PreviewVisualUtility.CleanupAllFunctionEntityPreviews(_scene);

        if (_scene.ParentNode != null && GodotObject.IsInstanceValid(_scene.ParentNode))
            _scene.ParentNode.QueueFree();

        base._ExitTree();
    }

    public override void _Process(double delta)
    {
        if (LevelViewerRenderIdleThrottle.IsSuspended)
        {
            Callable.From(() => SetProcess(false)).CallDeferred();
            return;
        }

        // Never let a per-frame exception (e.g. a stale/disposed node reference surfacing as
        // ObjectDisposedException) escape the engine callback and tear down the process.
        try
        {
            FlushPendingParameterSyncs();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] _Process failed: " + ex);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton or InputEventMouseMotion or InputEventKey)
        {
            if (!IsProcessing())
                SetProcess(true);
            LevelViewerRenderIdleThrottle.NotifyUserActivity();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        try
        {
            PhysicsProcessInternal();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] PhysicsProcess failed: " + ex);
        }
        finally
        {
            if (!HasPendingPhysicsWork())
                SetPhysicsProcess(false);
        }
    }

    private void WakePhysicsProcess()
    {
        // Called from the WebSocket receive thread as well as the main thread. IsPhysicsProcessing()
        // and SetPhysicsProcess() touch Node internals and must only run on the main thread, so the
        // whole check is marshalled there (accessing them off-thread can hard-crash the process).
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this) && !IsPhysicsProcessing())
                SetPhysicsProcess(true);
        }).CallDeferred();
    }

    private bool HasPendingPhysicsWork()
    {
        if (!_incomingMessages.IsEmpty)
            return true;
        if (_levelName != "" && _didLoadLevel)
            return true;
        if (_addedEntity != null || _removedEntity != null || _removedComposite != ShortGuid.Invalid)
            return true;
        if (_forceSelectionApply || _currentEntityGOID != _currentEntity)
            return true;

        lock (_lock)
        {
            if (_renderFiltersDirty || _compositeFocusDirty)
                return true;
        }

        return false;
    }

    private void PhysicsProcessInternal()
    {
        if (LevelViewerRenderIdleThrottle.IsSuspended && _incomingMessages.IsEmpty)
            return;

        // A resource snapshot being taken in holds everything behind it: what follows (an entity resource
        // naming a model the snapshot brought) has to see the tables it describes.
        while (!(_scene?.IsResourceSyncBusy ?? false) && _incomingMessages.TryDequeue(out string message))
            HandleMessage(message);

        if (_levelName != "" && _didLoadLevel)
        {
            string level = _levelName;
            string pathToAi = _pathToAI;
            _didLoadLevel = false;

            if (!ShouldSkipLevelReload(level, pathToAi))
                Callable.From(() => _scene.QueueLoadLevel(level, pathToAi)).CallDeferred();
        }

        if (_addedEntity != null)
        {
            ViewerLog.Print("Adding entity: " + _addedEntity.Item2.AsUInt32);
            _scene.AddEntity(_addedEntity.Item1, _addedEntity.Item2);
            _addedEntity = null;
            _scene.RefreshEntityHighlights();
        }

        if (_removedEntity != null)
        {
            ViewerLog.Print("Removing entity: " + _removedEntity.Item2.AsUInt32);
            _scene.RemoveEntity(_removedEntity.Item1, _removedEntity.Item2);
            _removedEntity = null;
        }

        if (_removedComposite != ShortGuid.Invalid)
        {
            ViewerLog.Print("Removing composite: " + _removedComposite.AsUInt32);
            _scene.RemoveComposite(_removedComposite);
            _removedComposite = ShortGuid.Invalid;
        }

        bool sceneFiltersDirty = false;
        lock (_lock)
        {
            sceneFiltersDirty = _sceneFiltersDirty;
            _sceneFiltersDirty = false;
        }

        bool stateInfoDirty = false;
        int navMeshState = -1;
        int coverState = -1;
        lock (_lock)
        {
            stateInfoDirty = _stateInfoDirty;
            _stateInfoDirty = false;
            navMeshState = _pendingNavMeshState;
            coverState = _pendingCoverState;
        }

        if (stateInfoDirty && _scene != null && _scene.Content.Loaded)
            _scene.ApplyStateInfoOverlays(navMeshState, coverState);

        if (sceneFiltersDirty && _scene != null && _scene.Content.Loaded)
            _scene.RefreshSceneGeometryFilters();

        int renderFiltersGeneration = -1;
        lock (_lock)
        {
            if (_renderFiltersDirty)
                renderFiltersGeneration = _renderFiltersGeneration;
        }

        if (renderFiltersGeneration >= 0)
        {
            bool nestedVisibilityDirty = false;
            uint nestedVisibilityPreviousCompositeId = 0;
            HashSet<uint> changedFunctionTypes = null;
            lock (_lock)
            {
                nestedVisibilityDirty = _nestedVisibilityDirty;
                nestedVisibilityPreviousCompositeId = _nestedVisibilityPreviousCompositeId;
                _nestedVisibilityDirty = false;

                changedFunctionTypes = _renderFiltersChangedFunctionTypes;
                _renderFiltersChangedFunctionTypes = null;
            }

            if (nestedVisibilityDirty)
            {
                _scene.RefreshNestedCompositeVisibility(
                    nestedVisibilityPreviousCompositeId,
                    PreviewVisibilitySettings.ActiveCompositeId);
            }
            else
            {
                _scene.RefreshRenderFilters(changedFunctionTypes);
            }

            SyncTransformGizmoToSelection();

            lock (_lock)
            {
                if (_renderFiltersGeneration == renderFiltersGeneration)
                    _renderFiltersDirty = false;
            }
        }

        bool compositeFocusDirty = false;
        lock (_lock)
        {
            compositeFocusDirty = _compositeFocusDirty;
        }

        if (_forceSelectionApply || _currentEntityGOID != _currentEntity)
            ApplySelectionNow();

        if (compositeFocusDirty && _scene != null && _scene.Content.Loaded && !_compositeFocusRefreshScheduled)
        {
            _compositeFocusRefreshScheduled = true;
            int contentGeneration = _scene.ContentGeneration;
            Callable.From(() =>
            {
                try
                {
                    if (_scene != null
                        && GodotObject.IsInstanceValid(_scene)
                        && _scene.Content.Loaded
                        && _scene.ContentGeneration == contentGeneration)
                    {
                        _scene.RefreshCompositeFocus();
                    }
                }
                catch (Exception ex)
                {
                    ViewerLog.PrintErr("[Viewer] Composite focus refresh failed: " + ex);
                }
                finally
                {
                    lock (_lock)
                    {
                        _compositeFocusDirty = false;
                        _compositeFocusRefreshScheduled = false;
                    }
                }
            }).CallDeferred();
        }
    }

    private bool ShouldSkipLevelReload(string levelName, string pathToAi)
    {
        if (_scene?.Content?.Loaded != true || string.IsNullOrEmpty(levelName))
            return false;

        if (!string.Equals(_pathToAI, pathToAi, StringComparison.OrdinalIgnoreCase))
            return false;

        string loadedName = _scene.Content.Level?.Name;
        return !string.IsNullOrEmpty(loadedName)
            && string.Equals(loadedName, levelName, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleMessage(string data)
    {
        Packet packet;
        try
        {
            packet = JsonConvert.DeserializeObject<Packet>(data);
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] HandleMessage deserialize failed: " + ex.Message
                + " | payloadLen=" + (data?.Length ?? 0));
            WebSocketPacketLog.LogReceiveFailed(data?.Length ?? 0, ex.Message);
            return;
        }

        if (packet == null)
        {
            ViewerLog.PrintErr("[Viewer] HandleMessage received null packet | payloadLen=" + (data?.Length ?? 0));
            WebSocketPacketLog.LogReceiveFailed(data?.Length ?? 0, "null packet");
            return;
        }

        WebSocketPacketLog.LogReceived(packet, data?.Length ?? 0);

        // Diagnostic breadcrumb (skip the high-frequency drag/param spam) so viewer.log shows the
        // exact packet sequence leading up to a crash.
        if (packet.packet_event != PacketEvent.ENTITY_MOVED
            && packet.packet_event != PacketEvent.ENTITY_PARAMETER_MODIFIED)
            ViewerLog.Print("Packet: " + packet.packet_event);

        if (packet.version != new Packet().version)
        {
            ViewerLog.PrintErr("Your Commands Editor is utilising a different API version than this Godot client!!\nPlease ensure both are up to date.");
            return;
        }

        // OpenCAGE echoes ENTITY_ADDED with its current (pre-selection) drill path, which would clear our selection.
        if (packet.packet_event == PacketEvent.ENTITY_ADDED)
        {
            lock (_lock)
            {
                if (_viewerOriginatedEntityAdds.Remove(packet.entity))
                    return;
            }
        }

        /* Likewise the ENTITY_DELETED answering an alias this side released: it goes out while OpenCAGE's
         * inspector is still between that alias and whatever replaced it, so its path says nothing is
         * selected - which the general handling below would take as the replacement being abandoned too.
         * Take the removal, leave the selection alone. */
        if (packet.packet_event == PacketEvent.ENTITY_DELETED)
        {
            bool releasedHere;
            lock (_lock)
                releasedHere = _releasedEphemeralAliases.Remove(packet.entity);
            if (releasedHere)
            {
                RemoveDeletedEntity(packet);
                return;
            }
        }

        if (packet.packet_event == PacketEvent.RENDER_FILTERS_CHANGED)
        {
            lock (_lock)
            {
                bool sceneFiltersChanged = RenderFilters.ApplySceneFiltersFromPacket(packet.scene_render_filters);
                if (packet.box_render_filters != null && RenderFilters.ApplyFromPacket(packet.box_render_filters, out HashSet<uint> changed))
                    MarkRenderFiltersDirty(changed);
                if (sceneFiltersChanged)
                    MarkSceneFiltersDirty();
            }
            return;
        }

        if (packet.packet_event == PacketEvent.MATERIAL_MAPPING_MODIFIED)
        {
            SyncedMaterialMappingSet mappingSync = packet.material_mapping;
            int contentGeneration = _scene?.ContentGeneration ?? 0;
            Callable.From(() =>
            {
                try
                {
                    if (_scene != null
                        && GodotObject.IsInstanceValid(_scene)
                        && _scene.Content.Loaded
                        && _scene.ContentGeneration == contentGeneration
                        && mappingSync != null)
                    {
                        ViewerLog.Print("MaterialMapping apply start (id=" + mappingSync.mapping_id + ")");
                        _scene.ApplySyncedMaterialMapping(mappingSync);
                        ViewerLog.Print("MaterialMapping apply complete");
                    }
                }
                catch (Exception ex)
                {
                    ViewerLog.PrintErr("[Viewer] Material mapping sync failed: " + ex);
                }
            }).CallDeferred();
            return;
        }

        if (packet.packet_event == PacketEvent.SETTINGS_CHANGED)
        {
            lock (_lock)
            {
                _showCameraPosition = packet.show_camera_position;
                ApplyViewerSettings(packet);
                ApplyActiveComposite(packet);
                _pendingNavMeshState = packet.show_navmesh_state;
                _pendingCoverState = packet.show_cover_state;
                _stateInfoDirty = true;
                if (RenderFilters.ApplySceneFiltersFromPacket(packet.scene_render_filters))
                    MarkSceneFiltersDirty();
                if (packet.box_render_filters != null)
                    RenderFilters.ApplyFromPacket(packet.box_render_filters);
                MarkRenderFiltersDirty(null);
            }
            Callable.From(ApplyCameraSettingsFollowState).CallDeferred();
            return;
        }

        if (packet.packet_event == PacketEvent.LEVEL_RESOURCES_MODIFIED)
        {
            _scene?.QueueResourceSync(packet);
            return;
        }

        if (packet.packet_event == PacketEvent.VIEWPORT_DROP_REQUEST)
        {
            HandleViewportDropRequest(packet);
            return;
        }

        if (packet.packet_event == PacketEvent.ENTITY_PARAMETER_MODIFIED
            || packet.packet_event == PacketEvent.ENTITY_MOVED
            || packet.packet_event == PacketEvent.ENTITY_RESOURCE_MODIFIED)
        {
            lock (_lock)
            {
                QueueParameterSync(packet);
            }
            return;
        }

        bool refreshCompositeFocusNow = false;
        lock (_lock)
        {
            _levelName = packet.level_name;
            _pathToAI = packet.system_folder;

            uint previousActiveCompositeId = PreviewVisibilitySettings.ActiveCompositeId;
            uint[] previousInstancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath;
            bool previousEntitySelected = _entitySelected;
            uint previousEntity = _currentEntity;
            int previousCompositeDepth = _pathComposites?.Count ?? 0;
            List<uint> previousPathComposites = _pathComposites;

            _pathComposites = packet.path_composites;
            _pathEntities = packet.path_entities;

            int incomingCompositeDepth = _pathComposites?.Count ?? 0;
            _syncedCompositePathDepth = incomingCompositeDepth;

            _compositeLoaded = _pathComposites != null && _pathComposites.Count != 0;
            _entitySelected = _compositeLoaded && _pathComposites.Count == _pathEntities.Count;

            _currentComposite = _compositeLoaded ? _pathComposites[_pathComposites.Count - 1] : 0;
            _currentEntity = _entitySelected ? _pathEntities[_pathEntities.Count - 1] : 0;

            _showCameraPosition = packet.show_camera_position;
            bool hideNestedChanged = ApplyViewerSettings(packet);
            ApplyActiveComposite(packet);

            bool activeCompositeChanged = previousActiveCompositeId != PreviewVisibilitySettings.ActiveCompositeId;
            bool instancePathChanged = !PreviewVisibilitySettings.InstancePathsEqual(
                previousInstancePath,
                PreviewVisibilitySettings.ActiveInstanceEntityPath);
            bool compositePathChanged = incomingCompositeDepth != previousCompositeDepth
                || !PathsEqual(previousPathComposites, packet.path_composites);
            bool navigationChanged = activeCompositeChanged || instancePathChanged;
            bool selectionChanged = previousEntitySelected != _entitySelected
                || previousEntity != _currentEntity
                || compositePathChanged;

            if (navigationChanged)
            {
                ResetProgressiveDeepSelectState();
                refreshCompositeFocusNow = true;
                _scene?.ResetCompositeScopedHides();
            }

            if (selectionChanged)
            {
                TryReleaseEphemeralDeepSelectAliasIfAbandoned(_currentEntity, _currentComposite);
                TrySendPendingEphemeralDeepSelectAliasRelease(null, null, false);
                _forceSelectionApply = true;
                _pendingSelectionOrigin = AlienScene.SelectionOrigin.Remote;
            }

            bool nestedVisibilityOnly = !hideNestedChanged
                && activeCompositeChanged
                && PreviewVisibilitySettings.HideNestedScriptEntities;

            _pendingNavMeshState = packet.show_navmesh_state;
            _pendingCoverState = packet.show_cover_state;
            _stateInfoDirty = true;
            if (RenderFilters.ApplySceneFiltersFromPacket(packet.scene_render_filters))
                MarkSceneFiltersDirty();
            if (packet.box_render_filters != null && RenderFilters.ApplyFromPacket(packet.box_render_filters, out HashSet<uint> changed))
            {
                if (hideNestedChanged)
                    MarkRenderFiltersDirty(null);
                else if (nestedVisibilityOnly)
                    MarkNestedVisibilityDirty(previousActiveCompositeId);
                else
                    MarkRenderFiltersDirty(changed);
            }
            else if (hideNestedChanged)
                MarkRenderFiltersDirty(null);
            else if (nestedVisibilityOnly)
                MarkNestedVisibilityDirty(previousActiveCompositeId);
        }

        if (refreshCompositeFocusNow)
            ApplyCompositeFocusNow();

        switch (packet.packet_event)
        {
            case PacketEvent.ENTITY_ADDED:
            {
                lock (_lock)
                {
                    Composite composite = _scene.Content.Level?.Commands.Entries.FirstOrDefault(o => o.shortGUID.AsUInt32 == packet.composite);
                    if (composite != null)
                    {
                        ShortGuid entityId = new ShortGuid(packet.entity);
                        if (composite.GetEntityByID(entityId) == null)
                        {
                            switch (packet.entity_variant)
                            {
                                case EntityVariant.FUNCTION:
                                {
                                    FunctionEntity functionEntity = new FunctionEntity() { shortGUID = entityId, function = new ShortGuid(packet.entity_function) };
                                    composite.AddFunction(functionEntity);
                                    ApplyEntityAddedParameters(functionEntity, packet);
                                    break;
                                }
                                case EntityVariant.VARIABLE:
                                {
                                    VariableEntity variableEntity = new VariableEntity() { shortGUID = entityId };
                                    composite.AddVariable(variableEntity);
                                    ApplyEntityAddedParameters(variableEntity, packet);
                                    break;
                                }
                                case EntityVariant.ALIAS:
                                {
                                    EntityPath aliasPath = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        aliasPath.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    AliasEntity aliasEntity = new AliasEntity() { shortGUID = entityId, alias = aliasPath };
                                    composite.AddAlias(aliasEntity);
                                    ApplyEntityAddedParameters(aliasEntity, packet);
                                    break;
                                }
                                case EntityVariant.PROXY:
                                {
                                    EntityPath proxy = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        proxy.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    ProxyEntity proxyEntity = new ProxyEntity() { shortGUID = entityId, proxy = proxy };
                                    composite.AddProxy(proxyEntity);
                                    ApplyEntityAddedParameters(proxyEntity, packet);
                                    break;
                                }
                            }
                        }
                    }

                    _addedEntity = new Tuple<ShortGuid, ShortGuid>(new ShortGuid(packet.composite), new ShortGuid(packet.entity));
                }
                break;
            }
            case PacketEvent.ENTITY_DELETED:
                RemoveDeletedEntity(packet);
                break;
            case PacketEvent.COMPOSITE_ADDED:
            {
                lock (_lock)
                {
                    _scene.Content.Level?.Commands.Entries.Add(new Composite() { shortGUID = new ShortGuid(packet.composite) });
                }
                break;
            }
            case PacketEvent.COMPOSITE_DELETED:
            {
                lock (_lock)
                {
                    _scene.Content.Level?.Commands.Entries.RemoveAll(o => o.shortGUID == new ShortGuid(packet.composite));
                    _removedComposite = new ShortGuid(packet.composite);
                }
                break;
            }
            case PacketEvent.LEVEL_LOADED:
            {
                bool skipReload;
                lock (_lock)
                {
                    skipReload = ShouldSkipLevelReload(packet.level_name, packet.system_folder);
                    if (!skipReload)
                    {
                        _didLoadLevel = true;
                        _viewerOriginatedEntityAdds.Clear();
                        _releasedEphemeralAliases.Clear();
                    }
                }

                if (skipReload)
                    break;

                Callable.From(() =>
                {
                    string label = string.IsNullOrWhiteSpace(_levelName) ? "level" : _levelName;
                    _scene.ShowLoadingMessage("Loading level " + label + "...");
                }).CallDeferred();
                break;
            }
            case PacketEvent.COMPOSITE_SELECTED:
                if (ShouldQueueScenePopulate(packet))
                {
                    uint compositeId = packet.composite;
                    Callable.From(() => _scene?.QueuePopulateComposite(new ShortGuid(compositeId))).CallDeferred();
                }
                break;
            case PacketEvent.COMPOSITE_RELOADED:
                // Legacy: hierarchy navigation now uses GENERIC_DATA_SYNC from OpenCAGE.
                break;
        }
    }

    private void QueueParameterSync(Packet packet)
    {
        if (_scene?.Content?.Level == null)
            return;

        // Don't apply position echoes that were caused by our own gizmo drag.
        if (_suppressParameterResync && packet.parameters?.Count > 0)
        {
            bool allPosition = true;
            foreach (SyncedParameter p in packet.parameters)
            {
                if (ParameterSync.GetDataType(p) != DataType.TRANSFORM)
                {
                    allPosition = false;
                    break;
                }
            }
            if (allPosition)
                return;
        }

        ShortGuid entityID = new ShortGuid(packet.entity);
        ShortGuid compositeID = new ShortGuid(packet.composite);
        Composite composite = _scene.Content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID == compositeID);
        if (composite == null)
            return;

        Entity entity = GetEntity(composite, entityID, packet.entity_variant);
        if (entity == null)
            return;

        List<SyncedParameter> syncs = new List<SyncedParameter>();
        if (packet.parameters != null && packet.parameters.Count > 0)
            syncs.AddRange(packet.parameters);
        else
            syncs.AddRange(BuildLegacySyncedParameters(packet, entity));

        foreach (SyncedParameter sync in syncs)
        {
            if (sync == null)
                continue;

            if (ParameterSync.GetDataType(sync) == DataType.RESOURCE &&
                (sync.renderable == null || sync.renderable.Count == 0) &&
                packet.renderable != null && packet.renderable.Count > 0)
            {
                foreach (Tuple<int, int> element in packet.renderable)
                {
                    sync.renderable.Add(new RenderableSyncElement()
                    {
                        model_index = element.Item1,
                        material_index = element.Item2,
                    });
                }
            }

            bool fromPointer = false;
            bool pointedOverride = false;
            ShortGuid visualCompositeID = compositeID;
            ShortGuid visualEntityID = entityID;
            Node3D visualLimitNode = null;

            DataType syncDataType = ParameterSync.GetDataType(sync);
            if (syncDataType == DataType.TRANSFORM || syncDataType == DataType.RESOURCE)
            {
                switch (entity.variant)
                {
                    case EntityVariant.PROXY:
                        HandlePointedEntity(sync, out visualEntityID, out visualCompositeID, ((ProxyEntity)entity).proxy, _scene.Content.Level.Commands.EntryPoints[0], out fromPointer, out pointedOverride);
                        break;
                    case EntityVariant.ALIAS:
                        HandlePointedEntity(sync, out visualEntityID, out visualCompositeID, ((AliasEntity)entity).alias, composite, out fromPointer, out pointedOverride);
                        visualLimitNode = _scene.TryResolveAliasOverrideNode(packet.path_entities);
                        break;
                }
            }

            ParameterSyncKey key = new ParameterSyncKey()
            {
                CompositeId = compositeID.AsUInt32,
                EntityId = entityID.AsUInt32,
                ParameterName = sync.name,
            };

            _pendingParameterSyncs[key] = new PendingParameterSync()
            {
                DataCompositeID = compositeID,
                DataEntityID = entityID,
                VisualCompositeID = visualCompositeID,
                VisualEntityID = visualEntityID,
                Sync = sync,
                FromPointer = fromPointer,
                PointedOverride = pointedOverride,
                VisualLimitNode = visualLimitNode,
            };
        }
    }

    private void FlushPendingParameterSyncs()
    {
        if (_scene == null)
            return;

        List<PendingParameterSync> pendingParameterSyncs = null;
        lock (_lock)
        {
            if (_pendingParameterSyncs.Count == 0)
                return;

            pendingParameterSyncs = new List<PendingParameterSync>(_pendingParameterSyncs.Values);
            _pendingParameterSyncs.Clear();
        }

        for (int i = 0; i < pendingParameterSyncs.Count; i++)
        {
            PendingParameterSync pending = pendingParameterSyncs[i];
            try
            {
                _scene.ApplyEntityParameter(
                    pending.DataCompositeID,
                    pending.DataEntityID,
                    pending.Sync,
                    pending.VisualCompositeID,
                    pending.VisualEntityID,
                    pending.FromPointer,
                    pending.PointedOverride,
                    pending.VisualLimitNode);

                if (!pending.Sync.removed)
                    TryCommitEphemeralDeepSelectAliasAfterParameterSync(
                        pending.DataCompositeID.AsUInt32,
                        pending.DataEntityID.AsUInt32);
            }
            catch (Exception ex)
            {
                ViewerLog.PrintErr("[Viewer] Parameter sync failed: " + ex);
            }
        }

        try
        {
            _scene.RefreshEntityHighlights();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] Alias refresh after sync failed: " + ex);
        }
    }

    private List<SyncedParameter> BuildLegacySyncedParameters(Packet packet, Entity entity)
    {
        List<SyncedParameter> syncs = new List<SyncedParameter>();
        switch (packet.packet_event)
        {
            case PacketEvent.ENTITY_MOVED:
            {
                SyncedParameter transformSync = new SyncedParameter()
                {
                    name = ShortGuidUtils.Generate("position").AsUInt32,
                    removed = !packet.has_transform,
                    data_type = (uint)DataType.TRANSFORM,
                };
                if (packet.has_transform)
                {
                    transformSync.vector3_a = new float[] { packet.position.X, packet.position.Y, packet.position.Z };
                    transformSync.vector3_b = new float[] { packet.rotation.X, packet.rotation.Y, packet.rotation.Z };
                }
                syncs.Add(transformSync);
                break;
            }
            case PacketEvent.ENTITY_RESOURCE_MODIFIED:
            {
                SyncedParameter resourceSync = new SyncedParameter()
                {
                    name = ShortGuidUtils.Generate("resource").AsUInt32,
                    removed = false,
                    data_type = (uint)DataType.RESOURCE,
                };
                foreach (Tuple<int, int> element in packet.renderable)
                {
                    resourceSync.renderable.Add(new RenderableSyncElement()
                    {
                        model_index = element.Item1,
                        material_index = element.Item2,
                    });
                }
                syncs.Add(resourceSync);
                break;
            }
        }
        return syncs;
    }

    private Entity GetEntity(Composite composite, ShortGuid entityID, EntityVariant variant)
    {
        switch (variant)
        {
            case EntityVariant.FUNCTION:
                return composite.functions.FirstOrDefault(o => o.shortGUID == entityID);
            case EntityVariant.VARIABLE:
                return composite.variables.FirstOrDefault(o => o.shortGUID == entityID);
            case EntityVariant.ALIAS:
                return composite.aliases.FirstOrDefault(o => o.shortGUID == entityID);
            case EntityVariant.PROXY:
                return composite.proxies.FirstOrDefault(o => o.shortGUID == entityID);
        }
        return null;
    }

    private void HandlePointedEntity(SyncedParameter sync, out ShortGuid entityID, out ShortGuid compositeID, EntityPath path, Composite startComposite, out bool fromPointer, out bool pointedOverride)
    {
        (Composite pComp, Entity pEnt) = _scene.Content.Level.Commands.Utils.GetResolvedTarget(_scene.Content.Level.Commands.Utils.ResolveAliasOrProxy(path, startComposite));
        entityID = pEnt != null ? pEnt.shortGUID : ShortGuid.Invalid;
        compositeID = pComp != null ? pComp.shortGUID : ShortGuid.Invalid;
        fromPointer = true;
        pointedOverride = !sync.removed;
    }

    private bool ApplyViewerSettings(Packet packet)
    {
        _focusOnSelected = packet.focus_object;
        _fixCameraToSelected = packet.fix_camera_to_selected;
        if (_fixCameraToSelected && !_focusOnSelected)
            _focusOnSelected = true;

        bool hideNestedChanged = _hideNestedScriptEntities != packet.hide_nested_script_entities;
        _hideNestedScriptEntities = packet.hide_nested_script_entities;
        PreviewVisibilitySettings.HideNestedScriptEntities = _hideNestedScriptEntities;

        bool highlightChanged = PreviewVisibilitySettings.HighlightAliases != packet.highlight_aliases
            || PreviewVisibilitySettings.HighlightProxies != packet.highlight_proxies;
        PreviewVisibilitySettings.HighlightAliases = packet.highlight_aliases;
        PreviewVisibilitySettings.HighlightProxies = packet.highlight_proxies;

        LevelViewerTransformSnap.GridSize = packet.transform_grid_snap > 0f ? packet.transform_grid_snap : 0f;
        LevelViewerTransformSnap.RotationDegrees = packet.rotation_snap_degrees > 0f ? packet.rotation_snap_degrees : 0f;

        ApplyDeepSelectModeFromPacket(packet.deep_select_mode);
        ApplyGizmoModeFromPacket(packet.gizmo_mode);
        ApplyCreateModeFromPacket(packet.create_function_type);

        if (packet.model_reference_wireframe != ModelReferenceRenderSettings.WireframeEnabled)
            _scene.SetModelReferenceWireframe(packet.model_reference_wireframe);

        if (highlightChanged && _scene != null)
            _scene.RefreshEntityHighlights();

        return hideNestedChanged;
    }

    private void ApplyDeepSelectModeFromPacket(int mode)
    {
        PreviewVisibilitySettings.DeepSelectModeKind next = mode switch
        {
            1 => PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect,
            2 => PreviewVisibilitySettings.DeepSelectModeKind.AdvancedDeepSelect,
            _ => PreviewVisibilitySettings.DeepSelectModeKind.None,
        };

        if (PreviewVisibilitySettings.DeepSelectMode == next)
            return;

        ResetProgressiveDeepSelectPickState();
        PreviewVisibilitySettings.DeepSelectMode = next;
    }

    private void ApplyGizmoModeFromPacket(int mode)
    {
        LevelViewerTransformGizmo.GizmoMode next = mode switch
        {
            1 => LevelViewerTransformGizmo.GizmoMode.TranslateWorld,
            2 => LevelViewerTransformGizmo.GizmoMode.RotateLocal,
            3 => LevelViewerTransformGizmo.GizmoMode.RotateWorld,
            4 => LevelViewerTransformGizmo.GizmoMode.TranslateLocal,
            _ => LevelViewerTransformGizmo.GizmoMode.None,
        };

        EnsureTransformGizmo();
        if (_transformGizmo == null || _transformGizmo.Mode == next)
            return;

        _transformGizmo.SetMode(next);
        SyncTransformGizmoToSelection();
    }

    private void ApplyCreateModeFromPacket(uint createFunctionType)
    {
        if (_createFunctionType == createFunctionType)
            return;

        _createFunctionType = createFunctionType;
        ApplyCreateModeCursor();

        if (CreateModeActive)
        {
            EnsureTransformGizmo();
            _transformGizmo?.ClearTarget();
        }
        else
        {
            SyncTransformGizmoToSelection();
        }
    }

    private void ApplyCreateModeCursor()
    {
        if (CreateModeActive)
        {
            if (_penCursorTexture == null && ResourceLoader.Exists("res://textures/cursor_pen.png"))
                _penCursorTexture = ResourceLoader.Load<Texture2D>("res://textures/cursor_pen.png");

            if (_penCursorTexture != null)
                Input.SetCustomMouseCursor(_penCursorTexture, Input.CursorShape.Arrow, new Vector2(3f, 29f));
            else
                Input.SetDefaultCursorShape(Input.CursorShape.Cross);
        }
        else
        {
            Input.SetCustomMouseCursor(null);
            Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
        }
    }

    /// <summary>Exit entity creation mode (e.g. gizmo hotkey pressed). Safe to call when inactive.</summary>
    public void ExitCreateMode()
    {
        if (!CreateModeActive)
            return;

        _createFunctionType = 0;
        ApplyCreateModeCursor();
        SyncTransformGizmoToSelection();
    }

    /// <summary>Ctrl+C in the viewport: copy the selected entity to OpenCAGE's shared entity clipboard.</summary>
    public void SendEntityClipboardCopy()
    {
        uint compositeId;
        uint entityId;
        bool entitySelected;
        lock (_lock)
        {
            compositeId = _currentComposite;
            entityId = _currentEntity;
            entitySelected = _entitySelected;
        }

        if (!entitySelected || compositeId == 0 || entityId == 0)
            return;

        SendMessage(new Packet(PacketEvent.ENTITY_CLIPBOARD_COPY)
        {
            composite = compositeId,
            entity = entityId,
        });
    }

    /// <summary>Ctrl+V in the viewport: ask OpenCAGE to paste its entity clipboard into the current composite.</summary>
    public void SendEntityClipboardPaste()
    {
        uint compositeId;
        bool compositeLoaded;
        lock (_lock)
        {
            compositeId = _currentComposite;
            compositeLoaded = _compositeLoaded;
        }

        if (!compositeLoaded || compositeId == 0)
            return;

        SendMessage(new Packet(PacketEvent.ENTITY_CLIPBOARD_PASTE)
        {
            composite = compositeId,
        });
    }

    /// <summary>
    /// Creation-mode click: raycast the scene for a placement position and ask OpenCAGE to create
    /// an entity of the active function type there.
    /// </summary>
    public bool TryCreateEntityAtScreen(Camera3D camera, Vector2 screenPosition)
    {
        if (!CreateModeActive || _scene == null || !_scene.Content.Loaded)
            return false;

        uint compositeId;
        bool compositeLoaded;
        lock (_lock)
        {
            compositeId = _currentComposite;
            compositeLoaded = _compositeLoaded;
        }

        if (!compositeLoaded || compositeId == 0)
            return false;

        if (!_scene.TryComputeCreatePlacement(camera, screenPosition, out Vector3 godotLocalPosition))
            return false;

        Vector3 cathodePosition = CathodeCoordinates.PositionFromGodot(godotLocalPosition);

        Packet packet = new Packet(PacketEvent.ENTITY_CREATE_REQUEST)
        {
            composite = compositeId,
            entity_function = _createFunctionType,
            has_transform = true,
            position = new System.Numerics.Vector3(cathodePosition.X, cathodePosition.Y, cathodePosition.Z),
            rotation = new System.Numerics.Vector3(0f, 0f, 0f),
        };
        SendMessage(packet);
        return true;
    }

    /// <summary>
    /// A composite dragged out of OpenCAGE's browser and dropped on the viewport. OpenCAGE can only
    /// tell us where in the viewport the drop landed - the geometry to hit is all on this side - so we
    /// raycast it exactly as creation mode does on a click and answer with the placement.
    /// </summary>
    private void HandleViewportDropRequest(Packet packet)
    {
        if (packet.create_composite_instance == 0 || _scene == null || !_scene.Content.Loaded)
            return;

        uint compositeId;
        bool compositeLoaded;
        lock (_lock)
        {
            compositeId = _currentComposite;
            compositeLoaded = _compositeLoaded;
        }

        if (!compositeLoaded || compositeId == 0)
            return;

        Camera3D camera = FindCamera();
        Viewport viewport = camera?.GetViewport();
        if (viewport == null)
            return;

        Vector2 size = viewport.GetVisibleRect().Size;
        Vector2 screenPosition = new Vector2(
            Mathf.Clamp(packet.drop_viewport_x, 0f, 1f) * size.X,
            Mathf.Clamp(packet.drop_viewport_y, 0f, 1f) * size.Y);

        if (!_scene.TryComputeCreatePlacement(camera, screenPosition, out Vector3 godotLocalPosition))
            return;

        Vector3 cathodePosition = CathodeCoordinates.PositionFromGodot(godotLocalPosition);
        SendMessage(new Packet(PacketEvent.ENTITY_CREATE_REQUEST)
        {
            composite = compositeId,
            create_composite_instance = packet.create_composite_instance,
            has_transform = true,
            position = new System.Numerics.Vector3(cathodePosition.X, cathodePosition.Y, cathodePosition.Z),
            rotation = new System.Numerics.Vector3(0f, 0f, 0f),
        });
    }

    private bool ApplyActiveComposite(Packet packet)
    {
        uint activeCompositeId = _compositeLoaded && _pathComposites != null && _pathComposites.Count > 0
            ? _currentComposite
            : packet.composite;

        PreviewVisibilitySettings.SyncFromEditorPath(_pathEntities, _pathComposites, _entitySelected);

        bool changed = PreviewVisibilitySettings.ActiveCompositeId != activeCompositeId;
        PreviewVisibilitySettings.ActiveCompositeId = activeCompositeId;
        return changed;
    }

    /// <summary>
    /// Full scene repopulate is only for composite-browser root switches (COMPOSITE_SELECTED with a
    /// single composite in the path). Hierarchy drill sends GENERIC_DATA_SYNC with instance steps.
    /// </summary>
    private static bool ShouldQueueScenePopulate(Packet packet)
    {
        if (packet.packet_event != PacketEvent.COMPOSITE_SELECTED || packet.composite == 0)
            return false;

        return packet.path_composites == null || packet.path_composites.Count <= 1;
    }

    private void MarkCompositeFocusDirty()
    {
        lock (_lock)
        {
            _compositeFocusDirty = true;
        }

        WakePhysicsProcess();
    }

    private void ApplyCompositeFocusNow()
    {
        if (_scene == null || !_scene.Content.Loaded)
            return;

        _scene.RefreshCompositeFocus();
        lock (_lock)
        {
            _compositeFocusDirty = false;
            _compositeFocusRefreshScheduled = false;
        }
    }

    private bool _sceneFiltersDirty = false;
    private bool _stateInfoDirty = false;
    private int _pendingNavMeshState = -1;
    private int _pendingCoverState = -1;

    /* Occlusion/collision geometry is spawned hidden, so a filter change is only a visibility flip */
    private void MarkSceneFiltersDirty()
    {
        _sceneFiltersDirty = true;
    }

    private void MarkRenderFiltersDirty(HashSet<uint> changedFunctionTypes)
    {
        _renderFiltersDirty = true;
        _nestedVisibilityDirty = false;
        _renderFiltersGeneration++;
        if (changedFunctionTypes == null || changedFunctionTypes.Count == 0)
            return;

        if (_renderFiltersChangedFunctionTypes == null)
            _renderFiltersChangedFunctionTypes = new HashSet<uint>(changedFunctionTypes);
        else
            _renderFiltersChangedFunctionTypes.UnionWith(changedFunctionTypes);
    }

    private void MarkNestedVisibilityDirty(uint previousActiveCompositeId)
    {
        _renderFiltersDirty = true;
        _nestedVisibilityDirty = true;
        _nestedVisibilityPreviousCompositeId = previousActiveCompositeId;
        _renderFiltersChangedFunctionTypes = null;
        _renderFiltersGeneration++;
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

        // Fire-and-forget task: an escaping exception would be unobserved. The inner try handles
        // per-attempt reconnects; this outer guard ensures nothing (even prologue failures) can
        // surface as an unobserved task exception.
        try
        {
            await ReconnectLoopBodyAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("[Viewer] Reconnect loop terminated unexpectedly: " + ex);
        }
    }

    private async Task ReconnectLoopBodyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            _client?.Dispose();
            _client = new ClientWebSocket();

            ViewerLog.Print("Trying to connect to Commands Editor...");

            try
            {
                await _client.ConnectAsync(
                    new Uri($"ws://localhost:{_webSocketPort}/commands_editor"),
                    cancellationToken);
                ViewerLog.Print("Connected to Commands Editor!");
                ViewerLogBridge.NotifyConnected();

                Interlocked.Exchange(ref _lastConnectedTicks, DateTime.UtcNow.Ticks);

                bool closedByEditor = await ReceiveLoopAsync(_client, cancellationToken);

                //A close frame means OpenCAGE shut the session down on purpose - no need to wait around
                if (closedByEditor)
                {
                    RequestEditorLostShutdown("the Commands Editor closed the connection");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ViewerLog.PrintErr("WebSocket connection error: " + ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            ViewerLog.Print("Disconnected from Commands Editor!");

            //Giving up is the watchdog thread's job - see StartEditorWatchdog. Keeping the deadline out
            //of this loop is the whole point: this loop can stall, and the deadline must not stall with it.
            if (_shutdownRequested)
                break;

            try
            {
                await Task.Delay(1500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <returns>True if the editor closed the session cleanly (rather than the connection just dropping).</returns>
    private async Task<bool> ReceiveLoopAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        StringBuilder messageBuilder = new StringBuilder();

        while (client.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return true;

            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                _incomingMessages.Enqueue(messageBuilder.ToString());
                WakePhysicsProcess();
                LevelViewerRenderIdleThrottle.NotifyUserActivity();
                messageBuilder.Clear();
            }
        }

        return false;
    }

    /// <summary>
    /// Close the viewer because the editor that owns it has gone away.
    /// </summary>
    private void RequestEditorLostShutdown(string reason)
    {
        if (_shutdownRequested)
            return;
        _shutdownRequested = true;

        ViewerLog.Print("[Viewer] Closing: " + reason + ".");

        //Called from the watchdog or the connection task, so hand the actual quit to the main thread
        Callable.From(() =>
        {
            SceneTree tree = GetTree();
            if (tree != null)
                tree.Quit();
            else
                OS.Kill(OS.GetProcessId()); //not in the tree (shouldn't happen) - make sure we still exit
        }).CallDeferred();

        //...and then make sure of it. Both CallDeferred and SceneTree.Quit need the main loop to still be
        //turning, and an editor that died badly is exactly the case where it might not be. Leaving an
        //orphaned viewer behind is the one outcome this must not have, so the polite path gets a grace
        //period and then the process takes itself down from a thread that needs nothing from Godot.
        Thread forceExit = new Thread(() =>
        {
            Thread.Sleep(HardExitGraceMilliseconds);
            try
            {
                ViewerLog.PrintErr("[Viewer] Shutdown did not complete in time - exiting the hard way.");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
            catch
            {
            }
        })
        {
            Name = "OpenCAGE viewer force exit",
            IsBackground = true,
        };
        forceExit.Start();
    }

    public void SendViewerLog(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        SendMessage(new Packet(PacketEvent.VIEWER_LOG)
        {
            log_message = message,
            log_is_error = isError,
        });
    }

    public void NotifyViewerPopulateStarted(string levelName, uint populateToken)
    {
        SendMessageBlocking(new Packet(PacketEvent.VIEWER_POPULATE_STARTED)
        {
            level_name = levelName ?? string.Empty,
            populate_token = populateToken,
        });
    }

    public void NotifyViewerPopulateFinished(uint populateToken)
    {
        SendMessageBlocking(new Packet(PacketEvent.VIEWER_POPULATE_FINISHED)
        {
            populate_token = populateToken,
        });
    }

    private void SendMessageBlocking(Packet content)
    {
        try
        {
            SendMessageAsync(content).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("Failed to send websocket message: " + ex.Message);
        }
    }

    public async void SendMessage(Packet content)
    {
        // async void: any escaping exception would become an unobserved exception and can
        // terminate the process, so everything must be caught here.
        try
        {
            await SendMessageAsync(content);
        }
        catch (Exception ex)
        {
            ViewerLog.PrintErr("Failed to send websocket message: " + ex.Message);
        }
    }

    public void SendViewportModeToEditor()
    {
        int gizmoMode = _transformGizmo != null
            ? (int)_transformGizmo.Mode
            : (int)LevelViewerTransformGizmo.GizmoMode.None;

        SendMessage(new Packet(PacketEvent.VIEWPORT_MODE_CHANGED)
        {
            deep_select_mode = (int)PreviewVisibilitySettings.DeepSelectMode,
            gizmo_mode = gizmoMode,
            create_function_type = _createFunctionType,
        });
    }

    private async Task SendMessageAsync(Packet content)
    {
        // Capture locally: _connectionCts is nulled/disposed on _ExitTree, possibly from another thread.
        CancellationTokenSource cts = _connectionCts;
        if (cts == null || _client == null || _client.State != WebSocketState.Open)
            return;

        CancellationToken token = cts.Token;
        await _sendLock.WaitAsync(token);
        try
        {
            if (_client == null || _client.State != WebSocketState.Open)
                return;

            string json = JsonConvert.SerializeObject(content);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            WebSocketPacketLog.LogSent(content, json.Length);
            try
            {
                await _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            catch (Exception ex)
            {
                ViewerLog.PrintErr("Failed to send websocket message: " + ex.Message);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendNewAliasToEditorAsync(
        Composite ownerComposite,
        AliasEntity alias,
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected)
    {
        Packet addPacket = BuildAliasEntityAddedPacket(ownerComposite, alias, pathEntities, pathComposites);
        _viewerOriginatedEntityAdds.Add(alias.shortGUID.AsUInt32);
        await SendMessageAsync(addPacket);

        /* Only now let go of the alias this one replaces. Sent the other way round, OpenCAGE deletes the
         * old alias while it is still its selection, and the ENTITY_DELETED it answers with says nothing
         * is selected - which this side reads as the new alias being abandoned too, and releases it. */
        TrySendPendingEphemeralDeepSelectAliasRelease(null, null, false);
    }

    private static Packet BuildSelectionPacket(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        Packet packet = new Packet(PacketEvent.ENTITY_SELECTED)
        {
            path_entities = pathEntities ?? new List<uint>(),
            path_composites = pathComposites ?? new List<uint>(),
            composite = pathComposites != null && pathComposites.Count > 0
                ? pathComposites[pathComposites.Count - 1]
                : 0,
        };

        if (entitySelected && pathEntities != null && pathEntities.Count > 0)
            packet.entity = pathEntities[pathEntities.Count - 1];

        return packet;
    }

    public void TryPickSelectAtScreen(Camera3D camera, Vector2 screenPosition)
    {
        if (_scene == null || camera == null || !_scene.Content.Loaded)
            return;

        if (!_scene.TryPickSelectionTarget(camera, screenPosition, out LevelViewerPick.SelectionTarget target, out _))
        {
            TryClearEntitySelection();
            return;
        }

        uint activeCompositeId = PreviewVisibilitySettings.ActiveCompositeId;
        if (activeCompositeId == 0 && _pathComposites != null && _pathComposites.Count > 0)
            activeCompositeId = _pathComposites[_pathComposites.Count - 1];

        Commands commands = _scene.Content.Level.Commands;
        bool built = false;
        List<uint> pathEntities = null;
        List<uint> pathComposites = null;
        bool entitySelected;
        bool createdNewAlias = false;
        int deepSelectDepth = 0;

        switch (PreviewVisibilitySettings.DeepSelectMode)
        {
            case PreviewVisibilitySettings.DeepSelectModeKind.AdvancedDeepSelect:
                ResetProgressiveDeepSelectState();
                {
                    uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
                    if (LevelViewerPick.GetDeepSelectMaxDepth(target, activeCompositeId, instancePath, commands) > 0)
                    {
                        built = TryPickDeepSelectViaAlias(
                            target,
                            activeCompositeId,
                            commands,
                            deepSelectDepth: 0,
                            out pathEntities,
                            out pathComposites,
                            out createdNewAlias);
                    }
                    else
                    {
                        built = LevelViewerPick.TryBuildActiveCompositeSelectionPath(
                            target,
                            activeCompositeId,
                            out pathEntities,
                            out pathComposites);
                    }
                }
                entitySelected = built;
                break;
            case PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect:
            {
                deepSelectDepth = ResolveProgressiveDeepSelectDepth(target, activeCompositeId);
                if (deepSelectDepth > 0)
                {
                    built = TryPickDeepSelectViaAlias(
                        target,
                        activeCompositeId,
                        commands,
                        deepSelectDepth,
                        out pathEntities,
                        out pathComposites,
                        out createdNewAlias);
                }

                if (!built)
                {
                    if (deepSelectDepth > 0)
                        ResetProgressiveDeepSelectState();

                    built = LevelViewerPick.TryBuildActiveCompositeSelectionPath(
                        target,
                        activeCompositeId,
                        out pathEntities,
                        out pathComposites);
                }

                entitySelected = built;
                break;
            }
            default:
                built = LevelViewerPick.TryBuildActiveCompositeSelectionPath(
                    target,
                    activeCompositeId,
                    out pathEntities,
                    out pathComposites);
                entitySelected = true;
                break;
        }

        if (!built)
            return;

        if (PreviewVisibilitySettings.DeepSelectMode != PreviewVisibilitySettings.DeepSelectModeKind.None
            && !PreviewVisibilitySettings.InstancePathsEqual(
                PreviewVisibilitySettings.CompositeFocusInstancePath,
                PreviewVisibilitySettings.ActiveInstanceEntityPath))
        {
            // LMB only selects aliases; grey-out follows OpenCAGE drill scope (Ctrl+MMB / hierarchy).
            PreviewVisibilitySettings.ResetCompositeFocusToActiveInstancePath();
            MarkCompositeFocusDirty();
        }

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        ApplySelectionNowAndCleanupEphemeralAlias(pathEntities, pathComposites, entitySelected);
        UpdateEphemeralDeepSelectAliasTracking(pathEntities, pathComposites, entitySelected);

        // New alias: selection path is bundled into ENTITY_ADDED so OpenCAGE can add+select atomically.
        if (createdNewAlias)
        {
            uint ownerCompositeId = pathComposites[pathComposites.Count - 1];
            uint aliasEntityId = pathEntities[pathEntities.Count - 1];
            LevelViewerPick.TryBuildAliasSelectionPath(
                target,
                ownerCompositeId,
                aliasEntityId,
                GetSyncedPathCompositesSnapshot(),
                out pathEntities,
                out pathComposites);

            Composite ownerComposite = commands.GetComposite(new ShortGuid(ownerCompositeId));
            AliasEntity alias = ownerComposite?.GetEntityByID(new ShortGuid(aliasEntityId)) as AliasEntity;
            if (alias != null)
                _ = SendNewAliasToEditorAsync(ownerComposite, alias, pathEntities, pathComposites, entitySelected);

            return;
        }

        SendSelectionToEditorWithPendingEphemeralDelete(pathEntities, pathComposites, entitySelected);
    }

    public void TryPickDrillIntoCompositeAtScreen(Camera3D camera, Vector2 screenPosition)
    {
        if (_scene == null || camera == null || !_scene.Content.Loaded)
            return;

        if (!_scene.TryPickSelectionTarget(camera, screenPosition, out LevelViewerPick.SelectionTarget target))
            return;

        uint activeCompositeId = PreviewVisibilitySettings.ActiveCompositeId;
        if (activeCompositeId == 0 && _pathComposites != null && _pathComposites.Count > 0)
            activeCompositeId = _pathComposites[_pathComposites.Count - 1];

        Commands commands = _scene.Content.Level.Commands;
        List<uint> pathEntities = null;
        List<uint> pathComposites = null;
        bool built = false;

        switch (PreviewVisibilitySettings.DeepSelectMode)
        {
            case PreviewVisibilitySettings.DeepSelectModeKind.AdvancedDeepSelect:
                built = LevelViewerPick.TryBuildDeepDrillPath(
                    target,
                    commands,
                    out pathEntities,
                    out pathComposites);
                break;
            case PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect:
            {
                uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
                LevelViewerPick.SelectionTarget drillTarget = ResolveProgressiveDeepSelectDrillTarget(
                    target,
                    activeCompositeId,
                    out int drillDepth);
                if (drillDepth > 0)
                {
                    built = LevelViewerPick.TryBuildProgressiveDeepDrillPath(
                        drillTarget,
                        activeCompositeId,
                        instancePath,
                        drillDepth,
                        commands,
                        out pathEntities,
                        out pathComposites);
                }

                if (!built)
                {
                    built = LevelViewerPick.TryBuildCompositeDrillPath(
                        target,
                        activeCompositeId,
                        commands,
                        out pathEntities,
                        out pathComposites);
                }

                break;
            }
            default:
                built = LevelViewerPick.TryBuildCompositeDrillPath(
                    target,
                    activeCompositeId,
                    commands,
                    out pathEntities,
                    out pathComposites);
                break;
        }

        if (!built)
            return;

        bool entitySelected = false;
        if (PreviewVisibilitySettings.DeepSelectMode != PreviewVisibilitySettings.DeepSelectModeKind.None)
        {
            LevelViewerPick.SelectionTarget preserveTarget = target;
            if (PreviewVisibilitySettings.DeepSelectMode == PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect
                && _progressiveDeepSelectEntityIds != null
                && _progressiveDeepSelectCompositeIds != null
                && _progressiveDeepSelectEntityIds.Count > 0)
            {
                preserveTarget = new LevelViewerPick.SelectionTarget(
                    _progressiveDeepSelectEntityIds,
                    _progressiveDeepSelectCompositeIds,
                    _progressiveDeepSelectLeafId);
            }

            TryMergePreservedSelectionIntoDrillPath(
                pathEntities,
                pathComposites,
                preserveTarget,
                commands,
                out pathEntities,
                out pathComposites,
                out entitySelected);
        }

        if (PreviewVisibilitySettings.DeepSelectMode == PreviewVisibilitySettings.DeepSelectModeKind.DeepSelect)
            ResetProgressiveDeepSelectState();

        UpdateCompositeFocusForDrillPath(pathEntities, pathComposites, entitySelected);
        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditorWithPendingEphemeralDelete(pathEntities, pathComposites, entitySelected);
        ApplySelectionNowAndCleanupEphemeralAlias(pathEntities, pathComposites, entitySelected);
        ApplyCompositeFocusNow();
    }

    private bool TryMergePreservedSelectionIntoDrillPath(
        List<uint> drillPathEntities,
        List<uint> drillPathComposites,
        LevelViewerPick.SelectionTarget pickTarget,
        Commands commands,
        out List<uint> pathEntities,
        out List<uint> pathComposites,
        out bool entitySelected)
    {
        pathEntities = drillPathEntities;
        pathComposites = drillPathComposites;
        entitySelected = false;

        if (commands == null
            || drillPathEntities == null
            || drillPathComposites == null
            || drillPathEntities.Count == 0
            || drillPathComposites.Count != drillPathEntities.Count + 1)
        {
            return false;
        }

        uint selectedEntityId;
        uint ownerCompositeId;
        bool hadSelection;
        lock (_lock)
        {
            hadSelection = _entitySelected;
            selectedEntityId = _currentEntity;
            ownerCompositeId = _pathComposites != null && _pathComposites.Count > 0
                ? _pathComposites[_pathComposites.Count - 1]
                : 0;
        }

        if (!hadSelection || selectedEntityId == 0)
            return false;

        uint enteredCompositeId = drillPathComposites[drillPathComposites.Count - 1];
        uint preserveEntityId = ResolvePreservedEntityForDrill(
            selectedEntityId,
            ownerCompositeId,
            enteredCompositeId,
            pickTarget,
            commands);
        if (preserveEntityId == 0)
            return false;

        pathEntities = new List<uint>(drillPathEntities);
        pathEntities.Add(preserveEntityId);
        pathComposites = new List<uint>(drillPathComposites);
        entitySelected = true;
        return true;
    }

    private static uint ResolvePreservedEntityForDrill(
        uint selectedEntityId,
        uint ownerCompositeId,
        uint enteredCompositeId,
        LevelViewerPick.SelectionTarget pickTarget,
        Commands commands)
    {
        Composite enteredComposite = commands.GetComposite(new ShortGuid(enteredCompositeId));
        if (enteredComposite == null)
            return 0;

        if (ownerCompositeId != 0)
        {
            Composite ownerComposite = commands.GetComposite(new ShortGuid(ownerCompositeId));
            Entity selectedEntity = ownerComposite?.GetEntityByID(new ShortGuid(selectedEntityId));
            if (selectedEntity?.variant == EntityVariant.ALIAS)
            {
                if (ownerCompositeId == enteredCompositeId)
                    return selectedEntityId;

                AliasEntity alias = (AliasEntity)selectedEntity;
                if (alias.alias?.path != null && alias.alias.path.Length > 0)
                {
                    uint resolved = alias.alias.path[alias.alias.path.Length - 1].AsUInt32;
                    if (enteredComposite.GetEntityByID(new ShortGuid(resolved)) != null)
                        return resolved;
                }
            }
            else if (selectedEntity != null
                && enteredComposite.GetEntityByID(new ShortGuid(selectedEntityId)) != null)
            {
                return selectedEntityId;
            }
        }

        return LevelViewerPick.TryFindPreservedEntityInComposite(pickTarget, enteredCompositeId, commands);
    }

    public void ResetProgressiveDeepSelectPickState()
    {
        ResetProgressiveDeepSelectState();
    }

    public void TryClearEntitySelection()
    {
        ResetProgressiveDeepSelectState();

        if (_scene == null || !_scene.Content.Loaded)
            return;

        List<uint> pathEntities;
        List<uint> pathComposites;
        bool entitySelected;

        lock (_lock)
        {
            if (!_entitySelected || _pathComposites == null || _pathComposites.Count == 0)
            {
                TryReleaseEphemeralDeepSelectAlias();
                TrySendPendingEphemeralDeepSelectAliasRelease(null, null, false);
                return;
            }

            if (_pathEntities == null || _pathEntities.Count != _pathComposites.Count)
                return;

            pathEntities = new List<uint>(_pathEntities);
            pathEntities.RemoveAt(pathEntities.Count - 1);
            pathComposites = new List<uint>(_pathComposites);
            entitySelected = false;
        }

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditorWithPendingEphemeralDelete(pathEntities, pathComposites, entitySelected);
        ApplySelectionNowAndCleanupEphemeralAlias(pathEntities, pathComposites, entitySelected);
    }

    /// <summary>Steps back one level: deselects the current entity, or pops out of a nested composite instance.</summary>
    public void TryStepBackHierarchy()
    {
        if (_scene == null || !_scene.Content.Loaded)
            return;

        List<uint> pathEntities;
        List<uint> pathComposites;
        bool entitySelected;

        lock (_lock)
        {
            if (_pathComposites == null || _pathComposites.Count == 0)
                return;

            pathComposites = new List<uint>(_pathComposites);
            pathEntities = _pathEntities != null ? new List<uint>(_pathEntities) : new List<uint>();

            if (_entitySelected && pathEntities.Count > 0 && pathEntities.Count == pathComposites.Count)
            {
                pathEntities.RemoveAt(pathEntities.Count - 1);
                entitySelected = false;
            }
            else if (pathComposites.Count > 1)
            {
                pathComposites.RemoveAt(pathComposites.Count - 1);
                entitySelected = pathEntities.Count > 0;
            }
            else
                return;
        }

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditorWithPendingEphemeralDelete(pathEntities, pathComposites, entitySelected);
        ApplySelectionNowAndCleanupEphemeralAlias(pathEntities, pathComposites, entitySelected);
    }

    private void ApplyLocalSelection(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        lock (_lock)
        {
            uint previousActiveComposite = PreviewVisibilitySettings.ActiveCompositeId;
            uint[] previousInstancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath;

            _pathEntities = pathEntities;
            _pathComposites = pathComposites;
            _compositeLoaded = _pathComposites != null && _pathComposites.Count != 0;
            _entitySelected = entitySelected;
            _currentComposite = _compositeLoaded ? _pathComposites[_pathComposites.Count - 1] : 0;
            _currentEntity = _entitySelected && _pathEntities != null && _pathEntities.Count > 0
                ? _pathEntities[_pathEntities.Count - 1]
                : 0;
            _forceSelectionApply = true;
            _pendingSelectionOrigin = AlienScene.SelectionOrigin.ViewportPick;

            bool preserveNavigationScope = ShouldPreserveNavigationScopeForSelection(
                pathEntities,
                pathComposites,
                entitySelected,
                previousActiveComposite,
                previousInstancePath);

            if (!preserveNavigationScope)
            {
                PreviewVisibilitySettings.SyncFromEditorPath(_pathEntities, _pathComposites, _entitySelected);
                PreviewVisibilitySettings.ActiveCompositeId = _currentComposite;
            }

            if (!preserveNavigationScope
                && (previousActiveComposite != PreviewVisibilitySettings.ActiveCompositeId
                    || !PreviewVisibilitySettings.InstancePathsEqual(
                        previousInstancePath,
                        PreviewVisibilitySettings.ActiveInstanceEntityPath)))
            {
                MarkCompositeFocusDirty();
                _scene?.ResetCompositeScopedHides();
            }
        }
    }

    private void UpdateCompositeFocusForDrillPath(
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected)
    {
        if (pathEntities == null || pathComposites == null || pathEntities.Count == 0)
            return;

        uint[] focusPath = PreviewVisibilitySettings.BuildInstanceEntityPath(
            pathEntities,
            pathComposites,
            entitySelected);
        if (PreviewVisibilitySettings.InstancePathsEqual(
                focusPath,
                PreviewVisibilitySettings.CompositeFocusInstancePath))
        {
            return;
        }

        PreviewVisibilitySettings.SetCompositeFocusInstancePath(focusPath);
        MarkCompositeFocusDirty();
    }

    /// <summary>
    /// Progressive deep-select alias picks change selection without drilling into nested composites.
    /// Keep alias highlights and composite focus tied to OpenCAGE navigation, not local alias paths.
    /// </summary>
    private static bool ShouldPreserveNavigationScopeForSelection(
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected,
        uint previousActiveComposite,
        uint[] previousInstancePath)
    {
        if (PreviewVisibilitySettings.DeepSelectMode == PreviewVisibilitySettings.DeepSelectModeKind.None)
            return false;

        if (pathComposites == null || pathComposites.Count == 0)
            return false;

        if (pathComposites[pathComposites.Count - 1] != previousActiveComposite)
            return false;

        uint[] selectionInstancePath = PreviewVisibilitySettings.BuildInstanceEntityPath(
            pathEntities,
            pathComposites,
            entitySelected);

        return PreviewVisibilitySettings.InstancePathsEqual(selectionInstancePath, previousInstancePath);
    }

    private void ApplySelectionNowAndCleanupEphemeralAlias(
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected)
    {
        ApplySelectionNow();
        TryReleaseEphemeralDeepSelectAliasIfAbandoned(
            entitySelected && pathEntities != null && pathEntities.Count > 0
                ? pathEntities[pathEntities.Count - 1]
                : 0,
            pathComposites != null && pathComposites.Count > 0
                ? pathComposites[pathComposites.Count - 1]
                : 0);
    }

    private void ApplySelectionNow()
    {
        if (_scene == null || !_scene.Content.Loaded)
            return;

        bool entitySelected;
        List<uint> pathEntities;
        List<uint> pathComposites;
        AlienScene.SelectionOrigin origin;

        lock (_lock)
        {
            if (!_forceSelectionApply && _currentEntityGOID == _currentEntity)
                return;

            entitySelected = _entitySelected;
            pathEntities = _pathEntities;
            pathComposites = _pathComposites;
            origin = _pendingSelectionOrigin;
            _pendingSelectionOrigin = AlienScene.SelectionOrigin.Remote;
            _currentEntityGOID = _currentEntity;
            _forceSelectionApply = false;
        }

        _scene.SelectEntity(pathEntities, pathComposites, entitySelected, origin);
    }

    private static bool PathsEqual(IReadOnlyList<uint> left, IReadOnlyList<uint> right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null)
            return left == right;

        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        return true;
    }

    private void SendSelectionToEditor(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        if (pathComposites == null || pathComposites.Count == 0)
            return;

        Packet packet = BuildSelectionPacket(pathEntities, pathComposites, entitySelected);
        if (entitySelected && pathEntities != null && pathEntities.Count > 0)
            TryFillEntityMetadata(packet);

        SendMessage(packet);
        ScheduleEmbeddedInputFocusRestore();
    }

    private void SendSelectionToEditorWithPendingEphemeralDelete(
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected)
    {
        bool canBundleSelection = entitySelected
            && pathEntities != null
            && pathEntities.Count > 0
            && pathComposites != null
            && pathComposites.Count == pathEntities.Count;

        if (_pendingEphemeralDeepSelectReleaseEntityId != 0)
        {
            SendPendingEphemeralDeepSelectAliasRelease(
                pathEntities,
                pathComposites,
                canBundleSelection);

            if (canBundleSelection)
                return;
        }

        SendSelectionToEditor(pathEntities, pathComposites, entitySelected);
    }

    private void SendPendingEphemeralDeepSelectAliasRelease(
        List<uint> selectionPathEntities,
        List<uint> selectionPathComposites,
        bool includeSelection)
    {
        if (_pendingEphemeralDeepSelectReleaseEntityId == 0)
            return;

        //The selection that replaced it rides along, so OpenCAGE applies the two in one step
        Packet packet = new Packet(PacketEvent.ENTITY_ALIAS_RELEASED)
        {
            composite = _pendingEphemeralDeepSelectReleaseCompositeId,
            entity = _pendingEphemeralDeepSelectReleaseEntityId,
            entity_variant = EntityVariant.ALIAS,
        };

        /* Remembered so the ENTITY_DELETED that may answer is taken as just that. An alias OpenCAGE keeps
         * never answers, and its id stays here until the level is reloaded; the one cost is that a later
         * deletion of it from OpenCAGE's side would also be read as an answer, its path left unapplied until
         * the packet after. */
        lock (_lock)
            _releasedEphemeralAliases.Add(packet.entity);

        if (includeSelection
            && selectionPathEntities != null
            && selectionPathComposites != null
            && selectionPathEntities.Count > 0
            && selectionPathEntities.Count == selectionPathComposites.Count)
        {
            packet.path_entities = new List<uint>(selectionPathEntities);
            packet.path_composites = new List<uint>(selectionPathComposites);
        }

        ClearPendingEphemeralDeepSelectRelease();
        SendMessage(packet);
    }

    private bool TrySendPendingEphemeralDeepSelectAliasRelease(
        List<uint> selectionPathEntities,
        List<uint> selectionPathComposites,
        bool includeSelection)
    {
        if (_pendingEphemeralDeepSelectReleaseEntityId == 0)
            return false;

        SendPendingEphemeralDeepSelectAliasRelease(
            selectionPathEntities,
            selectionPathComposites,
            includeSelection);
        return true;
    }

    private void ClearPendingEphemeralDeepSelectRelease()
    {
        _pendingEphemeralDeepSelectReleaseCompositeId = 0;
        _pendingEphemeralDeepSelectReleaseEntityId = 0;
    }

    private void RemoveDeletedEntity(Packet packet)
    {
        lock (_lock)
        {
            Composite composite = _scene.Content.Level?.Commands.Entries.FirstOrDefault(o => o.shortGUID.AsUInt32 == packet.composite);
            if (composite != null)
            {
                ShortGuid entityId = new ShortGuid(packet.entity);
                switch (packet.entity_variant)
                {
                    case EntityVariant.FUNCTION:
                        composite.RemoveFunction(entityId);
                        break;
                    case EntityVariant.ALIAS:
                        composite.RemoveAlias(entityId);
                        break;
                    case EntityVariant.VARIABLE:
                        composite.RemoveVariable(entityId);
                        break;
                    case EntityVariant.PROXY:
                        composite.RemoveProxy(entityId);
                        break;
                }
            }

            ClearEphemeralDeepSelectAliasTrackingIfMatch(packet.composite, packet.entity);
            _removedEntity = new Tuple<ShortGuid, ShortGuid>(new ShortGuid(packet.composite), new ShortGuid(packet.entity));
        }
    }

    private void TryFillEntityMetadata(Packet packet)
    {
        if (_scene?.Content?.Level == null || packet.entity == 0)
            return;

        Composite composite = _scene.Content.Level.Commands.GetComposite(new ShortGuid(packet.composite));
        Entity entity = composite?.GetEntityByID(new ShortGuid(packet.entity));
        if (entity == null)
            return;

        packet.entity_variant = entity.variant;
        if (entity.variant == EntityVariant.FUNCTION)
            packet.entity_function = ((FunctionEntity)entity).function.AsUInt32;
    }

    // -------------------------------------------------------------------------
    //  Transform gizmo outbound sync
    // -------------------------------------------------------------------------

    private void OnGizmoDragCommitted(Node3D target)
    {
        if (target != null && GodotObject.IsInstanceValid(target))
            LevelViewerPick.InvalidatePickBounds(target);

        ViewerLog.Print("[DragDiag] commit | entity=" + (target?.Name ?? "?")
            + " | pos=" + (target?.Position.ToString() ?? "?"));
    }

    private void OnGizmoTransformChanged(Vector3 godotPos, Vector3 godotRotDeg)
    {
        List<uint> pathEntities;
        List<uint> pathComposites;
        bool entitySelected;
        lock (_lock)
        {
            pathEntities   = _pathEntities;
            pathComposites = _pathComposites;
            entitySelected = _entitySelected;
        }

        if (!entitySelected || pathEntities == null || pathEntities.Count == 0 || pathComposites == null || pathComposites.Count == 0)
            return;

        ShortGuid compositeId = new ShortGuid(pathComposites[pathComposites.Count - 1]);
        ShortGuid entityId = new ShortGuid(pathEntities[pathEntities.Count - 1]);

        if (_scene != null)
        {
            Composite composite = _scene.Content.Level.Commands.GetComposite(compositeId);
            Entity entity = composite?.GetEntityByID(entityId);
            if (entity?.variant == EntityVariant.ALIAS)
            {
                AliasEntity alias = (AliasEntity)entity;
                SyncedParameter sync = BuildPositionSync(godotPos, godotRotDeg);
                bool positionAdded = EnsureAliasPositionParameter(alias, sync);

                EntityOverride aliasOverride = _scene.TryResolveAliasOverrideNode(pathEntities);
                _scene.ApplyEntityParameter(
                    compositeId,
                    entityId,
                    sync,
                    compositeId,
                    entityId,
                    fromPointer: true,
                    pointedOverride: true,
                    aliasOverride);

                if (positionAdded)
                {
                    CommitEphemeralDeepSelectAlias(compositeId.AsUInt32, entityId.AsUInt32);
                    _scene.RefreshEntityHighlights();
                }
            }
            else
            {
                _scene.ApplyGizmoTransformToAllInstances(compositeId, entityId, godotPos, godotRotDeg);
            }
        }

        SendEntityTransform(godotPos, godotRotDeg, pathEntities, pathComposites);
    }

    /// <summary>
    /// Send a position parameter update packet to OpenCAGE for the currently selected entity.
    /// <paramref name="godotPos"/> and <paramref name="godotRotDeg"/> are in Godot parent-local space
    /// (matches the entity position parameter, not GlobalPosition).
    /// </summary>
    public void SendEntityTransform(Vector3 godotPos, Vector3 godotRotDeg,
        List<uint> pathEntities, List<uint> pathComposites)
    {
        if (pathComposites == null || pathComposites.Count == 0 ||
            pathEntities   == null || pathEntities.Count   == 0)
            return;

        // Convert Godot → Cathode space
        Vector3 cathodePos = CathodeCoordinates.PositionFromGodot(godotPos);
        Vector3 cathodeRot = CathodeCoordinates.EulerDegreesFromGodot(godotRotDeg);

        uint positionName = ShortGuidUtils.Generate("position").AsUInt32;

        SyncedParameter sync = new SyncedParameter()
        {
            name      = positionName,
            removed   = false,
            data_type = (uint)DataType.TRANSFORM,
            vector3_a = new float[] { cathodePos.X, cathodePos.Y, cathodePos.Z },
            vector3_b = new float[] { cathodeRot.X, cathodeRot.Y, cathodeRot.Z },
        };

        Packet packet = new Packet(PacketEvent.ENTITY_PARAMETER_MODIFIED)
        {
            path_entities   = pathEntities,
            path_composites = pathComposites,
            composite       = pathComposites[pathComposites.Count - 1],
            entity          = pathEntities[pathEntities.Count - 1],
            parameters      = new System.Collections.Generic.List<SyncedParameter>() { sync },
        };

        TryFillEntityMetadata(packet);

        _suppressParameterResync = true;
        try
        {
            SendMessage(packet);
        }
        finally
        {
            // Re-enable after a short delay so echo packet is already suppressed
            Callable.From(() => { _suppressParameterResync = false; }).CallDeferred();
        }
    }

    private bool ProgressiveDeepSelectTargetsMatch(
        LevelViewerPick.SelectionTarget target,
        uint activeCompositeId,
        uint[] instancePath)
    {
        if (target.EntityIds == null
            || _progressiveDeepSelectEntityIds == null
            || target.CompositeIds == null
            || _progressiveDeepSelectCompositeIds == null
            || activeCompositeId != _progressiveDeepSelectActiveComposite
            || !PreviewVisibilitySettings.InstancePathsEqual(instancePath, _progressiveDeepSelectInstancePath))
        {
            return false;
        }

        if (_progressiveDeepSelectEntityIds.Count != target.EntityIds.Count
            || _progressiveDeepSelectCompositeIds.Count != target.CompositeIds.Count)
        {
            return false;
        }

        for (int i = 0; i < target.EntityIds.Count; i++)
        {
            if (_progressiveDeepSelectEntityIds[i] != target.EntityIds[i])
                return false;
        }

        for (int i = 0; i < target.CompositeIds.Count; i++)
        {
            if (_progressiveDeepSelectCompositeIds[i] != target.CompositeIds[i])
                return false;
        }

        return true;
    }

    private LevelViewerPick.SelectionTarget ResolveProgressiveDeepSelectDrillTarget(
        LevelViewerPick.SelectionTarget pickedTarget,
        uint activeCompositeId,
        out int drillDepth)
    {
        drillDepth = 1;
        uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
        Commands commands = _scene?.Content?.Level?.Commands;

        if (_progressiveDeepSelectDepth <= 0
            || activeCompositeId != _progressiveDeepSelectActiveComposite
            || !PreviewVisibilitySettings.InstancePathsEqual(instancePath, _progressiveDeepSelectInstancePath))
        {
            return pickedTarget;
        }

        uint pickedLeafId = pickedTarget.LeafEntityId;
        bool sameGeometryPick = ProgressiveDeepSelectTargetsMatch(pickedTarget, activeCompositeId, instancePath);
        bool pickingCurrentSelection = false;
        lock (_lock)
        {
            pickingCurrentSelection = _entitySelected
                && pickedLeafId != 0
                && pickedLeafId == _currentEntity;
        }

        if (!sameGeometryPick && !pickingCurrentSelection)
            return pickedTarget;

        drillDepth = _progressiveDeepSelectDepth;

        if (_progressiveDeepSelectEntityIds == null
            || _progressiveDeepSelectCompositeIds == null
            || _progressiveDeepSelectEntityIds.Count == 0)
        {
            return pickedTarget;
        }

        int pickedMaxDepth = LevelViewerPick.GetDeepSelectMaxDepth(pickedTarget, activeCompositeId, instancePath, commands);
        if (pickedMaxDepth >= drillDepth)
            return pickedTarget;

        return new LevelViewerPick.SelectionTarget(
            _progressiveDeepSelectEntityIds,
            _progressiveDeepSelectCompositeIds,
            _progressiveDeepSelectLeafId);
    }

    private int ResolveProgressiveDeepSelectDepth(LevelViewerPick.SelectionTarget target, uint activeCompositeId)
    {
        uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
        Commands commands = _scene?.Content?.Level?.Commands;
        uint leafId = target.LeafEntityId;
        int maxDepth = LevelViewerPick.GetDeepSelectMaxDepth(target, activeCompositeId, instancePath, commands);
        if (maxDepth <= 0)
        {
            ResetProgressiveDeepSelectState();
            return 0;
        }

        bool samePick = ProgressiveDeepSelectTargetsMatch(target, activeCompositeId, instancePath);

        int depth = samePick ? _progressiveDeepSelectDepth + 1 : 0;
        depth = Math.Min(depth, maxDepth);

        _progressiveDeepSelectLeafId = leafId;
        _progressiveDeepSelectDepth = depth;
        _progressiveDeepSelectActiveComposite = activeCompositeId;
        _progressiveDeepSelectInstancePath = (uint[])instancePath.Clone();
        _progressiveDeepSelectEntityIds = target.EntityIds != null
            ? new List<uint>(target.EntityIds)
            : null;
        _progressiveDeepSelectCompositeIds = target.CompositeIds != null
            ? new List<uint>(target.CompositeIds)
            : null;

        return depth;
    }

    private void ResetProgressiveDeepSelectState()
    {
        _progressiveDeepSelectLeafId = 0;
        _progressiveDeepSelectDepth = 0;
        _progressiveDeepSelectActiveComposite = 0;
        _progressiveDeepSelectInstancePath = Array.Empty<uint>();
        _progressiveDeepSelectEntityIds = null;
        _progressiveDeepSelectCompositeIds = null;
    }

    private bool TryPickDeepSelectViaAlias(
        LevelViewerPick.SelectionTarget target,
        uint ownerCompositeId,
        Commands commands,
        int deepSelectDepth,
        out List<uint> pathEntities,
        out List<uint> pathComposites,
        out bool createdNewAlias)
    {
        pathEntities = null;
        pathComposites = null;
        createdNewAlias = false;

        if (commands == null || ownerCompositeId == 0)
            return false;

        Composite ownerComposite = commands.GetComposite(new ShortGuid(ownerCompositeId));
        if (ownerComposite == null)
            return false;

        uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
        if (LevelViewerPick.GetDeepSelectMaxDepth(target, ownerCompositeId, instancePath, commands) <= 0)
            return false;

        bool builtHierarchy = deepSelectDepth > 0
            ? LevelViewerPick.TryBuildDeepSelectAliasHierarchyPath(
                target,
                ownerCompositeId,
                instancePath,
                deepSelectDepth,
                out ShortGuid[] hierarchy,
                commands)
            : LevelViewerPick.TryBuildAliasHierarchyPath(target, ownerCompositeId, instancePath, out hierarchy, commands);
        if (!builtHierarchy)
            return false;

        if (!LevelViewerPick.TryFindAliasWithPath(ownerComposite, hierarchy, out AliasEntity alias))
        {
            ShortGuid aliasId = ShortGuidUtils.GenerateRandom();
            alias = new AliasEntity(aliasId, hierarchy);
            ownerComposite.AddAlias(alias);
            _scene.AddEntity(ownerComposite.shortGUID, alias.shortGUID);
            createdNewAlias = true;
        }

        if (!LevelViewerPick.TryBuildAliasSelectionPath(
                target,
                ownerCompositeId,
                alias.shortGUID.AsUInt32,
                GetSyncedPathCompositesSnapshot(),
                out pathEntities,
                out pathComposites))
            return false;

        return true;
    }

    private List<uint> GetSyncedPathCompositesSnapshot()
    {
        lock (_lock)
        {
            return _pathComposites != null ? new List<uint>(_pathComposites) : null;
        }
    }

    private void UpdateEphemeralDeepSelectAliasTracking(
        List<uint> pathEntities,
        List<uint> pathComposites,
        bool entitySelected)
    {
        if (PreviewVisibilitySettings.DeepSelectMode == PreviewVisibilitySettings.DeepSelectModeKind.None)
            return;

        if (!entitySelected
            || pathEntities == null
            || pathEntities.Count == 0
            || pathComposites == null
            || pathComposites.Count == 0
            || _scene?.Content?.Level == null)
        {
            ClearEphemeralDeepSelectAliasTracking();
            return;
        }

        uint compositeId = pathComposites[pathComposites.Count - 1];
        uint entityId = pathEntities[pathEntities.Count - 1];
        Composite composite = _scene.Content.Level.Commands.GetComposite(new ShortGuid(compositeId));
        Entity entity = composite?.GetEntityByID(new ShortGuid(entityId));
        if (entity is AliasEntity alias && IsAliasParameterFree(alias))
            TrackEphemeralDeepSelectAlias(compositeId, entityId);
        else
            ClearEphemeralDeepSelectAliasTracking();
    }

    private static bool IsAliasParameterFree(AliasEntity alias)
    {
        return alias != null && (alias.parameters == null || alias.parameters.Count == 0);
    }

    private void TrackEphemeralDeepSelectAlias(uint compositeId, uint aliasEntityId)
    {
        _ephemeralDeepSelectAliasCompositeId = compositeId;
        _ephemeralDeepSelectAliasEntityId = aliasEntityId;
    }

    private void ClearEphemeralDeepSelectAliasTracking()
    {
        _ephemeralDeepSelectAliasCompositeId = 0;
        _ephemeralDeepSelectAliasEntityId = 0;
    }

    private void ClearEphemeralDeepSelectAliasTrackingIfMatch(uint compositeId, uint entityId)
    {
        if (entityId != 0
            && entityId == _ephemeralDeepSelectAliasEntityId
            && compositeId == _ephemeralDeepSelectAliasCompositeId)
        {
            ClearEphemeralDeepSelectAliasTracking();
        }
    }

    private void CommitEphemeralDeepSelectAlias(uint compositeId, uint aliasEntityId)
    {
        if (aliasEntityId != 0
            && aliasEntityId == _ephemeralDeepSelectAliasEntityId
            && compositeId == _ephemeralDeepSelectAliasCompositeId)
        {
            ClearEphemeralDeepSelectAliasTracking();
        }
    }

    private void TryCommitEphemeralDeepSelectAliasAfterParameterSync(uint compositeId, uint entityId)
    {
        if (entityId == 0 || entityId != _ephemeralDeepSelectAliasEntityId || compositeId != _ephemeralDeepSelectAliasCompositeId)
            return;

        if (_scene?.Content?.Level == null)
            return;

        Composite composite = _scene.Content.Level.Commands.GetComposite(new ShortGuid(compositeId));
        Entity entity = composite != null
            ? GetEntity(composite, new ShortGuid(entityId), EntityVariant.ALIAS)
            : null;
        if (entity is AliasEntity alias && !IsAliasParameterFree(alias))
            CommitEphemeralDeepSelectAlias(compositeId, entityId);
    }

    private void TryReleaseEphemeralDeepSelectAliasIfAbandoned(uint newSelectedEntityId, uint newSelectedCompositeId)
    {
        if (_ephemeralDeepSelectAliasEntityId == 0)
            return;

        if (newSelectedEntityId == _ephemeralDeepSelectAliasEntityId
            && (newSelectedCompositeId == 0 || newSelectedCompositeId == _ephemeralDeepSelectAliasCompositeId))
        {
            return;
        }

        TryReleaseEphemeralDeepSelectAlias();
    }

    private void TryReleaseEphemeralDeepSelectAlias()
    {
        if (_ephemeralDeepSelectAliasEntityId == 0 || _scene?.Content?.Level == null)
            return;

        uint compositeId = _ephemeralDeepSelectAliasCompositeId;
        uint entityId = _ephemeralDeepSelectAliasEntityId;
        Composite composite = _scene.Content.Level.Commands.GetComposite(new ShortGuid(compositeId));
        AliasEntity alias = composite != null
            ? GetEntity(composite, new ShortGuid(entityId), EntityVariant.ALIAS) as AliasEntity
            : null;

        if (alias == null || !IsAliasParameterFree(alias))
        {
            ClearEphemeralDeepSelectAliasTracking();
            return;
        }

        /* Not removed here. Whether the alias was used is OpenCAGE's to say - it may have been edited
         * there, or been given a flowgraph node, neither of which this side can see - so it is offered
         * back (ENTITY_ALIAS_RELEASED) and stays put until OpenCAGE's ENTITY_DELETED takes it away. */
        _pendingEphemeralDeepSelectReleaseCompositeId = compositeId;
        _pendingEphemeralDeepSelectReleaseEntityId = entityId;
        ClearEphemeralDeepSelectAliasTracking();
    }

    private static bool EnsureAliasPositionParameter(AliasEntity alias, SyncedParameter sync)
    {
        if (alias == null || alias.GetParameter("position") != null || sync == null)
            return false;

        Vector3 cathodePos = new Vector3(sync.vector3_a[0], sync.vector3_a[1], sync.vector3_a[2]);
        Vector3 cathodeRot = new Vector3(sync.vector3_b[0], sync.vector3_b[1], sync.vector3_b[2]);
        alias.AddParameter("position", new cTransform(cathodePos, cathodeRot));
        return true;
    }

    private void SendAliasEntityAdded(
        Composite ownerComposite,
        AliasEntity alias,
        List<uint> pathEntities,
        List<uint> pathComposites)
    {
        Packet packet = BuildAliasEntityAddedPacket(ownerComposite, alias, pathEntities, pathComposites);
        _viewerOriginatedEntityAdds.Add(alias.shortGUID.AsUInt32);
        SendMessage(packet);
    }

    private static Packet BuildAliasEntityAddedPacket(
        Composite ownerComposite,
        AliasEntity alias,
        List<uint> pathEntities,
        List<uint> pathComposites)
    {
        return new Packet(PacketEvent.ENTITY_ADDED)
        {
            composite = ownerComposite.shortGUID.AsUInt32,
            entity = alias.shortGUID.AsUInt32,
            entity_variant = EntityVariant.ALIAS,
            entity_pointed = alias.alias.pathUint,
            path_entities = pathEntities ?? new List<uint>(),
            path_composites = pathComposites ?? new List<uint>(),
        };
    }

    private static SyncedParameter BuildPositionSync(Vector3 godotPos, Vector3 godotRotDeg)
    {
        Vector3 cathodePos = CathodeCoordinates.PositionFromGodot(godotPos);
        Vector3 cathodeRot = CathodeCoordinates.EulerDegreesFromGodot(godotRotDeg);
        return new SyncedParameter()
        {
            name = ShortGuidUtils.Generate("position").AsUInt32,
            removed = false,
            data_type = (uint)DataType.TRANSFORM,
            vector3_a = new float[] { cathodePos.X, cathodePos.Y, cathodePos.Z },
            vector3_b = new float[] { cathodeRot.X, cathodeRot.Y, cathodeRot.Z },
        };
    }

    private void ApplyEntityAddedParameters(Entity entity, Packet packet)
    {
        if (entity == null || packet?.parameters == null || packet.parameters.Count == 0 || _scene?.Content == null)
            return;

        foreach (SyncedParameter sync in packet.parameters)
            ParameterSync.ApplyToEntity(entity, sync, _scene.Content);
    }

    private static bool IsEmbeddedInOpenCage =>
        OS.GetEnvironment("OPENCAGE_EMBEDDED") == "1";

    private void ScheduleEmbeddedInputFocusRestore()
    {
        if (!IsEmbeddedInOpenCage)
            return;

        SceneTree tree = GetTree();
        if (tree == null)
            return;

        Callable.From(RestoreEmbeddedInputFocus).CallDeferred();
        SceneTreeTimer timer = tree.CreateTimer(0.05);
        timer.Timeout += () => RestoreEmbeddedInputFocus();
    }

    private void RestoreEmbeddedInputFocus()
    {
        // Deferred/timer callback: the node may have left the tree in the meantime.
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
            return;

        Window window = GetViewport()?.GetWindow();
        if (window == null)
            return;

        IntPtr hwnd = (IntPtr)DisplayServer.WindowGetNativeHandle(
            DisplayServer.HandleType.WindowHandle,
            window.GetWindowId());
        if (hwnd == IntPtr.Zero)
            return;

        if (IsAnyMouseButtonPressed() && GetCapture() != hwnd)
            return;

        if (!LevelViewerEmbeddedFocus.IsMouseOverMainWindow())
            return;

        if (GetFocus() != hwnd)
            SetFocus(hwnd);
    }

    private static bool IsAnyMouseButtonPressed()
    {
        const int VK_LBUTTON = 0x01;
        const int VK_RBUTTON = 0x02;
        const int VK_MBUTTON = 0x04;
        return IsKeyDown(VK_LBUTTON) || IsKeyDown(VK_RBUTTON) || IsKeyDown(VK_MBUTTON);
    }

    private static int ResolveWebSocketPort()
    {
        string envPort = OS.GetEnvironment("OPENCAGE_WS_PORT");
        if (int.TryParse(envPort, out int parsedEnvPort) && parsedEnvPort > 0)
            return parsedEnvPort;

        string[] args = OS.GetCmdlineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--opencage-ws-port=", StringComparison.Ordinal))
            {
                if (int.TryParse(arg.Substring("--opencage-ws-port=".Length), out int parsedArgPort) && parsedArgPort > 0)
                    return parsedArgPort;
            }
            else if (arg == "--opencage-ws-port" && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int nextArgPort) && nextArgPort > 0)
            {
                return nextArgPort;
            }
        }

        return DefaultWebSocketPort;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetCapture();

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
