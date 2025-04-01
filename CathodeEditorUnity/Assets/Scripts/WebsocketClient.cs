using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;
using WebSocketSharp;
using System.Numerics;
using CATHODE.Scripting;

public class WebsocketClient : MonoBehaviour
{
    private WebSocket client;
    private AlienLevelLoader loader;
    private Mutex mutex = new Mutex();

    private string levelToLoad = "";
    private bool shouldLoad = false;

    private System.Numerics.Vector3 camPosition;
    private bool shouldRepositionCam = false;

    private string compositeName = "";
    private bool shouldLoadComposite = false;

    private int[] redsIndex;
    private bool shouldFocusOnReds = false;

    private string labelToShow = "";

    private string _pathToAI = "";
    public string PathToAI => _pathToAI;

    void Start()
    {
        loader = GetComponent<AlienLevelLoader>();
        StartCoroutine(ReconnectLoop());
    }

    private void Update()
    {
        if (shouldLoad)
        {
            if (loader.LevelName != levelToLoad)
                loader.LoadLevel(levelToLoad);
            shouldLoad = false;
        }
        if (shouldLoadComposite)
        {
            if (loader.CompositeIDString != compositeName)
                loader.LoadComposite(new ShortGuid(compositeName));
            shouldLoadComposite = false;
        }
        if (shouldRepositionCam)
        {
            UnityEngine.Vector3 camPositionUnity = new UnityEngine.Vector3(camPosition.X, camPosition.Y, camPosition.Z);
            //Camera.main.transform.position = camPositionUnity - new UnityEngine.Vector3(0, 1, 0);
            //Camera.main.transform.LookAt(camPositionUnity);
            this.transform.position = camPositionUnity;
#if UNITY_EDITOR
            Selection.activeGameObject = this.gameObject;
#endif
            StartCoroutine(FocusDelayed());
            shouldRepositionCam = false;
        }
        if (shouldFocusOnReds)
        {
            List<UnityEngine.Object> objs = new List<UnityEngine.Object>(redsIndex.Length);
            for (int i = 0; i < redsIndex.Length; i++) objs.Add(GameObject.Find(levelToLoad).transform.GetChild(redsIndex[i]));
#if UNITY_EDITOR
            Selection.objects = objs.ToArray();
#endif
            StartCoroutine(FocusDelayed());
            shouldFocusOnReds = false;
        }
    }
    private IEnumerator FocusDelayed()
    {
        yield return new WaitForEndOfFrame();
        //SceneView.FrameLastActiveSceneView();
        //yield return new WaitForEndOfFrame();
        //SceneView.FrameLastActiveSceneView();
    }

    private void OnDrawGizmos()
    {
#if UNITY_EDITOR
        Handles.Label(this.transform.position, labelToShow);
#endif
    }

    private void OnMessage(object sender, MessageEventArgs e)
    {
        WSPacket packet = JsonConvert.DeserializeObject<WSPacket>(e.Data);
        switch (packet.type)
        {
            case MessageType.SYNC_VERSION:
                {
                    if (packet.version != VERSION)
                    {
                        Debug.LogError("Your Commands Editor is utilising a newer API version than this Unity client!!\nPlease update this Unity client to avoid experiencing errors.");
                    }
                    break;
                }
            case MessageType.LOAD_LEVEL:
                {
                    mutex.WaitOne();
                    levelToLoad = packet.level_name;
                    _pathToAI = packet.alien_path;
                    shouldLoad = true;
                    mutex.ReleaseMutex();
                    break;
                }
            case MessageType.LOAD_COMPOSITE:
                {
                    mutex.WaitOne();
                    compositeName = packet.composite_name;
                    levelToLoad = packet.level_name;
                    shouldLoadComposite = true;
                    shouldLoad = true;
                    mutex.ReleaseMutex();
                    break;
                }
            case MessageType.GO_TO_POSITION:
                {
                    mutex.WaitOne();
                    camPosition = packet.position;
                    shouldRepositionCam = true;
                    mutex.ReleaseMutex();
                    break;
                }
            case MessageType.SHOW_ENTITY_NAME:
                {
                    mutex.WaitOne();
                    labelToShow = packet.entity_name;
                    mutex.ReleaseMutex();
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
            if (client != null)
            {
                client.OnMessage -= OnMessage;
                client.OnOpen -= Client_OnOpen;
                client.OnClose -= OnClose;
            }

            client = new WebSocket("ws://localhost:1702/commands_editor");
            client.OnMessage += OnMessage;
            client.OnOpen += Client_OnOpen;
            client.OnClose += OnClose;

            Debug.Log("Trying to connect to Commands Editor...");

            while (!client.IsAlive)
            {
                try { client.Connect(); } catch { }
                yield return new WaitForSeconds(1.5f);
            }

            Debug.Log("Connected to Commands Editor!");

            while (client != null && client.IsAlive)
                yield return new WaitForEndOfFrame();

            client.Close();

            Debug.LogWarning("Disconnected from Commands Editor!");
        }

    }

    private void Client_OnOpen(object sender, EventArgs e)
    {

    }

    public void SendMessage(WSPacket content)
    {
        client.Send(JsonConvert.SerializeObject(content));
    }

    //TODO: Keep this in sync with clients
    public const int VERSION = 3;
    public enum MessageType
    {
        SYNC_VERSION,

        LOAD_LEVEL,
        LOAD_COMPOSITE,

        GO_TO_POSITION,
        SHOW_ENTITY_NAME,
    }
    public class WSPacket
    {
        public MessageType type;

        public int version;

        public string level_name;
        public string alien_path;

        public System.Numerics.Vector3 position;
        public System.Numerics.Vector3 rotation;

        public string entity_name;

        public string composite_name;
    }
}