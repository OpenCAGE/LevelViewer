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

public class CommandsEditorConnection : MonoBehaviour
{
    private WebSocket _client;
    private AlienLevelLoader _loader;

    private readonly object _lock = new object();

    private string _levelName = "";

    private string _pathToAI = "";
    public string PathToAI => _pathToAI;

    private uint _composite;
    private List<uint> _path;
    private uint _entity;

    private Vector3 _position;
    private Vector3 _rotation;
    private bool _entityMoved = false;

    List<Tuple<int, int>> _renderable;
    private bool _renderableUpdated = false;

    void Start()
    {
        _loader = GetComponent<AlienLevelLoader>();
        StartCoroutine(ReconnectLoop());
    }

    private void Update()
    {
        if (_levelName != "" && _loader.LevelName != _levelName)
            _loader.LoadLevel(_levelName);

        if (_composite != 0 && _loader.CompositeID != _composite)
            _loader.LoadComposite(new ShortGuid(_composite));

        if (_entity != 0)
        {
            GameObject entity = ResolvePath();
            Selection.activeGameObject = entity;
        }

        if (_entityMoved)
        {
            _entityMoved = false;
        }

        if (_renderableUpdated)
        {

            _renderableUpdated = false;
        }
    }

    private GameObject ResolvePath()
    {
        try
        {
            Transform t = _loader.ParentGameObject.transform;
            for (int i = 0; i < _path.Count + 1; i++)
            {
                t = t.Find(_path.Count == i ? _entity.ToString() : _path[i].ToString());
            }
            return t.gameObject;
        }
        catch
        {
            //This can fail if we're selecting an entity which isn't a function. Should handle it better.
            return null;
        }
    }

    private void OnMessage(object sender, MessageEventArgs e)
    {
        Debug.Log(e.Data);

        Packet packet = JsonConvert.DeserializeObject<Packet>(e.Data);

        if (packet.version != new Packet().version)
        {
            Debug.LogError("Your Commands Editor is utilising a different API version than this Unity client!!\nPlease ensure both are up to date.");
            return;
        }

        //TODO: this isn't working right just yet, so commenting out.
        //if (packet.dirty)
        //{
        //    Debug.LogError("Content has been modified inside the Commands editor without saving before opening Unity. Please save inside the Commands editor and re-play Unity to sync changes.");
        //    return;
        //}

        lock (_lock)
        {
            _levelName = packet.level_name;
            _pathToAI = packet.system_folder;
            _composite = packet.composite;
            _path = packet.path;
            _entity = packet.entity;
        }

        switch (packet.packet_event)
        {
            case PacketEvent.ENTITY_MOVED:
                {
                    _position = new Vector3(packet.position.X, packet.position.Y, packet.position.Z);
                    _rotation = new Vector3(packet.rotation.X, packet.rotation.Y, packet.rotation.Z);
                    _entityMoved = true;
                    break;
                }
            case PacketEvent.ENTITY_RESOURCE_MODIFIED:
                {
                    _renderable = packet.renderable;
                    _renderableUpdated = true;
                    break;
                }
        }
    }

    private void OnClose(object sender, CloseEventArgs e)
    {

    }

    private IEnumerator ReconnectLoop()
    {
        yield return new WaitForEndOfFrame();

        while (true)
        {
            if (_client != null)
            {
                _client.OnMessage -= OnMessage;
                _client.OnOpen -= Client_OnOpen;
                _client.OnClose -= OnClose;
            }

            _client = new WebSocket("ws://localhost:1702/commands_editor");
            _client.OnMessage += OnMessage;
            _client.OnOpen += Client_OnOpen;
            _client.OnClose += OnClose;

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

    private void Client_OnOpen(object sender, EventArgs e)
    {

    }

    public void SendMessage(Packet content)
    {
        _client.Send(JsonConvert.SerializeObject(content));
    }




    //TODO: Keep this in sync with clients
    public enum PacketEvent
    {
        LEVEL_LOADED,

        COMPOSITE_SELECTED,
        COMPOSITE_PATH_STEPPED,
        COMPOSITE_DELETED,

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
        public int version = 3;

        //Setup metadata
        public string level_name = "";
        public string system_folder = "";

        //Selection metadata
        public uint composite = 0;
        public List<uint> path = new List<uint>();
        public uint entity = 0;

        //Transform
        public System.Numerics.Vector3 position = new System.Numerics.Vector3();
        public System.Numerics.Vector3 rotation = new System.Numerics.Vector3();

        //Renderable resource
        public List<Tuple<int, int>> renderable = new List<Tuple<int, int>>(); //Model Index, Material Index

        //Track if things have changed
        public bool dirty = false;
    }
}