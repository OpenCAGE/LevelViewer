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
    private bool _focusSelected = false;
    private bool _hideNestedScriptEntities = false;
    private bool _renderFiltersDirty = false;
    private bool _nestedVisibilityDirty = false;
    private uint _nestedVisibilityPreviousCompositeId = 0;
    private int _renderFiltersGeneration = 0;
    private HashSet<uint> _renderFiltersChangedFunctionTypes = null;

    public override void _Ready()
    {
        _scene = GetNode<AlienScene>("../AlienScene");
        _connectionCts = new CancellationTokenSource();
        _ = ReconnectLoopAsync(_connectionCts.Token);
    }

    public override void _ExitTree()
    {
        _connectionCts?.Cancel();
        _connectionCts?.Dispose();
        _connectionCts = null;

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
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent
            && keyEvent.Pressed
            && !keyEvent.Echo
            && keyEvent.Keycode == Key.F1)
        {
            bool wireframe = !ModelReferenceRenderSettings.WireframeEnabled;
            _scene.SetModelReferenceWireframe(wireframe);
            GD.Print("Model reference wireframe: " + (wireframe ? "on" : "off"));
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        while (_incomingMessages.TryDequeue(out string message))
            HandleMessage(message);

        if (_levelName != "" && _didLoadLevel)
        {
            if (_scene.ParentNode != null && GodotObject.IsInstanceValid(_scene.ParentNode))
                _scene.ParentNode.QueueFree();

            _scene.LoadLevel(_levelName, _pathToAI);
            _didLoadLevel = false;
        }

        if (_compositeLoaded && _scene.Content.Loaded)
        {
            if (_scene.CompositeID != _pathComposites[0])
                _scene.PopulateComposite(new ShortGuid(_pathComposites[0]));
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

        if (_currentEntityGOID != _currentEntity)
        {
            GD.Print("Selecting entity: " + _currentEntity);
            _scene.SelectEntity(_pathEntities, _focusSelected);
            _currentEntityGOID = _currentEntity;
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

            _pathComposites = packet.path_composites;
            _pathEntities = packet.path_entities;

            _compositeLoaded = _pathComposites != null && _pathComposites.Count != 0;
            _entitySelected = _compositeLoaded && _pathComposites.Count == _pathEntities.Count;

            _currentComposite = _compositeLoaded ? _pathComposites[_pathComposites.Count - 1] : 0;
            _currentEntity = _entitySelected ? _pathEntities[_pathEntities.Count - 1] : 0;

            _focusSelected = packet.focus_object;
            uint previousActiveCompositeId = PreviewVisibilitySettings.ActiveCompositeId;
            bool hideNestedChanged = ApplyViewerSettings(packet);
            bool activeCompositeChanged = ApplyActiveComposite(packet);

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
                break;
            }
        }
    }

    private void QueueParameterSync(Packet packet)
    {
        if (_scene?.Content?.Level == null)
            return;

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

        bool changed = PreviewVisibilitySettings.ActiveCompositeId != activeCompositeId;
        PreviewVisibilitySettings.ActiveCompositeId = activeCompositeId;
        return changed;
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

        while (!cancellationToken.IsCancellationRequested)
        {
            _client?.Dispose();
            _client = new ClientWebSocket();

            GD.Print("Trying to connect to Commands Editor...");

            try
            {
                await _client.ConnectAsync(new Uri("ws://localhost:1702/commands_editor"), cancellationToken);
                GD.Print("Connected to Commands Editor!");

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
}
