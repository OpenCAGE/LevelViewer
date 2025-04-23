using System.Collections.Generic;
using UnityEngine;
using CATHODE;
using System.IO;
using CathodeLib;
using CATHODE.LEGACY;
using static CATHODE.LEGACY.ShadersPAK;
using System.Threading.Tasks;
using CATHODE.Scripting;
using System.Linq;
using CATHODE.Scripting.Internal;
using System;
using UnityEditor;
using System.Collections;
using System.Resources;
using System.Security.Principal;
using UnityEngine.Animations;

public class AlienScene : MonoBehaviour
{
    public Action OnLoaded;

    private string _levelName = "";
    public string LevelName => _levelName;

    private GameObject _parentGameObject = null;
    public GameObject ParentGameObject => _parentGameObject;

    private Composite _loadedComposite = null;
    public uint CompositeID => _loadedComposite == null ? 0 : _loadedComposite.shortGUID.ToUInt32();
    public string CompositeIDString => _loadedComposite == null || _loadedComposite.shortGUID == ShortGuid.Invalid ? "" : _loadedComposite.shortGUID.ToByteString();
    public string CompositeName => _loadedComposite == null ? "" : _loadedComposite.name;

    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private Dictionary<Material, bool> _materialSupport = new Dictionary<Material, bool>();
    private Dictionary<int, GameObjectHolder> _modelGOs = new Dictionary<int, GameObjectHolder>();
    
    private Dictionary<ShortGuid, List<GameObject>> _compositeGameObjects = new Dictionary<ShortGuid, List<GameObject>>();
    private Dictionary<GameObject, Entity> _gameObjectEntities = new Dictionary<GameObject, Entity>();

    private CommandsEditorConnection _client;

    IEnumerator Start()
    {
        _client = GetComponent<CommandsEditorConnection>();

        yield return new WaitForEndOfFrame();

#if UNITY_EDITOR
        try
        {
            SceneView.FocusWindowIfItsOpen(typeof(SceneView));
            EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")).Close();
        }
        catch { }
#endif
    }

    private void ResetLevel()
    {
        if (_parentGameObject != null)
            Destroy(_parentGameObject);

        _materials.Clear();
        _materialSupport.Clear();
        _modelGOs.Clear();

        LevelContent.Reset();
    }

    /* Load the content for a level */
    public void LoadLevel(string level)
    {
        if (level == null || level == "" || _client.PathToAI == "")
            return;

        Debug.Log("Loading level " + level + "...");

        ResetLevel();

        _levelName = level;
        LevelContent.Load(_client.PathToAI, level);
    }
    
    /* Populate the scene with a given Composite */
    public void PopulateComposite(ShortGuid guid)
    {
        if (!LevelContent.Loaded) return;

        _compositeGameObjects.Clear();
        _gameObjectEntities.Clear();

        if (_parentGameObject != null)
            Destroy(_parentGameObject);

        _parentGameObject = new GameObject(_levelName);
        _parentGameObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        _parentGameObject.isStatic = true;

        Composite comp = LevelContent.CommandsPAK.GetComposite(guid);
        Debug.Log("Loading composite " + comp?.name + "...");
        _loadedComposite = comp;
        AddCompositeInstance(comp, _parentGameObject, null);

        OnLoaded?.Invoke();
    }

    private void AddCompositeInstance(Composite composite, GameObject compositeGO, Entity parentEntity)
    {
        if (composite == null) return;

        if (_compositeGameObjects.ContainsKey(composite.shortGUID))
        {
            _compositeGameObjects[composite.shortGUID].Add(compositeGO);
        }
        else
        {
            List<GameObject> compositeGOs = new List<GameObject>();
            compositeGOs.Add(compositeGO);
            _compositeGameObjects.Add(composite.shortGUID, compositeGOs);
        }

        foreach (Entity entity in composite.functions)
            AddEntity(composite, entity, compositeGO);
        foreach (Entity entity in composite.variables)
            AddEntity(composite, entity, compositeGO);
        foreach (Entity entity in composite.aliases)
            AddEntity(composite, entity, compositeGO);
        foreach (Entity entity in composite.proxies)
            AddEntity(composite, entity, compositeGO);
    }

    /* Remove all instances of a given Composite in the scene */
    public void RemoveComposite(ShortGuid composite)
    {
        if (_compositeGameObjects.ContainsKey(composite))
        {
            foreach (GameObject compositeInstance in _compositeGameObjects[composite])
            {
                if (compositeInstance != null)
                {
                    Destroy(compositeInstance);
                    _gameObjectEntities.Remove(compositeInstance);
                }
            }
            _compositeGameObjects.Remove(composite);
        }
    }

    /* Add an Entity to all instances of its contained Composite within the scene */
    public void AddEntity(ShortGuid composite, ShortGuid entity)
    {
        string entityGameObjectName = entity.ToUInt32().ToString();
        if (_compositeGameObjects.ContainsKey(composite))
        {
            foreach (GameObject compositeInstance in _compositeGameObjects[composite])
            {
                if (compositeInstance != null)
                {
                    Composite c = LevelContent.CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID == composite);
                    Entity e = c.GetEntityByID(entity);
                    if (c != null && e != null)
                    {
                        AddEntity(c, e, compositeInstance);
                    }
                }
            }
        }
    }

    private void AddEntity(Composite composite, Entity entity, GameObject parentGO)
    {
        GetEntityTransform(entity, out Vector3 position, out Vector3 rotation);

        GameObject entityGO = new GameObject(entity.shortGUID.ToUInt32().ToString());
        entityGO.transform.parent = parentGO.transform;
        entityGO.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
        entityGO.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
        entityGO.isStatic = true;
        _gameObjectEntities.Add(entityGO, entity);

        switch (entity.variant)
        {
            //Create mapped entity which can override original data
            case EntityVariant.ALIAS:
                {
                    AliasEntity alias = (AliasEntity)entity;
                    GameObject aliasedGO = GetGameObject(EntityPathToGUIDList(alias.alias), parentGO.transform);
                    if (aliasedGO != null)
                    {
                        aliasedGO.tag = "pointed";
                        entityGO.AddComponent<EntityOverride>().PointedEntity = aliasedGO;
                        if (alias.GetParameter("position") != null)
                            aliasedGO.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                    }
                }
                break;
            case EntityVariant.PROXY:
                {
                    ProxyEntity proxy = (ProxyEntity)entity;
                    GameObject proxiedGO = GetGameObject(EntityPathToGUIDList(proxy.proxy), ParentGameObject.transform);
                    if (proxiedGO != null)
                    {
                        proxiedGO.tag = "pointed";
                        entityGO.AddComponent<EntityOverride>().PointedEntity = proxiedGO;
                        if (proxy.GetParameter("position") != null)
                            proxiedGO.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                    }
                }
                break;

            //Create base entity which provides its own data
            case EntityVariant.FUNCTION:
                {
                    FunctionEntity function = (FunctionEntity)entity;
                    if (!CommandsUtils.FunctionTypeExists(function.function))
                    {
                        Composite compositeNext = LevelContent.CommandsPAK.GetComposite(function.function);
                        if (compositeNext != null)
                        {
                            AddCompositeInstance(compositeNext, entityGO, function);
                        }
                    }
                    else
                    {
                        switch ((FunctionType)function.function.ToUInt32())
                        {
                            //Renderables
                            case FunctionType.ModelReference:
                                if (LevelContent.RemappedResources.ContainsKey(function))
                                {
                                    //Using a resource mapping which has changed at runtime
                                    List<Tuple<int, int>> renderableElement = LevelContent.RemappedResources[function];
                                    for (int i = 0; i < renderableElement.Count; i++)
                                    {
                                        CreateRenderable(entityGO, renderableElement[i].Item1, renderableElement[i].Item2);
                                    }
                                }
                                else
                                {
                                    //Using a resource mapping which was written to disk
                                    Parameter resourceParam = function.GetParameter("resource");
                                    if (resourceParam != null && resourceParam.content != null && resourceParam.content.dataType == DataType.RESOURCE)
                                    {
                                        cResource resource = (cResource)resourceParam.content;
                                        ResourceReference renderable = resource.GetResource(ResourceType.RENDERABLE_INSTANCE);
                                        if (renderable != null)
                                        {
                                            for (int i = 0; i < renderable.count; i++)
                                            {
                                                RenderableElements.Element renderableElement = LevelContent.RenderableREDS.Entries[renderable.index + i];
                                                CreateRenderable(entityGO, renderableElement.ModelIndex, renderableElement.MaterialIndex);
                                            }
                                        }
                                    }
                                }
                                break;
                        }
                    }
                }
                break;
        }
    }

    /* Select an Entity GameObject at a specific instance hierarchy */
    public void SelectEntity(List<uint> path)
    {
        GameObject gameObject = GetGameObject(path, ParentGameObject.transform);
        if (gameObject != null)
        {
            EntityOverride o = gameObject.GetComponent<EntityOverride>();
            if (o != null)
            {
                gameObject = o.PointedEntity;
            }
        }
        Selection.activeGameObject = gameObject;

        if (_client.FocusSelected)
            SceneView.FrameLastActiveSceneView();
    }

    private GameObject GetGameObject(List<uint> path, Transform parent)
    {
        try
        {
            Transform t = parent;
            for (int i = 0; i < path.Count; i++)
                t = t.Find(path[i].ToString());
            return t.gameObject;
        }
        catch
        {
            //This can fail if we're selecting an entity which isn't a function.
            //We should populate placeholders for these so we can still show the transforms probably.
        }
        return null;
    }

    private List<uint> EntityPathToGUIDList(EntityPath path)
    {
        List<uint> list = new List<uint>();
        foreach (ShortGuid guid in path.path)
        {
            if (guid == ShortGuid.Invalid) continue;
            list.Add(guid.ToUInt32());
        }
        return list;
    }

    /* Reposition all Entities in the scene with a new local position and rotation */
    public void RepositionEntity(ShortGuid composite, ShortGuid entity, Vector3 position, Quaternion rotation, bool fromPointer, bool pointedPos)
    {
        string entityGameObjectName = entity.ToUInt32().ToString();
        if (_compositeGameObjects.ContainsKey(composite))
        {
            foreach (GameObject compositeInstance in _compositeGameObjects[composite])
            {
                if (compositeInstance != null)
                {
                    Transform compositeInstanceTransform = compositeInstance.transform;
                    for (int i = 0; i < compositeInstanceTransform.childCount; i++)
                    {
                        Transform child = compositeInstanceTransform.GetChild(i);
                        if (child.name == entityGameObjectName)
                        {
                            child.tag = pointedPos ? "pointed" : "Untagged";
                            if (!(child.tag == "pointed" && !fromPointer))
                            {
                                child.localPosition = position;
                                child.localRotation = rotation;
                            }
                        }
                    }
                }
            }
        }
    }

    /* Remove all instances of a given Entity in the scene */
    public void RemoveEntity(ShortGuid composite, ShortGuid entity)
    {
        string entityGameObjectName = entity.ToUInt32().ToString();
        if (_compositeGameObjects.ContainsKey(composite))
        {
            foreach (GameObject compositeInstance in _compositeGameObjects[composite])
            {
                if (compositeInstance != null)
                {
                    Transform compositeInstanceTransform = compositeInstance.transform;
                    for (int i = 0; i < compositeInstanceTransform.childCount; i++)
                    {
                        Transform child = compositeInstanceTransform.GetChild(i);
                        if (child.name == entityGameObjectName)
                        {
                            EntityOverride o = child.GetComponent<EntityOverride>();
                            if (o != null)
                            {
                                //Reset aliased/proxied entity back to its un-overridden transform
                                GetEntityTransform(_gameObjectEntities[o.PointedEntity], out Vector3 position, out Vector3 rotation);
                                o.PointedEntity.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));
                                o.PointedEntity.tag = "Untagged";
                            }
                            Destroy(child.gameObject);
                            _gameObjectEntities.Remove(child.gameObject);
                        }
                    }
                }
            }
            _compositeGameObjects.Remove(composite);
        }
    }

    /* Update the MeshRenderers for all Entities in the scene */
    public void UpdateRenderable(ShortGuid composite, ShortGuid entity, List<Tuple<int, int>> renderables)
    {
        //todo: this should handle overrides
        string entityGameObjectName = entity.ToUInt32().ToString();
        if (_compositeGameObjects.ContainsKey(composite))
        {
            foreach (GameObject compositeInstance in _compositeGameObjects[composite])
            {
                if (compositeInstance != null)
                {
                    Transform compositeInstanceTransform = compositeInstance.transform;
                    for (int i = 0; i < compositeInstanceTransform.childCount; i++)
                    {
                        Transform child = compositeInstanceTransform.GetChild(i);
                        if (child.name == entityGameObjectName)
                        {
                            for (int x = 0; x < child.childCount; x++)
                            {
                                Destroy(child.GetChild(x).gameObject);
                            }
                            for (int x = 0; x < renderables.Count; x++)
                            {
                                CreateRenderable(child.gameObject, renderables[x].Item1, renderables[x].Item2);
                            }
                        }
                    }
                }
            }
        }
    }

    private void CreateRenderable(GameObject parent, int modelIndex, int materialIndex)
    {
        GameObjectHolder holder = GetModel(modelIndex);
        if (holder == null)
        {
            Debug.Log("Attempted to load non-parsed model (" + modelIndex + "). Skipping!");
            return;
        }

        Material material = GetMaterial((materialIndex == -1) ? holder.DefaultMaterial : materialIndex);
        if (!_materialSupport[material])
            return;

        GameObject newModelSpawn = new GameObject();
        newModelSpawn.transform.parent = parent.transform;
        newModelSpawn.transform.localPosition = Vector3.zero;
        newModelSpawn.transform.localRotation = Quaternion.identity;
        newModelSpawn.name = holder.MainMesh.name;
        newModelSpawn.AddComponent<MeshFilter>().sharedMesh = holder.MainMesh;
        newModelSpawn.hideFlags = HideFlags.NotEditable | HideFlags.HideInHierarchy;

        MeshRenderer renderer = newModelSpawn.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        newModelSpawn.SetActive(_materialSupport[material]);
    }

    private bool GetEntityTransform(Entity entity, out Vector3 position, out Vector3 rotation)
    {
        position = Vector3.zero;
        rotation = Vector3.zero;
        if (entity == null) return false;

        Parameter positionParam = entity.GetParameter("position");
        if (positionParam != null && positionParam.content != null)
        {
            switch (positionParam.content.dataType)
            {
                case DataType.TRANSFORM:
                    cTransform transform = (cTransform)positionParam.content;
                    position = transform.position;
                    rotation = transform.rotation;
                    return true;
            }
        }
        return false;
    }

    private GameObjectHolder GetModel(int EntryIndex)
    {
        if (!_modelGOs.ContainsKey(EntryIndex))
        {
            Models.CS2.Component.LOD.Submesh submesh = LevelContent.ModelsPAK.GetAtWriteIndex(EntryIndex);
            if (submesh == null) return null;
            Models.CS2.Component.LOD lod = LevelContent.ModelsPAK.FindModelLODForSubmesh(submesh);
            Models.CS2 mesh = LevelContent.ModelsPAK.FindModelForSubmesh(submesh);
            Mesh thisMesh = submesh.ToMesh();

            GameObjectHolder ThisModelPart = new GameObjectHolder();
            ThisModelPart.Name = ((mesh == null) ? "" : mesh.Name) + ": " + ((lod == null) ? "" : lod.Name);
            ThisModelPart.MainMesh = thisMesh;
            ThisModelPart.DefaultMaterial = submesh.MaterialLibraryIndex;
            _modelGOs.Add(EntryIndex, ThisModelPart);
        }
        return _modelGOs[EntryIndex];
    }

    private Material GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material InMaterial = LevelContent.ModelsMTL.GetAtWriteIndex(MTLIndex);
            int RemappedIndex = LevelContent.ShadersIDXRemap.Datas[InMaterial.ShaderIndex].Index;
            ShadersPAK.ShaderEntry Shader = LevelContent.ShadersPAK.Shaders[RemappedIndex];

            Material toReturn = new Material(UnityEngine.Shader.Find("Standard"));
            toReturn.name = InMaterial.Name;

            ShaderMaterialMetadata metadata = LevelContent.ShadersPAK.GetMaterialMetadataFromShader(InMaterial);

            switch (metadata.shaderCategory)
            {
                //Unsupported shader slot types - draw transparent for now
                case ShaderCategory.CA_SHADOWCASTER:
                case ShaderCategory.CA_DEFERRED:
                case ShaderCategory.CA_DEBUG:
                case ShaderCategory.CA_OCCLUSION_CULLING:
                case ShaderCategory.CA_FOGSPHERE:
                case ShaderCategory.CA_FOGPLANE:
                case ShaderCategory.CA_EFFECT_OVERLAY:
                case ShaderCategory.CA_DECAL:
                case ShaderCategory.CA_VOLUME_LIGHT:
                case ShaderCategory.CA_REFRACTION:
                    toReturn.name += " (NOT RENDERED: " + metadata.shaderCategory.ToString() + ")";
                    _materialSupport.Add(toReturn, false);
                    return toReturn;
            }
            toReturn.name += " " + metadata.shaderCategory.ToString();

            for (int i = 0; i < Shader.Header.CSTCounts.Length; i++)
            {
                using (BinaryReader cstReader = new BinaryReader(new MemoryStream(LevelContent.ModelsMTL.CSTData[i])))
                {
                    int baseOffset = (InMaterial.ConstantBuffers[i].Offset * 4);

                    if (CSTIndexValid(metadata.cstIndexes.Diffuse0, ref Shader, i))
                    {
                        Vector4 colour = LoadFromCST<Vector4>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.Diffuse0] * 4));
                        toReturn.SetColor("_Color", colour);
                    }
                }
            }

            _materialSupport.Add(toReturn, true);
            _materials.Add(MTLIndex, toReturn);
        }
        return _materials[MTLIndex];
    }
    private T LoadFromCST<T>(BinaryReader cstReader, int offset)
    {
        cstReader.BaseStream.Position = offset;
        return Utilities.Consume<T>(cstReader);
    }
    private bool CSTIndexValid(int cstIndex, ref ShadersPAK.ShaderEntry Shader, int i)
    {
        return cstIndex >= 0 && cstIndex < Shader.Header.CSTCounts[i] && (int)Shader.CSTLinks[i][cstIndex] != -1 && Shader.CSTLinks[i][cstIndex] != 255;
    }
}

//Temp wrapper for GameObject while we just want it in memory
public class GameObjectHolder
{
    public string Name;
    public Mesh MainMesh; //TODO: should this be contained in a globally referenced array?
    public int DefaultMaterial; 
}

public static class LevelContent
{
    static LevelContent()
    {

    }

    public static void Load(string aiPath, string levelName)
    {
        string levelPath = aiPath + "/DATA/ENV/PRODUCTION/" + levelName + "/";
        string worldPath = levelPath + "WORLD/";
        string renderablePath = levelPath + "RENDERABLE/";

        //The game has two hard-coded _PATCH overrides. We should use RENDERABLE from the non-patched folder.
        switch (levelName)
        {
            case "DLC/BSPNOSTROMO_RIPLEY_PATCH":
            case "DLC/BSPNOSTROMO_TWOTEAMS_PATCH":
                renderablePath = levelPath.Replace(levelName, levelName.Substring(0, levelName.Length - ("_PATCH").Length)) + "RENDERABLE/";
                break;
        }

        Parallel.For(0, 8, (i) =>
        {
            switch (i)
            {
                case 0:
                    CommandsPAK = new Commands(worldPath + "COMMANDS.PAK");
                    break;
                case 1:
                    RenderableREDS = new RenderableElements(worldPath + "REDS.BIN");
                    break;
                case 2:
                    ResourcesBIN = new CATHODE.Resources(worldPath + "RESOURCES.BIN");
                    break;
                case 3:
                    ModelsCST = File.ReadAllBytes(renderablePath + "LEVEL_MODELS.CST");
                    break;
                case 4:
                    ModelsMTL = new Materials(renderablePath + "LEVEL_MODELS.MTL");
                    break;
                case 5:
                    ModelsPAK = new Models(renderablePath + "LEVEL_MODELS.PAK");
                    break;
                case 6:
                    ShadersPAK = new ShadersPAK(renderablePath + "LEVEL_SHADERS_DX11.PAK");
                    break;
                case 7:
                    ShadersIDXRemap = new IDXRemap(renderablePath + "LEVEL_SHADERS_DX11_IDX_REMAP.PAK");
                    break;
            }
        });
    }

    public static void Reset()
    {
        CommandsPAK = null;
        RenderableREDS = null;
        ResourcesBIN = null;
        ModelsCST = null;
        ModelsMTL = null;
        ModelsPAK = null;
        ShadersPAK = null;
        ShadersIDXRemap = null;
        RemappedResources.Clear();
    }

    public static bool Loaded => CommandsPAK != null && CommandsPAK.Loaded;

    public static Commands CommandsPAK;
    public static RenderableElements RenderableREDS;
    public static CATHODE.Resources ResourcesBIN;
    public static byte[] ModelsCST;
    public static Materials ModelsMTL;
    public static Models ModelsPAK;
    public static ShadersPAK ShadersPAK;
    public static IDXRemap ShadersIDXRemap;

    //This acts as a temporary override for REDS.BIN mapping runtime changes from Commands Editor
    public static Dictionary<Entity, List<Tuple<int, int>>> RemappedResources = new Dictionary<Entity, List<Tuple<int, int>>>(); //Model Index, Material Index
};