using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using OpenCAGE.UnityConnection;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;
using WebSocketSharp;

[RequireComponent(typeof(AlienScene))]
public class CommandsEditorConnection : MonoBehaviour
{
    private WebSocket _client;
    private AlienScene _scene;

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

    private struct ParameterSyncKey : System.IEquatable<ParameterSyncKey>
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

    private Tuple<ShortGuid, ShortGuid> _addedEntity = null;
    private Tuple<ShortGuid, ShortGuid> _removedEntity = null;
    private ShortGuid _removedComposite = ShortGuid.Invalid;

    //settings
    public bool FocusSelected => _focusSelected;
    private bool _focusSelected = false;
    private bool _hideNestedScriptEntities = false;
    private bool _renderFiltersDirty = false;
    private bool _nestedVisibilityDirty = false;
    private uint _nestedVisibilityPreviousCompositeId = 0;
    private int _renderFiltersGeneration = 0;
    private HashSet<uint> _renderFiltersChangedFunctionTypes = null;

    void Start()
    {
        _scene = GetComponent<AlienScene>();
        StartCoroutine(ReconnectLoop());
    }

    /* Recieve data from Commands Editor and sync it to our local Commands object */
    private void OnMessage(object sender, MessageEventArgs e)
    {
        Packet packet = JsonConvert.DeserializeObject<Packet>(e.Data);

        if (packet.version != new Packet().version)
        {
            Debug.LogError("Your Commands Editor is utilising a different API version than this Unity client!!\nPlease ensure both are up to date.");
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

        //if (packet.dirty)
        //{
        //    Debug.LogError("Content has been modified inside the Commands editor without saving before opening Unity. Please save inside the Commands editor and re-play Unity to sync changes.");
        //    return;
        //}

        lock (_lock)
        {
            _levelName = packet.level_name;
            _pathToAI = packet.system_folder;

            _pathComposites = packet.path_composites;
            _pathEntities = packet.path_entities;

            _compositeLoaded = _pathComposites.Count != 0;
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
                        Composite composite = _scene.Content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID.AsUInt32 == packet.composite);
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
                                    EntityPath alias = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        alias.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    composite.AddAlias(new AliasEntity() { shortGUID = new ShortGuid(packet.entity), alias = alias });
                                    break;
                                case EntityVariant.PROXY:
                                    EntityPath proxy = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        proxy.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    composite.AddProxy(new ProxyEntity() { shortGUID = new ShortGuid(packet.entity), proxy = proxy });
                                    break;
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
                        Composite composite = _scene.Content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID.AsUInt32 == packet.composite);
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
                        _scene.Content.Level.Commands.Entries.Add(new Composite() { shortGUID = new ShortGuid(packet.composite) });
                    }
                    break;
                }
            case PacketEvent.COMPOSITE_DELETED:
                {
                    lock (_lock)
                    {
                        _scene.Content.Level.Commands.Entries.RemoveAll(o => o.shortGUID == new ShortGuid(packet.composite));

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

    private void Update()
    {
        FlushPendingParameterSyncs();
    }

    private List<SyncedParameter> BuildLegacySyncedParameters(Packet packet, Entity entity)
    {
        List<SyncedParameter> syncs = new List<SyncedParameter>();
        switch (packet.packet_event)
        {
            case PacketEvent.ENTITY_MOVED:
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
            case PacketEvent.ENTITY_RESOURCE_MODIFIED:
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

    /* Sync any changes that happened with our Unity scene */
    private void FixedUpdate()
    {
        if (_levelName != "" && _didLoadLevel)
        {
            //NEW: Destroy everything and start again, rather than manually handling everything.
            Destroy(_scene.ParentGameObject);
            Destroy(_scene);

            Resources.UnloadUnusedAssets();
			
            _scene = this.gameObject.AddComponent<AlienScene>();
            _scene.LoadLevel(_levelName, _pathToAI);

            _didLoadLevel = false;
        }

        if (_compositeLoaded)
        {
            if (_scene.CompositeID != _pathComposites[0])
                _scene.PopulateComposite(new ShortGuid(_pathComposites[0]));
            //if (_loader.highlighted) <- todo: add highlighting for actual active composite. the modification should apply to ALL instances of the composite too, unless we apply as aliases in the editor... hmm...
        }

        if (_addedEntity != null)
        {
            Debug.Log("Adding entity: " + _addedEntity.Item2.AsUInt32);
            _scene.AddEntity(_addedEntity.Item1, _addedEntity.Item2);
            _addedEntity = null;
        }

        if (_removedEntity != null)
        {
            Debug.Log("Removing entity: " + _removedEntity.Item2.AsUInt32);
            _scene.RemoveEntity(_removedEntity.Item1, _removedEntity.Item2);
            _removedEntity = null;
        }

        if (_removedComposite != ShortGuid.Invalid)
        {
            Debug.Log("Removing composite: " + _removedComposite.AsUInt32);
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
            Debug.Log("Selecting entity: " + _currentEntity);
            _scene.SelectEntity(_pathEntities, _focusSelected);
            _currentEntityGOID = _currentEntity;
        }
    }

    private bool ApplyViewerSettings(Packet packet)
    {
        bool hideNestedChanged = _hideNestedScriptEntities != packet.hide_nested_script_entities;
        _hideNestedScriptEntities = packet.hide_nested_script_entities;
        PreviewVisibilitySettings.HideNestedScriptEntities = _hideNestedScriptEntities;
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

    private IEnumerator ReconnectLoop()
    {
        yield return new WaitForEndOfFrame();

        while (true)
        {
            if (_client != null)
            {
                _client.OnMessage -= OnMessage;
            }

            _client = new WebSocket("ws://localhost:1702/commands_editor");
            _client.OnMessage += OnMessage;

            Debug.Log("Trying to connect to Commands Editor...");

            while (!_client.IsAlive)
            {
                try { _client.Connect(); } catch { }
                yield return new WaitForSeconds(1.5f);
            }

            Debug.Log("Connected to Commands Editor!");

            while (_client != null && _client.IsAlive)
                yield return new WaitForSeconds(0.1f);

            _client.Close();

            Debug.LogWarning("Disconnected from Commands Editor!");
        }
    }

    public void SendMessage(Packet content)
    {
        _client.Send(JsonConvert.SerializeObject(content));
    }
}