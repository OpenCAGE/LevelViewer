using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using WebSocketSharp;
using CATHODE.Scripting;
using System.IO;
using UnityEngine.UIElements;
using System.Linq;
using CATHODE.Scripting.Internal;

[RequireComponent(typeof(AlienScene))]
public class CommandsEditorConnection : MonoBehaviour
{
    private WebSocket _client;
    private AlienScene _scene;

    private readonly object _lock = new object();

    private string _levelName = "";

    private string _pathToAI = "";
    public string PathToAI => _pathToAI;

    private List<uint> _pathComposites;
    private List<uint> _pathEntities;
    private bool _compositeLoaded;
    private bool _entitySelected;
    private uint _currentComposite;
    private uint _currentEntity;

    private uint _currentEntityGOID = 0;

    private Vector3 _position;
    private Vector3 _rotation;
    private Tuple<ShortGuid, ShortGuid> _movedEntity = null;
    private bool _movingPointed = false;
    private bool _pointedPos = false;

    List<Tuple<int, int>> _renderable;
    private Tuple<ShortGuid, ShortGuid> _renderableEntity = null;

    private Tuple<ShortGuid, ShortGuid> _addedEntity = null;
    private Tuple<ShortGuid, ShortGuid> _removedEntity = null;
    private ShortGuid _removedComposite = ShortGuid.Invalid;

    //settings
    public bool FocusSelected => _focusSelected;
    private bool _focusSelected = false;

    void Start()
    {
        _scene = GetComponent<AlienScene>();
        StartCoroutine(ReconnectLoop());
    }

    /* Recieve data from Commands Editor and sync it to our local Commands object */
    private void OnMessage(object sender, MessageEventArgs e)
    {
        //Debug.Log(e.Data);

        Packet packet = JsonConvert.DeserializeObject<Packet>(e.Data);

        if (packet.version != new Packet().version)
        {
            Debug.LogError("Your Commands Editor is utilising a different API version than this Unity client!!\nPlease ensure both are up to date.");
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
        }

        switch (packet.packet_event)
        {
            case PacketEvent.ENTITY_MOVED:
                {
                    lock (_lock)
                    {
                        _position = new Vector3(packet.position.X, packet.position.Y, packet.position.Z);
                        _rotation = new Vector3(packet.rotation.X, packet.rotation.Y, packet.rotation.Z);
                        _movingPointed = false;
                        _pointedPos = false;

                        ShortGuid entityID = new ShortGuid(packet.entity);
                        ShortGuid compositeID = new ShortGuid(packet.composite);
                        Composite composite = LevelContent.CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID == compositeID);
                        if (composite != null)
                        {
                            Entity entity = null;
                            switch (packet.entity_variant)
                            {
                                case EntityVariant.FUNCTION:
                                    entity = composite.functions.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.VARIABLE:
                                    entity = composite.variables.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.ALIAS:
                                    entity = composite.aliases.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.PROXY:
                                    entity = composite.proxies.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                            }
                            if (entity != null)
                            {
                                if (packet.has_transform)
                                {
                                    Parameter position = entity.GetParameter("position");
                                    if (position == null || position?.content?.dataType == DataType.TRANSFORM)
                                        position = entity.AddParameter("position", new cTransform());
                                    cTransform transform = (cTransform)position.content;
                                    transform.position = new Vector3(packet.position.X, packet.position.Y, packet.position.Z);
                                    transform.rotation = new Vector3(packet.rotation.X, packet.rotation.Y, packet.rotation.Z);
                                }
                                else
                                {
                                    entity.RemoveParameter("position");
                                }

                                //If this entity points to another, resolve it to apply the transform correctly in scene
                                switch (entity.variant)
                                {
                                    case EntityVariant.PROXY:
                                        HandlePointedTransform(packet, out entityID, out compositeID, ((ProxyEntity)entity).proxy, LevelContent.CommandsPAK.EntryPoints[0]);
                                        break;
                                    case EntityVariant.ALIAS:
                                        HandlePointedTransform(packet, out entityID, out compositeID, ((AliasEntity)entity).alias, composite);
                                        break;
                                }
                            }
                        }

                        _movedEntity = new Tuple<ShortGuid, ShortGuid>(compositeID, entityID);
                    }
                    break;
                }
            case PacketEvent.ENTITY_RESOURCE_MODIFIED:
                {
                    lock (_lock)
                    {
                        ShortGuid entityID = new ShortGuid(packet.entity);
                        ShortGuid compositeID = new ShortGuid(packet.composite);
                        Composite composite = LevelContent.CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID == compositeID);
                        if (composite != null)
                        {
                            Entity entity = null;
                            switch (packet.entity_variant)
                            {
                                case EntityVariant.FUNCTION:
                                    entity = composite.functions.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.VARIABLE:
                                    entity = composite.variables.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.ALIAS:
                                    entity = composite.aliases.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                                case EntityVariant.PROXY:
                                    entity = composite.proxies.FirstOrDefault(o => o.shortGUID == entityID);
                                    break;
                            }
                            if (entity != null)
                            {
                                LevelContent.RemappedResources.Remove(entity);
                                LevelContent.RemappedResources.Add(entity, packet.renderable);
                            }
                        }

                        _renderable = packet.renderable;
                        _renderableEntity = new Tuple<ShortGuid, ShortGuid>(compositeID, entityID);
                    }
                    break;
                }
            case PacketEvent.ENTITY_ADDED:
                {
                    lock (_lock)
                    {
                        Composite composite = LevelContent.CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID.ToUInt32() == packet.composite);
                        if (composite != null)
                        {
                            switch (packet.entity_variant)
                            {
                                case EntityVariant.FUNCTION:
                                    composite.functions.Add(new FunctionEntity() { shortGUID = new ShortGuid(packet.entity), function = new ShortGuid(packet.entity_function) });
                                    break;
                                case EntityVariant.VARIABLE:
                                    composite.variables.Add(new VariableEntity() { shortGUID = new ShortGuid(packet.entity) });
                                    break;
                                case EntityVariant.ALIAS:
                                    EntityPath alias = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        alias.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    composite.aliases.Add(new AliasEntity() { shortGUID = new ShortGuid(packet.entity), alias = alias });
                                    break;
                                case EntityVariant.PROXY:
                                    EntityPath proxy = new EntityPath() { path = new ShortGuid[packet.entity_pointed.Count] };
                                    for (int i = 0; i < packet.entity_pointed.Count; i++)
                                        proxy.path[i] = new ShortGuid(packet.entity_pointed[i]);
                                    composite.proxies.Add(new ProxyEntity() { shortGUID = new ShortGuid(packet.entity), proxy = proxy });
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
                        Composite composite = LevelContent.CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID.ToUInt32() == packet.composite);
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
                        LevelContent.CommandsPAK.Entries.Add(new Composite() { shortGUID = new ShortGuid(packet.composite) });
                    }
                    break;
                }
            case PacketEvent.COMPOSITE_DELETED:
                {
                    lock (_lock)
                    {
                        LevelContent.CommandsPAK.Entries.RemoveAll(o => o.shortGUID == new ShortGuid(packet.composite));

                        _removedComposite = new ShortGuid(packet.composite);
                    }
                    break;
                }
        }
    }
    private void HandlePointedTransform(Packet packet, out ShortGuid entityID, out ShortGuid compositeID, EntityPath path, Composite startComposite)
    {
        Entity pEnt = path.GetPointedEntity(LevelContent.CommandsPAK, startComposite, out Composite pComp);
        entityID = pEnt != null ? pEnt.shortGUID : ShortGuid.Invalid;
        compositeID = pComp != null ? pComp.shortGUID : ShortGuid.Invalid;
        if (!packet.has_transform)
        {
            _pointedPos = false;
            Parameter p = pEnt.GetParameter("position");
            if (p != null && p?.content?.dataType == DataType.TRANSFORM)
            {
                cTransform pT = (cTransform)p.content;
                _position = pT.position;
                _rotation = pT.rotation;
            }
            else
            {
                _position = Vector3.zero;
                _rotation = Vector3.zero;
            }
        }
        else
        {
            _pointedPos = true;
        }
        _movingPointed = true;
    }

    /* Sync any changes that happened with our Unity scene */
    private void FixedUpdate()
    {
        if (_levelName != "" && _scene.LevelName != _levelName)
        {
            _scene.LoadLevel(_levelName);
        }

        if (_compositeLoaded)
        {
            if (_scene.CompositeID != _pathComposites[0])
                _scene.PopulateComposite(new ShortGuid(_pathComposites[0]));
            //if (_loader.highlighted) <- todo: add highlighting for actual active composite. the modification should apply to ALL instances of the composite too, unless we apply as aliases in the editor... hmm...
        }

        if (_addedEntity != null)
        {
            Debug.Log("Adding entity: " + _addedEntity.Item2.ToUInt32());
            _scene.AddEntity(_addedEntity.Item1, _addedEntity.Item2);
            _addedEntity = null;
        }

        if (_removedEntity != null)
        {
            Debug.Log("Removing entity: " + _removedEntity.Item2.ToUInt32());
            _scene.RemoveEntity(_removedEntity.Item1, _removedEntity.Item2);
            _removedEntity = null;
        }

        if (_removedComposite != ShortGuid.Invalid)
        {
            Debug.Log("Removing composite: " + _removedComposite.ToUInt32());
            _scene.RemoveComposite(_removedComposite);
            _removedComposite = ShortGuid.Invalid;
        }

        if (_renderableEntity != null)
        {
            Debug.Log("Updating renderables for entity: " + _renderableEntity.Item2.ToUInt32() + " [" + _renderable.Count + "]");
            _scene.UpdateRenderable(_renderableEntity.Item1, _renderableEntity.Item2, _renderable);
            _renderableEntity = null;
        }

        if (_movedEntity != null)
        {
            Debug.Log("Updating transform for entity: " + _movedEntity.Item2.ToUInt32() + " [" + _position + ", " + _rotation + "]");
            _scene.RepositionEntity(_movedEntity.Item1, _movedEntity.Item2, _position, Quaternion.Euler(_rotation), _movingPointed, _pointedPos);
            _movedEntity = null;
        }

        if (_currentEntityGOID != _currentEntity)
        {
            Debug.Log("Selecting entity: " + _currentEntity);
            _scene.SelectEntity(_pathEntities);
            _currentEntityGOID = _currentEntity;
        }
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



    #region PACKET
    //TODO: Keep this in sync with clients
    public enum PacketEvent
    {
        LEVEL_LOADED,

        COMPOSITE_SELECTED,
        COMPOSITE_RELOADED,
        COMPOSITE_DELETED,
        COMPOSITE_ADDED,

        ENTITY_SELECTED,
        ENTITY_MOVED,
        ENTITY_DELETED,
        ENTITY_ADDED,
        ENTITY_RESOURCE_MODIFIED,

        GENERIC_DATA_SYNC,
    }

    public class Packet
    {
        public Packet(PacketEvent packet_event = PacketEvent.GENERIC_DATA_SYNC)
        {
            this.packet_event = packet_event;
        }

        //Packet metadata
        public PacketEvent packet_event;
        public int version = 4;

        //Setup metadata
        public string level_name = "";
        public string system_folder = "";

        //Selection metadata
        public List<uint> path_entities = new List<uint>();
        public List<uint> path_composites = new List<uint>();
        public uint entity;
        public uint composite;

        //Transform
        public bool has_transform = false;
        public System.Numerics.Vector3 position = new System.Numerics.Vector3();
        public System.Numerics.Vector3 rotation = new System.Numerics.Vector3();

        //Renderable resource
        public List<Tuple<int, int>> renderable = new List<Tuple<int, int>>(); //Model Index, Material Index

        //Modified entity info
        public EntityVariant entity_variant;
        public uint entity_function; //For function entities
        public List<uint> entity_pointed; //For alias/proxy entities

        //Track if things have changed
        public bool dirty = false;

        //Settings
        public bool focus_object = false;
    }
    #endregion
}