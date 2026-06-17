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

	public bool FocusSelected => _focusSelected;
	public bool ShowCameraPosition => _showCameraPosition;
	public bool HasEntitySelection => _entitySelected;
	/// <summary>True when the editor path includes a nested composite (not just the loaded root).</summary>
	public bool HasChildCompositeInPath => _pathComposites != null && _pathComposites.Count > 1;
	private bool _focusSelected = false;
	private bool _showCameraPosition = true;
    private bool _hideNestedScriptEntities = false;
    private bool _renderFiltersDirty = false;
    private bool _nestedVisibilityDirty = false;
    private uint _nestedVisibilityPreviousCompositeId = 0;
    private int _renderFiltersGeneration = 0;
    private HashSet<uint> _renderFiltersChangedFunctionTypes = null;

    private LevelViewerConnectionHud _connectionHud;
    /// <summary>Composite path depth last seen from OpenCAGE packets (detect editor navigating back).</summary>
    private int _syncedCompositePathDepth;

    private LevelViewerTransformGizmo _transformGizmo;
    public LevelViewerTransformGizmo TransformGizmo => _transformGizmo;

    /// <summary>Block resync echo from our own outbound transform packets.</summary>
    private bool _suppressParameterResync;
    private bool _compositeFocusDirty;
    private bool _compositeFocusRefreshScheduled;
    private bool _forceSelectionApply;
    private readonly HashSet<uint> _viewerOriginatedEntityAdds = new HashSet<uint>();
    private uint _progressiveDeepSelectLeafId;
    private int _progressiveDeepSelectDepth;
    private uint _progressiveDeepSelectActiveComposite;
    private uint[] _progressiveDeepSelectInstancePath = System.Array.Empty<uint>();
    private List<uint> _progressiveDeepSelectEntityIds;
    private List<uint> _progressiveDeepSelectCompositeIds;
    private uint _ephemeralDeepSelectAliasCompositeId;
    private uint _ephemeralDeepSelectAliasEntityId;
    private uint _pendingEphemeralDeepSelectDeleteCompositeId;
    private uint _pendingEphemeralDeepSelectDeleteEntityId;

    public bool IsWebSocketConnected => _client != null && _client.State == WebSocketState.Open;

    public override void _Ready()
    {
        _scene = GetNode<AlienScene>("../AlienScene");
        _connectionCts = new CancellationTokenSource();
        ViewerLogBridge.RegisterConnection(this);
        if (_scene != null)
            _scene.OnSelectionChanged += OnSceneSelectionChanged;
        Callable.From(EnsureConnectionHud).CallDeferred();
        Callable.From(EnsureTransformGizmo).CallDeferred();
        _ = ReconnectLoopAsync(_connectionCts.Token);
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

    private void OnSceneSelectionChanged(Node3D selectedNode)
    {
        Callable.From(() => SyncTransformGizmoToSelection()).CallDeferred();
    }

    public void SyncTransformGizmoToSelection(Camera3D camera = null)
    {
        if (_transformGizmo == null || !GodotObject.IsInstanceValid(_transformGizmo))
            EnsureTransformGizmo();

        if (_transformGizmo == null)
            return;

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
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

        if (_connectionHud != null && GodotObject.IsInstanceValid(_connectionHud))
            _connectionHud.QueueFree();
        _connectionHud = null;

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
        FlushPendingParameterSyncs();
        _connectionHud?.UpdateFade((float)delta);
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
    }

    private void PhysicsProcessInternal()
    {
        while (_incomingMessages.TryDequeue(out string message))
            HandleMessage(message);

        if (_levelName != "" && _didLoadLevel)
        {
            string level = _levelName;
            string pathToAi = _pathToAI;
            _didLoadLevel = false;

            if (!ShouldSkipLevelReload(level, pathToAi))
                Callable.From(() => _scene.QueueLoadLevel(level, pathToAi)).CallDeferred();
        }

        if (_compositeLoaded && _scene.Content.Loaded && _pathComposites != null && _pathComposites.Count > 0)
        {
            uint levelRootCompositeId = _scene.Content.Level.Commands.EntryPoints[0].shortGUID.AsUInt32;
            if (levelRootCompositeId != 0 && _scene.CompositeID != levelRootCompositeId)
            {
                Callable.From(() => _scene.QueuePopulateComposite(new ShortGuid(levelRootCompositeId))).CallDeferred();
            }
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
            Callable.From(() =>
            {
                try
                {
                    _scene.RefreshCompositeFocus();
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

        if (packet.packet_event == PacketEvent.RENDER_FILTERS_CHANGED)
        {
            lock (_lock)
            {
                if (packet.box_render_filters != null && RenderFilters.ApplyFromPacket(packet.box_render_filters, out HashSet<uint> changed))
                    MarkRenderFiltersDirty(changed);
            }
            return;
        }

        if (packet.packet_event == PacketEvent.SETTINGS_CHANGED)
        {
            lock (_lock)
            {
                _focusSelected = packet.focus_object;
                _showCameraPosition = packet.show_camera_position;
                ApplyViewerSettings(packet);
                ApplyActiveComposite(packet);
                if (packet.box_render_filters != null)
                    RenderFilters.ApplyFromPacket(packet.box_render_filters);
                MarkRenderFiltersDirty(null);
            }
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

            _focusSelected = packet.focus_object;
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
                MarkCompositeFocusDirty();
                _scene?.ResetCompositeScopedHides();
            }

            if (selectionChanged)
            {
                TryRemoveEphemeralDeepSelectAliasIfAbandoned(_currentEntity, _currentComposite);
                TrySendPendingEphemeralDeepSelectAliasDelete(null, null, false);
                _forceSelectionApply = true;
            }

            bool nestedVisibilityOnly = !hideNestedChanged
                && activeCompositeChanged
                && PreviewVisibilitySettings.HideNestedScriptEntities;

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
                                    composite.AddFunction(new FunctionEntity() { shortGUID = entityId, function = new ShortGuid(packet.entity_function) });
                                    break;
                                case EntityVariant.VARIABLE:
                                    composite.AddVariable(new VariableEntity() { shortGUID = entityId });
                                    break;
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
                                    composite.AddProxy(new ProxyEntity() { shortGUID = entityId, proxy = proxy });
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
                break;
            }
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
                        _didLoadLevel = true;
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
        bool hideNestedChanged = _hideNestedScriptEntities != packet.hide_nested_script_entities;
        _hideNestedScriptEntities = packet.hide_nested_script_entities;
        PreviewVisibilitySettings.HideNestedScriptEntities = _hideNestedScriptEntities;

        bool highlightChanged = PreviewVisibilitySettings.HighlightAliases != packet.highlight_aliases
            || PreviewVisibilitySettings.HighlightProxies != packet.highlight_proxies;
        PreviewVisibilitySettings.HighlightAliases = packet.highlight_aliases;
        PreviewVisibilitySettings.HighlightProxies = packet.highlight_proxies;

        LevelViewerTransformSnap.GridSize = packet.transform_grid_snap > 0f ? packet.transform_grid_snap : 0f;
        LevelViewerTransformSnap.RotationDegrees = packet.rotation_snap_degrees > 0f ? packet.rotation_snap_degrees : 0f;

        if (packet.model_reference_wireframe != ModelReferenceRenderSettings.WireframeEnabled)
            _scene.SetModelReferenceWireframe(packet.model_reference_wireframe);

        if (highlightChanged && _scene != null)
            _scene.RefreshEntityHighlights();

        return hideNestedChanged;
    }

    private bool ApplyActiveComposite(Packet packet)
    {
        uint activeCompositeId = _compositeLoaded && _pathComposites != null && _pathComposites.Count > 0
            ? _currentComposite
            : packet.composite;

        PreviewVisibilitySettings.SyncFromEditorPath(_pathEntities, _pathComposites, _entitySelected);

        bool changed = PreviewVisibilitySettings.ActiveCompositeId != activeCompositeId;
        PreviewVisibilitySettings.ActiveCompositeId = activeCompositeId;
        if (changed)
            MarkCompositeFocusDirty();
        return changed;
    }

    private void MarkCompositeFocusDirty()
    {
        lock (_lock)
        {
            _compositeFocusDirty = true;
        }
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

    private void EnsureConnectionHud()
    {
        if (_connectionHud != null && GodotObject.IsInstanceValid(_connectionHud))
            return;

        Node host = GetTree().CurrentScene ?? this;
        if (host == null || !GodotObject.IsInstanceValid(host))
            return;

        _connectionHud = new LevelViewerConnectionHud();
        _connectionHud.AttachTo(host);
        _connectionHud.ShowWaiting();
    }

    private void NotifyConnectionWaiting()
    {
        Callable.From(() => _connectionHud?.ShowWaiting()).CallDeferred();
    }

    private void NotifyConnectionConnected()
    {
        Callable.From(() => _connectionHud?.ShowConnected()).CallDeferred();
    }

    private void NotifyConnectionDisconnected()
    {
        Callable.From(() => _connectionHud?.ShowDisconnected()).CallDeferred();
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();

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
                NotifyConnectionConnected();

                await ReceiveLoopAsync(_client, cancellationToken);
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
            NotifyConnectionDisconnected();

            try
            {
                await Task.Delay(1500, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            NotifyConnectionWaiting();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket client, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        StringBuilder messageBuilder = new StringBuilder();

        while (client.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                _incomingMessages.Enqueue(messageBuilder.ToString());
                messageBuilder.Clear();
            }
        }
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

    public async void SendMessage(Packet content)
    {
        await SendMessageAsync(content);
    }

    private async Task SendMessageAsync(Packet content)
    {
        if (_client == null || _client.State != WebSocketState.Open)
            return;

        await _sendLock.WaitAsync(_connectionCts.Token);
        try
        {
            if (_client == null || _client.State != WebSocketState.Open)
                return;

            string json = JsonConvert.SerializeObject(content);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            WebSocketPacketLog.LogSent(content, json.Length);
            try
            {
                await _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _connectionCts.Token);
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

        switch (PreviewVisibilitySettings.DeepSelectMode)
        {
            case PreviewVisibilitySettings.DeepSelectModeKind.AdvancedDeepSelect:
                ResetProgressiveDeepSelectState();
                {
                    uint[] instancePath = PreviewVisibilitySettings.ActiveInstanceEntityPath ?? Array.Empty<uint>();
                    if (LevelViewerPick.GetDeepSelectMaxDepth(target, activeCompositeId, instancePath) > 0)
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
                int deepSelectDepth = ResolveProgressiveDeepSelectDepth(target, activeCompositeId);
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

        uint nextSelectedEntity = entitySelected && pathEntities != null && pathEntities.Count > 0
            ? pathEntities[pathEntities.Count - 1]
            : 0;
        uint nextSelectedComposite = pathComposites != null && pathComposites.Count > 0
            ? pathComposites[pathComposites.Count - 1]
            : 0;
        TryRemoveEphemeralDeepSelectAliasIfAbandoned(nextSelectedEntity, nextSelectedComposite);

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        ApplySelectionNow();
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
            {
                TrySendPendingEphemeralDeepSelectAliasDelete(null, null, false);
                _ = SendNewAliasToEditorAsync(ownerComposite, alias, pathEntities, pathComposites, entitySelected);
            }

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

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditorWithPendingEphemeralDelete(pathEntities, pathComposites, entitySelected);
        ApplySelectionNow();
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
                TryRemoveEphemeralDeepSelectAlias();
                TrySendPendingEphemeralDeepSelectAliasDelete(null, null, false);
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
        ApplySelectionNow();
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
        ApplySelectionNow();
    }

    private void ApplyLocalSelection(List<uint> pathEntities, List<uint> pathComposites, bool entitySelected)
    {
        uint newSelectedEntity = entitySelected && pathEntities != null && pathEntities.Count > 0
            ? pathEntities[pathEntities.Count - 1]
            : 0;
        uint newSelectedComposite = pathComposites != null && pathComposites.Count > 0
            ? pathComposites[pathComposites.Count - 1]
            : 0;
        TryRemoveEphemeralDeepSelectAliasIfAbandoned(newSelectedEntity, newSelectedComposite);

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

    private void ApplySelectionNow()
    {
        if (_scene == null || !_scene.Content.Loaded)
            return;

        bool focusSelected;
        bool entitySelected;
        List<uint> pathEntities;
        List<uint> pathComposites;

        lock (_lock)
        {
            if (!_forceSelectionApply && _currentEntityGOID == _currentEntity)
                return;

            focusSelected = _focusSelected;
            entitySelected = _entitySelected;
            pathEntities = _pathEntities;
            pathComposites = _pathComposites;
            _currentEntityGOID = _currentEntity;
            _forceSelectionApply = false;
        }

        _scene.SelectEntity(pathEntities, pathComposites, entitySelected, focusSelected);
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

        if (_pendingEphemeralDeepSelectDeleteEntityId != 0)
        {
            SendPendingEphemeralDeepSelectAliasDelete(
                pathEntities,
                pathComposites,
                canBundleSelection);

            if (canBundleSelection)
                return;
        }

        SendSelectionToEditor(pathEntities, pathComposites, entitySelected);
    }

    private void SendPendingEphemeralDeepSelectAliasDelete(
        List<uint> selectionPathEntities,
        List<uint> selectionPathComposites,
        bool includeSelection)
    {
        if (_pendingEphemeralDeepSelectDeleteEntityId == 0)
            return;

        Packet packet = new Packet(PacketEvent.ENTITY_DELETED)
        {
            composite = _pendingEphemeralDeepSelectDeleteCompositeId,
            entity = _pendingEphemeralDeepSelectDeleteEntityId,
            entity_variant = EntityVariant.ALIAS,
        };

        if (includeSelection
            && selectionPathEntities != null
            && selectionPathComposites != null
            && selectionPathEntities.Count > 0
            && selectionPathEntities.Count == selectionPathComposites.Count)
        {
            packet.path_entities = new List<uint>(selectionPathEntities);
            packet.path_composites = new List<uint>(selectionPathComposites);
        }

        ClearPendingEphemeralDeepSelectDelete();
        SendMessage(packet);
    }

    private bool TrySendPendingEphemeralDeepSelectAliasDelete(
        List<uint> selectionPathEntities,
        List<uint> selectionPathComposites,
        bool includeSelection)
    {
        if (_pendingEphemeralDeepSelectDeleteEntityId == 0)
            return false;

        SendPendingEphemeralDeepSelectAliasDelete(
            selectionPathEntities,
            selectionPathComposites,
            includeSelection);
        return true;
    }

    private void ClearPendingEphemeralDeepSelectDelete()
    {
        _pendingEphemeralDeepSelectDeleteCompositeId = 0;
        _pendingEphemeralDeepSelectDeleteEntityId = 0;
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

        int pickedMaxDepth = LevelViewerPick.GetDeepSelectMaxDepth(pickedTarget, activeCompositeId, instancePath);
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
        uint leafId = target.LeafEntityId;
        int maxDepth = LevelViewerPick.GetDeepSelectMaxDepth(target, activeCompositeId, instancePath);
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
        if (LevelViewerPick.GetDeepSelectMaxDepth(target, ownerCompositeId, instancePath) <= 0)
            return false;

        bool builtHierarchy = deepSelectDepth > 0
            ? LevelViewerPick.TryBuildDeepSelectAliasHierarchyPath(
                target,
                ownerCompositeId,
                instancePath,
                deepSelectDepth,
                out ShortGuid[] hierarchy)
            : LevelViewerPick.TryBuildAliasHierarchyPath(target, ownerCompositeId, instancePath, out hierarchy);
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

    private void TryRemoveEphemeralDeepSelectAliasIfAbandoned(uint newSelectedEntityId, uint newSelectedCompositeId)
    {
        if (_ephemeralDeepSelectAliasEntityId == 0)
            return;

        if (newSelectedEntityId == _ephemeralDeepSelectAliasEntityId
            && (newSelectedCompositeId == 0 || newSelectedCompositeId == _ephemeralDeepSelectAliasCompositeId))
        {
            return;
        }

        TryRemoveEphemeralDeepSelectAlias();
    }

    private void TryRemoveEphemeralDeepSelectAlias()
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

        composite.RemoveAlias(alias);
        _scene.RemoveEntity(new ShortGuid(compositeId), new ShortGuid(entityId));
        _pendingEphemeralDeepSelectDeleteCompositeId = compositeId;
        _pendingEphemeralDeepSelectDeleteEntityId = entityId;
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

        Callable.From(RestoreEmbeddedInputFocus).CallDeferred();
        SceneTreeTimer timer = GetTree().CreateTimer(0.05);
        timer.Timeout += () => RestoreEmbeddedInputFocus();
    }

    private void RestoreEmbeddedInputFocus()
    {
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
