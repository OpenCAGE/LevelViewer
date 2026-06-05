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
    private ClientWebSocket _client;
    private AlienScene _scene;
    private CancellationTokenSource _connectionCts;

    private readonly object _lock = new object();

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
    private bool _forceSelectionApply;

    public override void _Ready()
    {
        _scene = GetNode<AlienScene>("../AlienScene");
        _connectionCts = new CancellationTokenSource();
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
        if (_transformGizmo == null || !GodotObject.IsInstanceValid(_transformGizmo))
            EnsureTransformGizmo();

        if (_transformGizmo == null)
            return;

        if (selectedNode != null && GodotObject.IsInstanceValid(selectedNode))
            _transformGizmo.SetTarget(selectedNode, FindCamera());
        else
            _transformGizmo.ClearTarget();
    }

    public override void _ExitTree()
    {
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
        while (_incomingMessages.TryDequeue(out string message))
            HandleMessage(message);

        if (_levelName != "" && _didLoadLevel)
        {
            string level = _levelName;
            string pathToAi = _pathToAI;
            _didLoadLevel = false;
            Callable.From(() => _scene.QueueLoadLevel(level, pathToAi)).CallDeferred();
        }

        if (_compositeLoaded && _scene.Content.Loaded)
        {
            if (_scene.CompositeID != _pathComposites[0])
            {
                uint compositeId = _pathComposites[0];
                Callable.From(() => _scene.QueuePopulateComposite(new ShortGuid(compositeId))).CallDeferred();
            }
        }

        if (_addedEntity != null)
        {
            GD.Print("Adding entity: " + _addedEntity.Item2.AsUInt32);
            _scene.AddEntity(_addedEntity.Item1, _addedEntity.Item2);
            _addedEntity = null;
        }

        if (_removedEntity != null)
        {
            GD.Print("Removing entity: " + _removedEntity.Item2.AsUInt32);
            _scene.RemoveEntity(_removedEntity.Item1, _removedEntity.Item2);
            _removedEntity = null;
        }

        if (_removedComposite != ShortGuid.Invalid)
        {
            GD.Print("Removing composite: " + _removedComposite.AsUInt32);
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

        if (compositeFocusDirty && _scene != null && _scene.Content.Loaded)
        {
            _scene.RefreshCompositeFocus();
            lock (_lock)
            {
                _compositeFocusDirty = false;
            }
        }
    }

    private void HandleMessage(string data)
    {
        Packet packet = JsonConvert.DeserializeObject<Packet>(data);

        if (packet.version != new Packet().version)
        {
            GD.PrintErr("Your Commands Editor is utilising a different API version than this Godot client!!\nPlease ensure both are up to date.");
            return;
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
            bool compositeNavigationChanged = activeCompositeChanged
                || instancePathChanged
                || incomingCompositeDepth != previousCompositeDepth
                || !PathsEqual(previousPathComposites, packet.path_composites);
            bool selectionChanged = previousEntitySelected != _entitySelected
                || previousEntity != _currentEntity
                || compositeNavigationChanged;

            if (compositeNavigationChanged)
                MarkCompositeFocusDirty();

            if (selectionChanged)
                _forceSelectionApply = true;

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
                        switch (packet.entity_variant)
                        {
                            case EntityVariant.FUNCTION:
                                composite.AddFunction(new FunctionEntity() { shortGUID = new ShortGuid(packet.entity), function = new ShortGuid(packet.entity_function) });
                                break;
                            case EntityVariant.VARIABLE:
                                composite.AddVariable(new VariableEntity() { shortGUID = new ShortGuid(packet.entity) });
                                break;
                            case EntityVariant.ALIAS:
                            {
                                EntityPath alias = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                for (int i = 0; i < packet.entity_pointed.Count; i++)
                                    alias.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                composite.AddAlias(new AliasEntity() { shortGUID = new ShortGuid(packet.entity), alias = alias });
                                break;
                            }
                            case EntityVariant.PROXY:
                            {
                                EntityPath proxy = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                for (int i = 0; i < packet.entity_pointed.Count; i++)
                                    proxy.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                composite.AddProxy(new ProxyEntity() { shortGUID = new ShortGuid(packet.entity), proxy = proxy });
                                break;
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
                        switch (packet.entity_variant)
                        {
                            case EntityVariant.FUNCTION:
                                composite.functions.RemoveAll(o => o.shortGUID == new ShortGuid(packet.entity));
                                break;
                            case EntityVariant.ALIAS:
                                composite.aliases.RemoveAll(o => o.shortGUID == new ShortGuid(packet.entity));
                                break;
                            case EntityVariant.VARIABLE:
                                composite.variables.RemoveAll(o => o.shortGUID == new ShortGuid(packet.entity));
                                break;
                            case EntityVariant.PROXY:
                                composite.proxies.RemoveAll(o => o.shortGUID == new ShortGuid(packet.entity));
                                break;
                        }
                    }

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
                lock (_lock)
                {
                    _didLoadLevel = true;
                }

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
            _scene.ApplyEntityParameter(
                pending.DataCompositeID,
                pending.DataEntityID,
                pending.Sync,
                pending.VisualCompositeID,
                pending.VisualEntityID,
                pending.FromPointer,
                pending.PointedOverride);
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

        if (packet.model_reference_wireframe != ModelReferenceRenderSettings.WireframeEnabled)
            _scene.SetModelReferenceWireframe(packet.model_reference_wireframe);

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

            GD.Print("Trying to connect to Commands Editor...");

            try
            {
                await _client.ConnectAsync(new Uri("ws://localhost:1702/commands_editor"), cancellationToken);
                GD.Print("Connected to Commands Editor!");
                NotifyConnectionConnected();

                await ReceiveLoopAsync(_client, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                GD.PrintErr("WebSocket connection error: " + ex.Message);
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            GD.Print("Disconnected from Commands Editor!");
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

    public async void SendMessage(Packet content)
    {
        if (_client == null || _client.State != WebSocketState.Open)
            return;

        string json = JsonConvert.SerializeObject(content);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            await _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _connectionCts.Token);
        }
        catch (Exception ex)
        {
            GD.PrintErr("Failed to send websocket message: " + ex.Message);
        }
    }

    public void TryPickSelectAtScreen(Camera3D camera, Vector2 screenPosition)
    {
        if (_scene == null || camera == null || !_scene.Content.Loaded)
            return;

        if (!_scene.TryPickSelectionTarget(camera, screenPosition, out LevelViewerPick.SelectionTarget target))
        {
            TryClearEntitySelection();
            return;
        }

        uint activeCompositeId = PreviewVisibilitySettings.ActiveCompositeId;
        if (activeCompositeId == 0 && _pathComposites != null && _pathComposites.Count > 0)
            activeCompositeId = _pathComposites[_pathComposites.Count - 1];

        Commands commands = _scene.Content.Level.Commands;
        bool built;
        List<uint> pathEntities;
        List<uint> pathComposites;
        bool entitySelected;

        if (PreviewVisibilitySettings.DeepSelectMode)
        {
            built = LevelViewerPick.TryBuildDeepSelectEntityPath(
                target,
                commands,
                out pathEntities,
                out pathComposites,
                out entitySelected);
        }
        else
        {
            built = LevelViewerPick.TryBuildActiveCompositeSelectionPath(
                target,
                activeCompositeId,
                out pathEntities,
                out pathComposites);
            entitySelected = true;
        }

        if (!built)
            return;

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditor(pathEntities, pathComposites, entitySelected);
        ApplySelectionNow();
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
        bool built;
        List<uint> pathEntities;
        List<uint> pathComposites;

        if (PreviewVisibilitySettings.DeepSelectMode)
        {
            built = LevelViewerPick.TryBuildDeepDrillPath(
                target,
                commands,
                out pathEntities,
                out pathComposites);
        }
        else
        {
            built = LevelViewerPick.TryBuildCompositeDrillPath(
                target,
                activeCompositeId,
                commands,
                out pathEntities,
                out pathComposites);
        }

        if (!built)
            return;

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected: false);
        SendSelectionToEditor(pathEntities, pathComposites, entitySelected: false);
        ApplySelectionNow();
    }

    public void TryClearEntitySelection()
    {
        if (_scene == null || !_scene.Content.Loaded)
            return;

        List<uint> pathEntities;
        List<uint> pathComposites;
        bool entitySelected;

        lock (_lock)
        {
            if (!_entitySelected || _pathComposites == null || _pathComposites.Count == 0)
                return;

            if (_pathEntities == null || _pathEntities.Count != _pathComposites.Count)
                return;

            pathEntities = new List<uint>(_pathEntities);
            pathEntities.RemoveAt(pathEntities.Count - 1);
            pathComposites = new List<uint>(_pathComposites);
            entitySelected = false;
        }

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditor(pathEntities, pathComposites, entitySelected);
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
            else if (pathEntities.Count > 0 && pathComposites.Count > pathEntities.Count)
            {
                pathEntities.RemoveAt(pathEntities.Count - 1);
                pathComposites.RemoveAt(pathComposites.Count - 1);
                entitySelected = false;
            }
            else
                return;
        }

        ApplyLocalSelection(pathEntities, pathComposites, entitySelected);
        SendSelectionToEditor(pathEntities, pathComposites, entitySelected);
        ApplySelectionNow();
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
            PreviewVisibilitySettings.SyncFromEditorPath(_pathEntities, _pathComposites, _entitySelected);
            PreviewVisibilitySettings.ActiveCompositeId = _currentComposite;

            if (previousActiveComposite != _currentComposite
                || !PreviewVisibilitySettings.InstancePathsEqual(previousInstancePath, PreviewVisibilitySettings.ActiveInstanceEntityPath))
            {
                MarkCompositeFocusDirty();
            }
        }
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

        Packet packet = new Packet(PacketEvent.ENTITY_SELECTED)
        {
            path_entities = pathEntities ?? new List<uint>(),
            path_composites = pathComposites,
            composite = pathComposites[pathComposites.Count - 1],
        };

        if (entitySelected && pathEntities != null && pathEntities.Count > 0)
        {
            packet.entity = pathEntities[pathEntities.Count - 1];
            TryFillEntityMetadata(packet);
        }

        SendMessage(packet);
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

        if (!entitySelected || pathEntities == null || pathEntities.Count == 0)
            return;

        if (_scene != null)
        {
            _scene.ApplyGizmoTransformToAllInstances(
                new ShortGuid(pathComposites[pathComposites.Count - 1]),
                new ShortGuid(pathEntities[pathEntities.Count - 1]),
                godotPos,
                godotRotDeg);
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
}
