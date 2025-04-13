using CATHODE.LEGACY;
using CATHODE;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Threading.Tasks;
using CathodeLib;
using static CATHODE.LEGACY.ShadersPAK;
using Material = UnityEngine.Material;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using System;
using UnityEditor;
using System.Linq;

public class UnityLevelContent : MonoSingleton<UnityLevelContent>
{
    public string Level => _level;
    private string _level;

    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private Dictionary<Material, bool> _materialSupport = new Dictionary<Material, bool>();
    private Dictionary<int, Mesh> _meshes = new Dictionary<int, Mesh>();

    private Commands CommandsPAK;
    private RenderableElements RenderableREDS;
    private CATHODE.Resources ResourcesBIN;
    private byte[] ModelsCST;
    private Materials ModelsMTL;
    private Models ModelsPAK;
    private ShadersPAK ShadersPAK;
    private IDXRemap ShadersIDXRemap;

    private string _pathToAI = "M:\\Modding\\Steam Projects\\steamapps\\common\\Alien Isolation"; //testing

    private void Start()
    {
        LoadLevel("BSP_TORRENS");
        CreatePlaceholderPrefabs();
        return;

        CreateCompositePrefab(2818868914);

        GameObject.Instantiate(GetCompositePrefab(2818868914));
    }

    /* Loads all assets for a specified level */
    public bool LoadLevel(string levelName)
    {
        _materials.Clear();
        _materialSupport.Clear();
        _meshes.Clear();

        if (_pathToAI == "" || !Directory.Exists(_pathToAI))
        {
            _level = "";
            return false;
        }

        _level = levelName;

        if (_level == "")
            return false;

        string levelPath = _pathToAI + "/DATA/ENV/PRODUCTION/" + levelName + "/";
        string worldPath = levelPath + "WORLD/";
        string renderablePath = levelPath + "RENDERABLE/";

        if (!Directory.Exists(levelPath))
        {
            _level = "";
            return false;
        }

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

        //fix for full path name in root
        string[] cont = CommandsPAK.EntryPoints[0].name.Replace('\\', '/').Split('/');
        CommandsPAK.EntryPoints[0].name = cont[cont.Length - 1];

        return true;
    }

    Dictionary<Composite, string> _compositePrefabs = new Dictionary<Composite, string>();
    private void CreatePlaceholderPrefabs()
    {
        if (Directory.Exists("Assets/Composites"))
            Directory.Delete("Assets/Composites", true);
        
        AssetDatabase.StartAssetEditing();
        foreach (Composite composite in CommandsPAK.Entries)
        {
            string compositeAsset = "Assets/Composites/" + composite.name.Replace(":", "_") + ".prefab";
            _compositePrefabs.Add(composite, compositeAsset);

            Directory.CreateDirectory(compositeAsset.Substring(0, compositeAsset.Length - Path.GetFileName(compositeAsset).Length));

            var go = new GameObject(name);
            PrefabUtility.SaveAsPrefabAsset(go, compositeAsset);
            DestroyImmediate(go);
        }
        AssetDatabase.StopAssetEditing();

        AssetDatabase.StartAssetEditing();
        foreach (Composite composite in CommandsPAK.Entries)
        {
            //string path = _compositePrefabs[composite];
            string path = "Assets/Composites/" + composite.name.Replace(":", "_") + ".prefab";

            // Load and instantiate for editing
            GameObject instance = new GameObject(Path.GetFileName(composite.name));
            instance.AddComponent<UnityComposite>().CreateComposite(composite);

            // Save the updated version
            //PrefabUtility.SaveAsPrefabAsset(instance, path);
            //DestroyImmediate(instance);
        }
        AssetDatabase.StopAssetEditing();
    }



    /* Gets or creates a Prefab of a Composite (NOTE: all nested Prefabs must already be made, else this will fail) */
    public GameObject CreateCompositePrefab(uint compositeID)
    {
        if (CommandsPAK == null || !CommandsPAK.Loaded)
            return null;

        Composite composite = CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID == compositeID);
        if (composite == null)
            return null;

        //Make sure all nested Composites have also had their Prefab created first
        foreach (FunctionEntity function in composite.functions)
        {
            if (!CommandsUtils.FunctionTypeExists(function.function))
            {
                CreateCompositePrefab(function.function.ToUInt32());
            }
        }

        string compositeAsset = "Assets/Composites/" + composite.name.Replace(":", "_") + ".prefab";

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(compositeAsset);
        if (prefab == null)
        {
            string path = compositeAsset.Substring(0, compositeAsset.Length - Path.GetFileName(compositeAsset).Length);
            if (!AssetDatabase.IsValidFolder(path))
                Directory.CreateDirectory(path);

            GameObject compositeGO = new GameObject(compositeID.ToString());
            compositeGO.AddComponent<UnityComposite>().CreateComposite(composite);
            //compositeGO.hideFlags |= HideFlags.NotEditable;
            prefab = PrefabUtility.SaveAsPrefabAsset(compositeGO, compositeAsset);
            Destroy(compositeGO);
        }
        return prefab;
    }

    /* Gets or creates a Prefab of a Composite (NOTE: all nested Prefabs must already be made, else this will fail) */
    public GameObject GetCompositePrefab(uint compositeID)
    {
        if (CommandsPAK == null || !CommandsPAK.Loaded) 
            return null;

        Composite composite = CommandsPAK.Entries.FirstOrDefault(o => o.shortGUID == compositeID);
        if (composite == null)
            return null;

        return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Composites/" + composite.name.Replace(":", "_") + ".prefab");
    }

    /* Creates a Unity GameObject with a Mesh and Material given a ModelPAK and MTL index */
    public void SpawnRenderable(GameObject parent, int modelIndex, int materialIndex)
    {
        Mesh holder = GetMesh(modelIndex);
        if (holder == null)
        {
            Debug.Log("Attempted to load non-parsed model (" + modelIndex + "). Skipping!");
            return;
        }

        Material material = GetMaterial((materialIndex == -1) ? Convert.ToInt32(holder.name) : materialIndex);
        if (!_materialSupport[material])
            return;

        GameObject newModelSpawn = new GameObject();
        newModelSpawn.transform.parent = parent.transform;
        newModelSpawn.transform.localPosition = Vector3.zero;
        newModelSpawn.transform.localRotation = Quaternion.identity;
        newModelSpawn.AddComponent<MeshFilter>().sharedMesh = holder;
        //newModelSpawn.hideFlags |= HideFlags.NotEditable;

        MeshRenderer renderer = newModelSpawn.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        newModelSpawn.SetActive(_materialSupport[material]);
    }

    /* Gets or creates the Unity Mesh for a given ModelPAK index */
    public Mesh GetMesh(int EntryIndex)
    {
        if (!_meshes.ContainsKey(EntryIndex))
        {
            Models.CS2.Component.LOD.Submesh submesh = ModelsPAK.GetAtWriteIndex(EntryIndex);
            if (submesh == null) return null;
            Models.CS2.Component.LOD lod = ModelsPAK.FindModelLODForSubmesh(submesh);
            Models.CS2 mesh = ModelsPAK.FindModelForSubmesh(submesh);
            Mesh thisMesh = submesh.ToMesh();
            thisMesh.name = submesh.MaterialLibraryIndex.ToString();
            _meshes.Add(EntryIndex, thisMesh);
        }
        return _meshes[EntryIndex];
    }

    /* Gets or creates the Unity Material for a given MTL index */
    public Material GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material InMaterial = ModelsMTL.GetAtWriteIndex(MTLIndex);
            int RemappedIndex = ShadersIDXRemap.Datas[InMaterial.ShaderIndex].Index;
            ShadersPAK.ShaderEntry Shader = ShadersPAK.Shaders[RemappedIndex];

            Material toReturn = new Material(UnityEngine.Shader.Find("Standard"));
            toReturn.name = InMaterial.Name;

            ShaderMaterialMetadata metadata = ShadersPAK.GetMaterialMetadataFromShader(InMaterial);

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
                    return toReturn; //todo: maybe remove _materialSupport and just return null?
            }
            toReturn.name += " " + metadata.shaderCategory.ToString();

            for (int i = 0; i < Shader.Header.CSTCounts.Length; i++)
            {
                using (BinaryReader cstReader = new BinaryReader(new MemoryStream(ModelsMTL.CSTData[i])))
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

    /* Sets a Unity Transform based on an Entity's cTransform */
    public void SetLocalEntityTransform(Entity entity, Transform transform)
    {
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        if (entity == null) return;

        Parameter positionParam = entity.GetParameter("position");
        if (positionParam != null && positionParam.content != null)
        {
            switch (positionParam.content.dataType)
            {
                case DataType.TRANSFORM:
                    cTransform cathodeTransform = (cTransform)positionParam.content;
                    transform.localPosition = cathodeTransform.position;
                    transform.localRotation = Quaternion.Euler(cathodeTransform.rotation);
                    break;
            }
        }
    }
}
