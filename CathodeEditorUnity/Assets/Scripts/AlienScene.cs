//#define USE_MATERIAL_DATA

using CATHODE;
using CATHODE.LEGACY;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Security.Principal;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using CATHODE.ShaderTypes;

public class AlienScene : MonoBehaviour
{
    public Action OnLoaded;

    private string _levelName = "";
    public string LevelName => _levelName;

    private GameObject _parentGameObject = null;
    public GameObject ParentGameObject => _parentGameObject;

    private Composite _loadedComposite = null;
    public uint CompositeID => _loadedComposite == null ? 0 : _loadedComposite.shortGUID.AsUInt32;
    public string CompositeIDString => _loadedComposite == null || _loadedComposite.shortGUID == ShortGuid.Invalid ? "" : _loadedComposite.shortGUID.ToByteString();
    public string CompositeName => _loadedComposite == null ? "" : _loadedComposite.name;

    private Dictionary<int, TexOrCube> _texturesGlobal = new Dictionary<int, TexOrCube>();
    private Dictionary<int, TexOrCube> _texturesLevel = new Dictionary<int, TexOrCube>();
    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private Dictionary<Material, bool> _materialSupport = new Dictionary<Material, bool>();
    private Dictionary<int, GameObjectHolder> _modelGOs = new Dictionary<int, GameObjectHolder>();
    
    private Dictionary<ShortGuid, List<GameObject>> _compositeGameObjects = new Dictionary<ShortGuid, List<GameObject>>();
    private Dictionary<GameObject, Entity> _gameObjectEntities = new Dictionary<GameObject, Entity>();

    public class TexOrCube
    {
        public Texture2D Texture = null;
        public Cubemap Cubemap = null;
    }

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
        _texturesLevel.Clear();

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
        string entityGameObjectName = entity.AsUInt32.ToString();
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

        GameObject entityGO = new GameObject(entity.shortGUID.AsUInt32.ToString());
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
                    if (!function.function.IsFunctionType)
                    {
                        Composite compositeNext = LevelContent.CommandsPAK.GetComposite(function.function);
                        if (compositeNext != null)
                        {
                            AddCompositeInstance(compositeNext, entityGO, function);
                        }
                    }
                    else
                    {
                        switch ((FunctionType)function.function.AsUInt32)
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
            list.Add(guid.AsUInt32);
        }
        return list;
    }

    /* Reposition all Entities in the scene with a new local position and rotation */
    public void RepositionEntity(ShortGuid composite, ShortGuid entity, Vector3 position, Quaternion rotation, bool fromPointer, bool pointedPos)
    {
        string entityGameObjectName = entity.AsUInt32.ToString();
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
        string entityGameObjectName = entity.AsUInt32.ToString();
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
        }
    }

    /* Update the MeshRenderers for all Entities in the scene */
    public void UpdateRenderable(ShortGuid composite, ShortGuid entity, List<Tuple<int, int>> renderables)
    {
        //todo: this should handle overrides
        string entityGameObjectName = entity.AsUInt32.ToString();
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
        newModelSpawn.name = holder.MainMesh.name + " (" + material.name + ")";
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
            thisMesh.name = ((mesh == null) ? "" : mesh.Name) + ": " + ((lod == null) ? "" : lod.Name);

            GameObjectHolder ThisModelPart = new GameObjectHolder();
            ThisModelPart.MainMesh = thisMesh;
            ThisModelPart.DefaultMaterial = submesh.MaterialIndex;
            _modelGOs.Add(EntryIndex, ThisModelPart);
        }
        return _modelGOs[EntryIndex];
    }

    private string GetShaderName(SHADER_LIST shaderType)
    {
        switch (shaderType)
        {
            case SHADER_LIST.CA_ENVIRONMENT:
                return "Cathode/CA_Environment";
            default:
                return "Standard";
        }
    }

    private Material GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material material = LevelContent.ModelsMTL.GetAtWriteIndex(MTLIndex);
            Shaders.Shader shader = LevelContent.ShadersPAK.Entries[material.ShaderIndex];

            if (shader.Ubershader != SHADER_LIST.CA_ENVIRONMENT)
            {
                //Debug.Log("Skipping: " + shader.Ubershader.ToString());
                Material mat = new Material(UnityEngine.Shader.Find("Standard"));
                mat.name += " (NOT RENDERED: " + shader.Ubershader.ToString() + ")";
                _materialSupport.Add(mat, false);
                return mat;
            }

            // Get the correct shader based on the shader type
            string shaderName = GetShaderName(shader.Ubershader);
            Shader unityShader = UnityEngine.Shader.Find(shaderName);
            if (unityShader == null)
            {
                Debug.LogWarning($"Shader '{shaderName}' not found, falling back to Standard shader");
                unityShader = UnityEngine.Shader.Find("Standard");
            }
            
            Material unityMaterial = new Material(unityShader);
            unityMaterial.name = material.Name + " " + shader.Ubershader.ToString();

#if USE_MATERIAL_DATA
            switch (shader.Ubershader)
            {
                case SHADER_LIST.CA_ENVIRONMENT:
                    ApplyEnvironmentShader(material, shader, unityMaterial);
                    break;
            }
#endif

            _materialSupport.Add(unityMaterial, true);
            _materials.Add(MTLIndex, unityMaterial);
        }
        return _materials[MTLIndex];
    }

#if USE_MATERIAL_DATA
    private float GetShaderFloat(Shaders.Shader shader, Materials.Material material, int index, float fallback = 0.0f)
    {
        if (shader.PixelShaderParameterRemaps.Count > index)
        {
            if (shader.PixelShaderParameterRemaps[index] != 255)
            {
                return material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index]];
            }
        }
        return fallback;
    }

    private Vector3 GetShaderVector3(Shaders.Shader shader, Materials.Material material, int index, Vector3 fallback)
    {
        if (shader.PixelShaderParameterRemaps.Count > index)
        {
            if (shader.PixelShaderParameterRemaps[index] != 255)
            {
                return new Vector3(
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index]], 
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index] + 1], 
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index] + 2]
                );
            }
        }
        return fallback;
    }

    private Vector4 GetShaderVector4(Shaders.Shader shader, Materials.Material material, int index, Vector4 fallback)
    {
        if (shader.PixelShaderParameterRemaps.Count > index)
        {
            if (shader.PixelShaderParameterRemaps[index] != 255)
            {
                return new Vector4(
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index]], 
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index] + 1], 
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index] + 2],
                    material.PixelShaderConstants[shader.PixelShaderParameterRemaps[index] + 3]
                );
            }
        }
        return fallback;
    }

    private bool ApplySampler(Materials.Material material, Shaders.Shader shader, Material unityMaterial, string textureMap, int index, string keyword = "")
    {
        if (shader.SamplerRemaps.Count <= index) return false;
        int diffuseMapIndex = shader.SamplerRemaps[index]; 
        if (diffuseMapIndex == 255) return false;

        TexOrCube texture = GetTexOrCube(material.TextureReferences[diffuseMapIndex]);
        if (texture?.Texture == null) return false;
        unityMaterial.SetTexture(textureMap, texture.Texture);
        if (keyword != "")
            unityMaterial.EnableKeyword(keyword);

        Shaders.StateBlock state = shader.Samplers.FirstOrDefault(o => o.Index == diffuseMapIndex);
        if (state != null)
        {
            for (int i = 0; i < state.Entries.Count; i++)
            {
                // Debug.Log("diffuseMapSampler: " + (Shaders.SamplerState)diffuseMapSampler.Entries[i].StateId + " = " + diffuseMapSampler.Entries[i].Value);
            }
        }
        return true;
    }

    private TexOrCube GetTexOrCube(TexturePtr ptr)
    {
        if (!((ptr.Location == TexturePtr.Source.GLOBAL && !_texturesGlobal.ContainsKey(ptr.Index)) || 
              (ptr.Location == TexturePtr.Source.LEVEL && !_texturesLevel.ContainsKey(ptr.Index))))
        {
            if (ptr.Location == TexturePtr.Source.GLOBAL)
                return _texturesGlobal[ptr.Index];
            else
                return _texturesLevel[ptr.Index];
        }

        Textures.TEX4 InTexture = (ptr.Location == TexturePtr.Source.GLOBAL ? LevelContent.TexturesPAK_GLOBAL : LevelContent.TexturesPAK).GetAtWriteIndex(ptr.Index);
        if (InTexture == null) return null;
        Textures.TEX4.Texture TexPart = InTexture.TextureStreamed;

        Vector2 textureDims = new Vector2(TexPart.Width, TexPart.Height);
        if (TexPart.Content == null || TexPart.Content.Length == 0)
            return null;
        int textureLength = TexPart.Content.Length;
        int mipLevels = TexPart.MipLevels;

        UnityEngine.TextureFormat format = UnityEngine.TextureFormat.BC7;
        switch (InTexture.Format)
        {
            case Textures.TextureFormat.A32R32G32B32F:
                format = UnityEngine.TextureFormat.RGBAFloat; 
                break;
            case Textures.TextureFormat.A16R16G16B16:
                format = UnityEngine.TextureFormat.RGBAHalf; 
                break;
            case Textures.TextureFormat.A8R8G8B8:
                format = UnityEngine.TextureFormat.RGBA32;
                break;
            case Textures.TextureFormat.X8R8G8B8:
                format = UnityEngine.TextureFormat.RGB24;
                break;
            case Textures.TextureFormat.A8:
                format = UnityEngine.TextureFormat.Alpha8;
                break;
            case Textures.TextureFormat.L8:
                format = UnityEngine.TextureFormat.R8;
                break;
            case Textures.TextureFormat.DXT1:
                format = UnityEngine.TextureFormat.DXT1;
                break;
            case Textures.TextureFormat.DXT5:
                format = UnityEngine.TextureFormat.DXT5;
                break;
            case Textures.TextureFormat.DXN:
                format = UnityEngine.TextureFormat.BC5; 
                break;
            case Textures.TextureFormat.A4R4G4B4:
                format = UnityEngine.TextureFormat.ARGB4444;
                break;
            case Textures.TextureFormat.BC6H:
                format = UnityEngine.TextureFormat.BC6H; 
                break;
            case Textures.TextureFormat.BC7:
                format = UnityEngine.TextureFormat.BC7;
                break;
            case Textures.TextureFormat.R16F:
                format = UnityEngine.TextureFormat.RHalf; 
                break;
            default:
                Debug.LogError("Unsupported texture format: " + InTexture.Format);
                break;
        }

        TexOrCube tex = new TexOrCube();
        using (BinaryReader tempReader = new BinaryReader(new MemoryStream(TexPart.Content)))
        {
            if (InTexture.StateFlags.HasFlag(Textures.TextureStateFlag.CUBE))
            {
                tex.Cubemap = new Cubemap((int)textureDims.x, format, false);
                tex.Cubemap.name = InTexture.Name;
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveX);
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeX);
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveY);
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeY);
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveZ);
                tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeZ);
                tex.Cubemap.Apply(false, true);
            }
            else
            {
                tex.Texture = new Texture2D((int)textureDims[0], (int)textureDims[1], format, mipLevels, true);
                tex.Texture.name = InTexture.Name;
                tex.Texture.LoadRawTextureData(tempReader.ReadBytes(textureLength));
                tex.Texture.Apply();
            }
        }
        if (ptr.Location == TexturePtr.Source.GLOBAL)
            _texturesGlobal.Add(ptr.Index, tex);
        else
            _texturesLevel.Add(ptr.Index, tex);

        return tex;
    }

    private void ApplyEnvironmentShader(Materials.Material material, Shaders.Shader shader, Material unityMaterial)
    {
        bool transparent =
            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHA_TEST)) != 0 ||
            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA)) != 0 ||
            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.GLASS)) != 0 ||
            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE)) != 0 ||
            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA)) != 0;

        unityMaterial.SetOverrideTag("RenderType", transparent ? "Transparent" : "Opaque");
        unityMaterial.SetInt("_SrcBlend", transparent ? (int)UnityEngine.Rendering.BlendMode.SrcAlpha : (int)UnityEngine.Rendering.BlendMode.One);
        unityMaterial.SetInt("_DstBlend", transparent ? (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (int)UnityEngine.Rendering.BlendMode.Zero);
        unityMaterial.SetInt("_ZWrite", transparent ? 0 : 1);
        unityMaterial.DisableKeyword("_ALPHATEST_ON");
        if (transparent) unityMaterial.EnableKeyword("_ALPHABLEND_ON");
        else unityMaterial.DisableKeyword("_ALPHATEST_ON");
        unityMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        unityMaterial.renderQueue = transparent ? 3000 : 2000;
        
        //Apply textures
        ApplySampler(material, shader, unityMaterial, "_DiffuseMap", (int)CA_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP);
        ApplySampler(material, shader, unityMaterial, "_NormalMap", (int)CA_ENVIRONMENT.SAMPLERS.NORMAL_MAP, "NORMAL_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_SpecularMap", (int)CA_ENVIRONMENT.SAMPLERS.SPECULAR_MAP, "SPECULAR_MAPPING");
        // ApplySampler(material, shader, unityMaterial, "_DirtMap", (int)CA_ENVIRONMENT.SAMPLERS.DIRT_MAP, "DIRT_MAPPING"); 
        ApplySampler(material, shader, unityMaterial, "_ParallaxMap", (int)CA_ENVIRONMENT.SAMPLERS.PARALLAX_MAP, "PARALLAX_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_AmbientOcclusionMap", (int)CA_ENVIRONMENT.SAMPLERS.AMBIENT_OCCLUSION_MAP, "AMBIENT_OCCLUSION_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_EnvironmentMap", (int)CA_ENVIRONMENT.SAMPLERS.ENVIRONMENT_MAP, "ENVIRONMENT_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_SecondaryDiffuseMap", (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP, "SECONDARY_DIFFUSE_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_SecondaryNormalMap", (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_NORMAL_MAP, "SECONDARY_NORMAL_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_SecondarySpecularMap", (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_SPECULAR_MAP, "SECONDARY_SPECULAR_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_SeparateAlphaMap", (int)CA_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP, "SEPARATE_ALPHA");
        ApplySampler(material, shader, unityMaterial, "_DustMap", (int)CA_ENVIRONMENT.SAMPLERS.DUST_MAP, "DUST_MAPPING");
        ApplySampler(material, shader, unityMaterial, "_IrradianceCubeMap", (int)CA_ENVIRONMENT.SAMPLERS.IRRADIANCE_CUBE_MAP, "IRRADIANCE_CUBE");
        ApplySampler(material, shader, unityMaterial, "_SparkleMap", (int)CA_ENVIRONMENT.SAMPLERS.SPARKLE_MAP, "SPARKLE");
        // ApplySampler(material, shader, unityMaterial, "_WetnessNoise", (int)CA_ENVIRONMENT.SAMPLERS.WETNESS_NOISE, "WETNESS");
        // ApplySampler(material, shader, unityMaterial, "_AlphablendNoiseMap", (int)CA_ENVIRONMENT.SAMPLERS.ALPHABLEND_NOISE_MAP, "ALPHABLEND_NOISE");

        //Apply parameters
        unityMaterial.SetFloat("_DiffuseUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_NormalUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.NORMAL_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_SpecularUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_ParallaxUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_ParallaxScale", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_SCALE, 0.1f));
        unityMaterial.SetFloat("_NormalMapStrength", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.NORMAL_MAP_STRENGTH, 1.0f));
        unityMaterial.SetFloat("_SpecularPower", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_POWER, 32.0f));
        unityMaterial.SetFloat("_AmbientOcclusionMapMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.AMBIENT_OCCLUSION_MAP_MULT, 1.0f));
        unityMaterial.SetFloat("_EnvironmentMapMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.ENVIRONMENT_MAP_MULT, 1.0f));
        unityMaterial.SetFloat("_EmissiveMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT, 1.0f));
        unityMaterial.SetFloat("_SecondaryDiffuseUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_SecondaryNormalUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_NORMAL_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_SecondaryNormalMapStrength", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_NORMAL_MAP_STRENGTH, 1.0f));
        unityMaterial.SetFloat("_SecondarySpecularUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_SecondarySpecularPower", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_POWER, 32.0f));
        unityMaterial.SetFloat("_SeparateAlphaUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_DustUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DUST_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_SparkleUvScale", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_UV_SCALE, 1.0f));
        unityMaterial.SetFloat("_SparklePower", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_POWER, 1.0f));
        unityMaterial.SetFloat("_SparkleThreshold", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_THRESHOLD, 0.5f));
        unityMaterial.SetFloat("_SparkleMultiplier", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_MULTIPLIER, 1.0f));
        unityMaterial.SetFloat("_WetnessUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.WETNESS_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_WetLevel", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.WET_LEVEL, 0.0f));
        unityMaterial.SetFloat("_AlphablendNoiseUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_AlphablendNoisePower", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_POWER, 1.0f));
        unityMaterial.SetFloat("_DirtUvMult", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIRT_UV_MULT, 1.0f));
        unityMaterial.SetFloat("_DirtBlendMultSpecPower", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIRT_BLEND_MULT_SPEC_POWER, 1.0f));
        unityMaterial.SetFloat("_DirtAoAmount", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIRT_AO_AMOUNT, 1.0f));
        unityMaterial.SetFloat("_FurRimLightingFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.FUR_RIM_LIGHTING_FACTOR, 1.0f));
        unityMaterial.SetFloat("_DiffuseRoughnessFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_ROUGHNESS_FACTOR, 1.0f));
        unityMaterial.SetFloat("_OpacityModifierValue", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.OPACITY_MODIFIER_VALUE, 1.0f));
        unityMaterial.SetFloat("_GlassDensity", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.GLASS_DENSITY, 1.0f));
        unityMaterial.SetFloat("_GlassLightness", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.GLASS_LIGHTNESS, 1.0f));
        unityMaterial.SetFloat("_SsrAmount", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SSR_AMOUNT, 1.0f));
        unityMaterial.SetFloat("_EnvironmentEmissiveFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.ENVIRONMENT_EMISSIVE_FACTOR, 1.0f));
        unityMaterial.SetFloat("_DustFalloff", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DUST_FALLOFF, 1.0f));
        unityMaterial.SetFloat("_SparkleNormalBias", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_NORMAL_BIAS, 0.0f));
        unityMaterial.SetFloat("_SparkleFadeStart", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_FADE_START, 0.0f));
        unityMaterial.SetFloat("_ParallaxBias", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_BIAS, 0.02f));
        unityMaterial.SetFloat("_TessellationFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.TESSELLATION_FACTOR, 1.0f));
        unityMaterial.SetFloat("_MinTessellationDistance", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.MIN_TESSELLATION_DISTANCE, 1.0f));
        unityMaterial.SetFloat("_TessellationRange", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.TESSELLATION_RANGE, 10.0f));
        unityMaterial.SetFloat("_ShapeFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SHAPE_FACTOR, 1.0f));
        unityMaterial.SetFloat("_DisplacementFactor", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DISPLACEMENT_FACTOR, 1.0f));
        unityMaterial.SetFloat("_DisplacementMapUvScale", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DISPLACEMENT_MAP_UV_SCALE, 1.0f));
        unityMaterial.SetFloat("_SizeCullingThreshold", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SIZE_CULLING_THRESHOLD, 0.01f));
        unityMaterial.SetFloat("_ForcePriorityLevel", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.FORCE_PRIORITY_LEVEL, 0.0f));
        unityMaterial.SetFloat("_ShiftPriorityLevel", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SHIFT_PRIORITY_LEVEL, 0.0f));
        unityMaterial.SetFloat("_FresnelIntensity", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.FRESNEL_INTENSITY, 1.0f));
        unityMaterial.SetFloat("_PlanarReflectiveOverbrightScalar", GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.PLANAR_REFLECTIVE_OVERBRIGHT_SCALAR, 1.0f));
        
        //Apply colors
        Vector4 diffuseTint = GetShaderVector4(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT, Vector4.one);
        unityMaterial.SetColor("_DiffuseTint", new Color(diffuseTint.x, diffuseTint.y, diffuseTint.z, diffuseTint.w));
        Vector4 secondaryDiffuseTint = GetShaderVector4(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_TINT, Vector4.one);
        unityMaterial.SetColor("_SecondaryDiffuseTint", new Color(secondaryDiffuseTint.x, secondaryDiffuseTint.y, secondaryDiffuseTint.z, secondaryDiffuseTint.w));
        Vector3 specularTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_TINT, Vector3.one);
        unityMaterial.SetColor("_SpecularTint", new Color(specularTint.x, specularTint.y, specularTint.z, 1.0f));
        Vector3 secondarySpecularTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_TINT, Vector3.one);
        unityMaterial.SetColor("_SecondarySpecularTint", new Color(secondarySpecularTint.x, secondarySpecularTint.y, secondarySpecularTint.z, 1.0f));
        Vector3 emissiveTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT, Vector3.one);
        unityMaterial.SetColor("_EmissiveTint", new Color(emissiveTint.x, emissiveTint.y, emissiveTint.z, 1.0f));
        Vector3 aoTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.AO_TINT, Vector3.one);
        unityMaterial.SetColor("_AoTint", new Color(aoTint.x, aoTint.y, aoTint.z, 1.0f));
        Vector3 vertAoTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.VERT_AO_TINT, Vector3.one);
        unityMaterial.SetColor("_VertAoTint", new Color(vertAoTint.x, vertAoTint.y, vertAoTint.z, 1.0f));
        Vector3 glassTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.GLASS_TINT, Vector3.one);
        unityMaterial.SetColor("_GlassTint", new Color(glassTint.x, glassTint.y, glassTint.z, 1.0f));
        Vector3 customTintColour = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.CUSTOM_TINT_COLOUR, Vector3.one);
        unityMaterial.SetColor("_CustomTintColour", new Color(customTintColour.x, customTintColour.y, customTintColour.z, 1.0f));
        
        //Set feature flags
        unityMaterial.SetFloat("_VertexColour", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.VERTEX_COLOUR)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_FogAlpha", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FOG_ALPHA)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_ReflectivePlastic", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.REFLECTIVE_PLASTIC)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DoubleSided", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DOUBLE_SIDED)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_UseAlphaAsBlendFactor", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.USE_ALPHA_AS_BLENDFACTOR)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_ForceToAlpha", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AlphaTest", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHA_TEST)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_TextureLodBiasNone", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_NONE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_TextureLodBiasSlight", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_SLIGHT)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_TextureLodBiasHigh", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_HIGH)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_PlanarReflective", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.PLANAR_REFLECTIVE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SeparateAlpha", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SeparateAlphaMapUseGreenChannel", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SignedDistanceField", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SIGNED_DISTANCE_FIELD)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DiffuseMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DIFFUSE_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondaryDiffuseMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_DIFFUSE_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondaryDiffuseBlendMultiply", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_DIFFUSE_BLEND_MULTIPLY)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_NormalMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.NORMAL_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_NormalMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.NORMAL_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondaryNormalMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_NORMAL_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondaryNormalBlendAdd", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_NORMAL_BLEND_ADD)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SpecularMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SpecularMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondarySpecularMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondarySpecularMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SecondarySpecularBlendMultiply", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_BLEND_MULTIPLY)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Glass", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.GLASS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DiffuseRoughness", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DIFFUSE_ROUGHNESS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_FrontRoughness", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FRONT_ROUGHNESS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AdditiveRoughness", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ADDITIVE_ROUGHNESS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_EnvironmentMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ENVIRONMENT_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AmbientOcclusionMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.AMBIENT_OCCLUSION_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AmbientOcclusionUV", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.AMBIENT_OCCLUSION_UV)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_VertexAmbientOcclusion", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.VERTEX_AMBIENT_OCCLUSION)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Emissive", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.EMISSIVE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DustMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DUST_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DustMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DUST_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SSR", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SSR)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_IrradianceCube", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.IRRADIANCE_CUBE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_RadiosityDynamic", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.RADIOSITY_DYNAMIC)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_FurRimLighting", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FUR_RIM_LIGHTING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_ParallaxMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.PARALLAX_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Decal", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DECAL)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DecalDiffuse", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DECAL_DIFFUSE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DecalNormal", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DECAL_NORMAL)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DecalSpecularEmissive", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DECAL_SPECULAR_EMISSIVE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_SpecularMappingMetalnessMasking", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING_METALNESS_MASKING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AlphablendNoise", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_AlphaLighting", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHA_LIGHTING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Sparkle", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SPARKLE)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_RadiosityStatic", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.RADIOSITY_STATIC)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DirtMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DIRT_MAPPING)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DirtBlendMultiply", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DIRT_BLEND_MULTIPLY)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DirtMappingParallax", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DIRT_MAPPING_PARALLAX)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Wetness", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.WETNESS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_HiLodCustomCharacterCorpseConstants", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.HI_LOD_CUSTOM_CHARACTER_CORPSE_CONSTANTS)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_NoClip", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.NO_CLIP)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_Tessellation", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.TESSELLATION)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_OrientationAdaptiveTessellation", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ORIENTATION_ADAPTIVE_TESSELLATION)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_PhongTessellation", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.PHONG_TESSELLATION)) != 0 ? 1.0f : 0.0f);
        unityMaterial.SetFloat("_DisplacementMapping", (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.DISPLACEMENT_MAPPING)) != 0 ? 1.0f : 0.0f);
    }
#endif
}

//Temp wrapper for GameObject while we just want it in memory
public class GameObjectHolder
{
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

        Parallel.For(0, 9, (i) =>
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
                    ShadersPAK = new Shaders(renderablePath + "LEVEL_SHADERS_DX11.PAK");
                    break;
#if USE_MATERIAL_DATA
                case 7:
                    TexturesPAK = new Textures(renderablePath + "LEVEL_TEXTURES.ALL.PAK");
                    break;
                case 8:
                    TexturesPAK_GLOBAL = new Textures(aiPath + "/DATA/ENV/GLOBAL/WORLD/GLOBAL_TEXTURES.ALL.PAK");
                    break;
#endif
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
        TexturesPAK = null;
        TexturesPAK_GLOBAL = null;
        RemappedResources.Clear();
    }

    public static bool Loaded => CommandsPAK != null && CommandsPAK.Loaded;

    public static Commands CommandsPAK;
    public static RenderableElements RenderableREDS;
    public static CATHODE.Resources ResourcesBIN;
    public static byte[] ModelsCST;
    public static Materials ModelsMTL;
    public static Models ModelsPAK;
    public static Shaders ShadersPAK;
    public static Textures TexturesPAK;
    public static Textures TexturesPAK_GLOBAL;

    //This acts as a temporary override for REDS.BIN mapping runtime changes from Commands Editor
    public static Dictionary<Entity, List<Tuple<int, int>>> RemappedResources = new Dictionary<Entity, List<Tuple<int, int>>>(); //Model Index, Material Index
};