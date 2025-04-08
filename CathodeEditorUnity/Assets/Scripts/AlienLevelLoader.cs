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
using UnityEngine.UIElements;
using System;
using Newtonsoft.Json;
using System.Reflection;
using UnityEditor;
using System.Collections;
using static CATHODE.Textures.TEX4;

public class AlienLevelLoader : MonoBehaviour
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

    private LevelContent _levelContent = null;

    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private Dictionary<Material, bool> _materialSupport = new Dictionary<Material, bool>();
    private Dictionary<int, GameObjectHolder> _modelGOs = new Dictionary<int, GameObjectHolder>();

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

        _levelContent = null;
    }

    public void LoadLevel(string level)
    {
        Debug.Log("Loading level " + level + "...");

        ResetLevel();

        _levelName = level;
        _levelContent = new LevelContent(_client.PathToAI, level);
    }
    public void LoadComposite(ShortGuid guid)
    {
        if (_levelContent == null) return;

        if (_parentGameObject != null)
            Destroy(_parentGameObject);
        _parentGameObject = new GameObject(_levelName);
        //_parentGameObject.hideFlags = HideFlags.HideInHierarchy;  <- enable this!!

        Composite comp = _levelContent.CommandsPAK.GetComposite(guid);
        Debug.Log("Loading composite " + comp?.name + "...");
        LoadComposite(comp);

        OnLoaded?.Invoke();
    }

    /* Load Commands data */
    private void LoadComposite(Composite composite)
    {
        _loadedComposite = composite;
        ParseComposite(composite, _parentGameObject, null, Vector3.zero, Quaternion.identity, new List<AliasEntity>());
    }
    void ParseComposite(Composite composite, GameObject parentGO, Entity parentEntity, Vector3 parentPos, Quaternion parentRot, List<AliasEntity> aliases)
    {
        if (composite == null) return;
        GameObject compositeGO = parentEntity == null ? _parentGameObject : new GameObject(parentEntity.shortGUID.ToUInt32().ToString());
        compositeGO.transform.parent = parentGO.transform;
        compositeGO.transform.SetLocalPositionAndRotation(parentPos, parentRot);

        //Compile all appropriate overrides, and keep the hierarchies trimmed so that index zero is accurate to this composite
        List<AliasEntity> trimmedAliases = new List<AliasEntity>();
        for (int i = 0; i < aliases.Count; i++)
        {
            List<ShortGuid> path = aliases[i].alias.path.ToList();
            path.RemoveAt(0);
            if (path.Count != 0)
                trimmedAliases.Add(aliases[i]);
        }
        trimmedAliases.AddRange(composite.aliases);
        aliases = trimmedAliases;

        //Parse all functions in this composite & handle them appropriately
        foreach (FunctionEntity function in composite.functions)
        {
            //Jump through to the next composite
            if (!CommandsUtils.FunctionTypeExists(function.function))
            {
                Composite compositeNext = _levelContent.CommandsPAK.GetComposite(function.function);
                if (compositeNext != null)
                {
                    //Find all overrides that are appropriate to take through to the next composite
                    List<AliasEntity> overridesNext = trimmedAliases.FindAll(o => o.alias.path[0] == function.shortGUID);

                    //Work out our position, accounting for overrides
                    Vector3 position, rotation;
                    AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Length == (o.alias.path[o.alias.path.Length - 1] == ShortGuid.Invalid ? 2 : 1) && o.alias.path[0] == function.shortGUID);
                    if (!GetEntityTransform(ovrride, out position, out rotation))
                        GetEntityTransform(function, out position, out rotation);

                    //Continue
                    ParseComposite(compositeNext, compositeGO, function, position, Quaternion.Euler(rotation), overridesNext);
                }
            }

            //Parse model data
            else if (CommandsUtils.GetFunctionType(function.function) == FunctionType.ModelReference)
            {
                //Work out our position, accounting for overrides
                Vector3 position, rotation;
                AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Length == 1 && o.alias.path[0] == function.shortGUID);
                if (!GetEntityTransform(ovrride, out position, out rotation))
                    GetEntityTransform(function, out position, out rotation);

                GameObject nodeModel = new GameObject(function.shortGUID.ToUInt32().ToString());
                nodeModel.transform.parent = compositeGO.transform;
                nodeModel.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));

                Parameter resourceParam = function.GetParameter("resource");
                if (resourceParam != null && resourceParam.content != null)
                {
                    switch (resourceParam.content.dataType)
                    {
                        case DataType.RESOURCE:
                            cResource resource = (cResource)resourceParam.content;
                            foreach (ResourceReference resourceRef in resource.value)
                            {
                                for (int i = 0; i < resourceRef.count; i++)
                                {
                                    RenderableElements.Element renderable = _levelContent.RenderableREDS.Entries[resourceRef.index + i];
                                    switch (resourceRef.resource_type)
                                    {
                                        case ResourceType.RENDERABLE_INSTANCE:
                                            SpawnRenderable(nodeModel, renderable.ModelIndex, renderable.MaterialIndex);
                                            break;
                                        case ResourceType.COLLISION_MAPPING:
                                            break;
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }
    }

    public void SpawnRenderable(GameObject parent, int modelIndex, int materialIndex)
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

        //Hack: we spawn the resource in a child of the GameObject hidden in the hierarchy, so that it's selectable in editor still
        GameObject newModelSpawnParent = new GameObject();
        if (parent != null) newModelSpawnParent.transform.parent = parent.transform;
        newModelSpawnParent.transform.localPosition = Vector3.zero;
        newModelSpawnParent.transform.localRotation = Quaternion.identity;
        newModelSpawnParent.name = holder.Name;

        GameObject newModelSpawn = new GameObject();
        newModelSpawn.transform.parent = newModelSpawnParent.transform;
        newModelSpawn.transform.localPosition = Vector3.zero;
        newModelSpawn.transform.localRotation = Quaternion.identity;
        newModelSpawn.name = newModelSpawnParent.name;
        newModelSpawn.AddComponent<MeshFilter>().sharedMesh = holder.MainMesh;
        MeshRenderer renderer = newModelSpawn.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        newModelSpawn.SetActive(_materialSupport[material]);

        if (renderer != null)
        {
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }

    bool GetEntityTransform(Entity entity, out Vector3 position, out Vector3 rotation)
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

    #region Asset Handlers
    private GameObjectHolder GetModel(int EntryIndex)
    {
        if (!_modelGOs.ContainsKey(EntryIndex))
        {
            Models.CS2.Component.LOD.Submesh submesh = _levelContent.ModelsPAK.GetAtWriteIndex(EntryIndex);
            if (submesh == null) return null;
            Models.CS2.Component.LOD lod = _levelContent.ModelsPAK.FindModelLODForSubmesh(submesh);
            Models.CS2 mesh = _levelContent.ModelsPAK.FindModelForSubmesh(submesh);
            Mesh thisMesh = submesh.ToMesh();

            GameObjectHolder ThisModelPart = new GameObjectHolder();
            ThisModelPart.Name = ((mesh == null) ? "" : mesh.Name) + ": " + ((lod == null) ? "" : lod.Name);
            ThisModelPart.MainMesh = thisMesh;
            ThisModelPart.DefaultMaterial = submesh.MaterialLibraryIndex;
            _modelGOs.Add(EntryIndex, ThisModelPart);
        }
        return _modelGOs[EntryIndex];
    }

    public Material GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material InMaterial = _levelContent.ModelsMTL.GetAtWriteIndex(MTLIndex);
            int RemappedIndex = _levelContent.ShadersIDXRemap.Datas[InMaterial.ShaderIndex].Index;
            ShadersPAK.ShaderEntry Shader = _levelContent.ShadersPAK.Shaders[RemappedIndex];

            Material toReturn = new Material(UnityEngine.Shader.Find("Standard"));
            toReturn.name = InMaterial.Name;

            ShaderMaterialMetadata metadata = _levelContent.ShadersPAK.GetMaterialMetadataFromShader(InMaterial);

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
                using (BinaryReader cstReader = new BinaryReader(new MemoryStream(_levelContent.ModelsMTL.CSTData[i])))
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
    #endregion
}

//Temp wrapper for GameObject while we just want it in memory
public class GameObjectHolder
{
    public string Name;
    public Mesh MainMesh; //TODO: should this be contained in a globally referenced array?
    public int DefaultMaterial; 
}

public class LevelContent
{
    public LevelContent(string aiPath, string levelName)
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

    public Commands CommandsPAK;
    public RenderableElements RenderableREDS;
    public CATHODE.Resources ResourcesBIN;
    public byte[] ModelsCST;
    public Materials ModelsMTL;
    public Models ModelsPAK;
    public ShadersPAK ShadersPAK;
    public IDXRemap ShadersIDXRemap;
};