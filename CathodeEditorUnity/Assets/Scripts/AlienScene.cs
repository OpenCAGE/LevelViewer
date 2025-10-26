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

    private Material GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material material = LevelContent.ModelsMTL.GetAtWriteIndex(MTLIndex);
            Shaders.Shader shader = LevelContent.ShadersPAK.Entries[material.ShaderIndex];

            Material unityMaterial = new Material(UnityEngine.Shader.Find("Standard"));
            unityMaterial.name = material.Name;
            switch (shader.Ubershader)
            {
                //Unsupported shader slot types - draw transparent for now
                case SHADER_LIST.CA_SHADOWCASTER:
                case SHADER_LIST.CA_DEFERRED:
                case SHADER_LIST.CA_DEBUG:
                case SHADER_LIST.CA_OCCLUSION_CULLING:
                case SHADER_LIST.CA_FOGSPHERE:
                case SHADER_LIST.CA_FOGPLANE:
                case SHADER_LIST.CA_EFFECT_OVERLAY:
                case SHADER_LIST.CA_DECAL:
                case SHADER_LIST.CA_VOLUME_LIGHT:
                case SHADER_LIST.CA_REFRACTION:
                    unityMaterial.name += " (NOT RENDERED: " + shader.Ubershader.ToString() + ")";
                    _materialSupport.Add(unityMaterial, false);
                    return unityMaterial;
            }
            unityMaterial.name += " " + shader.Ubershader.ToString();

#if USE_MATERIAL_DATA
            float DIFFUSE_UV_MULT = 16; //why does this need to be 16?
            switch (shader.Ubershader)
            {
                case SHADER_LIST.CA_ENVIRONMENT:
                    ApplySampler(material, shader, unityMaterial, "_MainTex", (int)CA_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP);
                    ApplySampler(material, shader, unityMaterial, "_BumpMap", (int)CA_ENVIRONMENT.SAMPLERS.NORMAL_MAP, "_NORMALMAP");
                    if (ApplySampler(material, shader, unityMaterial, "_MetallicGlossMap", (int)CA_ENVIRONMENT.SAMPLERS.SPECULAR_MAP, "_METALLICGLOSSMAP"))
                    {
                        unityMaterial.SetFloat("_Glossiness", 0.0f);
                        unityMaterial.SetFloat("_GlossMapScale", 0.0f);
                    }
                    ApplySampler(material, shader, unityMaterial, "_DetailMask", (int)CA_ENVIRONMENT.SAMPLERS.DIRT_MAP, "_DETAIL_MULX2");
                    ApplySampler(material, shader, unityMaterial, "_ParallaxMap", (int)CA_ENVIRONMENT.SAMPLERS.PARALLAX_MAP, "_PARALLAXMAP");
                    ApplySampler(material, shader, unityMaterial, "_OcclusionMap", (int)CA_ENVIRONMENT.SAMPLERS.AMBIENT_OCCLUSION_MAP);

                    DIFFUSE_UV_MULT *= GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT, 1.0f);
                    float emissiveMult = GetShaderFloat(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT, 1.0f);
                    Vector3 emissiveTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT, Vector3.one);
                    Vector3 diffuseTint = GetShaderVector3(shader, material, (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT, Vector3.one);

                    if (shader.PixelShaderParameterRemaps[7] != 255)
                    {
                        //todo: this is labelled as half4 - perhaps it's only two values?
                        //Color DIFFUSE_TINT = new Color(material.PixelShaderConstants[shader.ParameterRemaps[2][7]], material.PixelShaderConstants[shader.ParameterRemaps[2][7] + 1], material.PixelShaderConstants[shader.ParameterRemaps[2][7] + 2]);
                        //Debug.Log(material.Name + " -> " + DIFFUSE_TINT);
                    }
                    break;
                case SHADER_LIST.CA_DECAL:
                    ApplySampler(material, shader, unityMaterial, "_MainTex", (int)CA_DECAL.SAMPLERS.DIFFUSE_MAP);
                    ApplySampler(material, shader, unityMaterial, "_ParallaxMap", (int)CA_DECAL.SAMPLERS.PARALLAX_MAP, "_PARALLAXMAP");
                    if (ApplySampler(material, shader, unityMaterial, "_MetallicGlossMap", (int)CA_DECAL.SAMPLERS.SPECULAR_MAP, "_METALLICGLOSSMAP"))
                    {
                        unityMaterial.SetFloat("_Glossiness", 0.0f);
                        unityMaterial.SetFloat("_GlossMapScale", 0.0f);
                    }
                    ApplySampler(material, shader, unityMaterial, "_BumpMap", (int)CA_DECAL.SAMPLERS.NORMAL_MAP, "_NORMALMAP");
                    break;
                case SHADER_LIST.CA_HAIR:
                    ApplySampler(material, shader, unityMaterial, "_MainTex", (int)CA_HAIR.SAMPLERS.DIFFUSE_MAP);
                    if (ApplySampler(material, shader, unityMaterial, "_MetallicGlossMap", (int)CA_HAIR.SAMPLERS.SPECULAR_MAP, "_METALLICGLOSSMAP"))
                    {
                        unityMaterial.SetFloat("_Glossiness", 0.0f);
                        unityMaterial.SetFloat("_GlossMapScale", 0.0f);
                    }
                    ApplySampler(material, shader, unityMaterial, "_BumpMap", (int)CA_HAIR.SAMPLERS.NORMAL_MAP, "_NORMALMAP");
                    if (shader.PixelShaderParameterRemaps[1] != 255)
                        DIFFUSE_UV_MULT *= material.PixelShaderConstants[shader.PixelShaderParameterRemaps[1]]; //CA_HAIR_PARAMETERS::DIFFUSE_UV_MULT
                    break;
            }
            unityMaterial.SetTextureScale("_MainTex", new Vector2(DIFFUSE_UV_MULT, DIFFUSE_UV_MULT));
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
            case Textures.TextureFormat.DXT3:
                //format = UnityEngine.TextureFormat.DXT3; 
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