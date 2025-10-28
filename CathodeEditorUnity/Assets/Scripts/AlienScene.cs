#define LOCAL_DEV

using CATHODE;
using CATHODE.LEGACY;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CATHODE.ShaderTypes;
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
#if !LOCAL_DEV
        _parentGameObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
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
#if !LOCAL_DEV
        entityGO.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
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
#if !LOCAL_DEV
        newModelSpawn.hideFlags = HideFlags.NotEditable | HideFlags.HideInHierarchy;
#endif

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

            Material unityMaterial = new Material(UnityEngine.Shader.Find("OpenCAGE"));
            unityMaterial.name = material.Name + " " + shader.Ubershader.ToString();

            switch (shader.Ubershader)
            {
                case SHADER_LIST.CA_ENVIRONMENT:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        separateAlphaMap = (int)CA_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP,
                        diffuseMap = (int)CA_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP,
                        secondaryDiffuseMap = (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_DIFFUSE_MAP,
                        normalMap = (int)CA_ENVIRONMENT.SAMPLERS.NORMAL_MAP,
                        secondaryNormalMap = (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_NORMAL_MAP,
                        specularMap = (int)CA_ENVIRONMENT.SAMPLERS.SPECULAR_MAP,
                        secondarySpecularMap = (int)CA_ENVIRONMENT.SAMPLERS.SECONDARY_SPECULAR_MAP,
                        environmentMap = (int)CA_ENVIRONMENT.SAMPLERS.ENVIRONMENT_MAP,
                        ambientOcclusionMap = (int)CA_ENVIRONMENT.SAMPLERS.AMBIENT_OCCLUSION_MAP,
                        dustMap = (int)CA_ENVIRONMENT.SAMPLERS.DUST_MAP,
                        irradianceCubeMap = (int)CA_ENVIRONMENT.SAMPLERS.IRRADIANCE_CUBE_MAP,
                        parallaxMap = (int)CA_ENVIRONMENT.SAMPLERS.PARALLAX_MAP,
                        alphablendNoiseMap = (int)CA_ENVIRONMENT.SAMPLERS.ALPHABLEND_NOISE_MAP,
                        sparkleMap = (int)CA_ENVIRONMENT.SAMPLERS.SPARKLE_MAP,
                        dirtMap = (int)CA_ENVIRONMENT.SAMPLERS.DIRT_MAP,
                        wetnessNoise = (int)CA_ENVIRONMENT.SAMPLERS.WETNESS_NOISE,
                        displacementMap = (int)CA_ENVIRONMENT.SAMPLERS.DISPLACEMENT_MAP,
                        diffuseUvMult = (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT,
                        separateAlphaUvMult = (int)CA_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT,
                        normalUvMult = (int)CA_ENVIRONMENT.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_ENVIRONMENT.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT,
                        secondaryDiffuseUvMult = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_UV_MULT,
                        secondaryDiffuseTint = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_DIFFUSE_TINT,
                        secondaryNormalUvMult = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_NORMAL_UV_MULT,
                        secondaryNormalMapStrength = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_NORMAL_MAP_STRENGTH,
                        specularUvMult = (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_UV_MULT,
                        specularPower = (int)CA_ENVIRONMENT.PARAMETERS.SPECULAR_POWER,
                        secondarySpecularTint = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_TINT,
                        secondarySpecularUvMult = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_UV_MULT,
                        secondarySpecularPower = (int)CA_ENVIRONMENT.PARAMETERS.SECONDARY_SPECULAR_POWER,
                        glassDensity = (int)CA_ENVIRONMENT.PARAMETERS.GLASS_DENSITY,
                        glassLightness = (int)CA_ENVIRONMENT.PARAMETERS.GLASS_LIGHTNESS,
                        glassTint = (int)CA_ENVIRONMENT.PARAMETERS.GLASS_TINT,
                        diffuseRoughnessFactor = (int)CA_ENVIRONMENT.PARAMETERS.DIFFUSE_ROUGHNESS_FACTOR,
                        environmentEmissiveFactor = (int)CA_ENVIRONMENT.PARAMETERS.ENVIRONMENT_EMISSIVE_FACTOR,
                        environmentMapMult = (int)CA_ENVIRONMENT.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        aoTint = (int)CA_ENVIRONMENT.PARAMETERS.AO_TINT,
                        ambientOcclusionMapMult = (int)CA_ENVIRONMENT.PARAMETERS.AMBIENT_OCCLUSION_MAP_MULT,
                        vertAoTint = (int)CA_ENVIRONMENT.PARAMETERS.VERT_AO_TINT,
                        dustUvMult = (int)CA_ENVIRONMENT.PARAMETERS.DUST_UV_MULT,
                        dustFalloff = (int)CA_ENVIRONMENT.PARAMETERS.DUST_FALLOFF,
                        ssrAmount = (int)CA_ENVIRONMENT.PARAMETERS.SSR_AMOUNT,
                        furRimLightingFactor = (int)CA_ENVIRONMENT.PARAMETERS.FUR_RIM_LIGHTING_FACTOR,
                        parallaxUvMult = (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_UV_MULT,
                        parallaxScale = (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_SCALE,
                        parallaxBias = (int)CA_ENVIRONMENT.PARAMETERS.PARALLAX_BIAS,
                        opacityModifierValue = (int)CA_ENVIRONMENT.PARAMETERS.OPACITY_MODIFIER_VALUE,
                        alphablendNoiseUvMult = (int)CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_UV_MULT,
                        alphablendNoisePower = (int)CA_ENVIRONMENT.PARAMETERS.ALPHABLEND_NOISE_POWER,
                        sparkleUvScale = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_UV_SCALE,
                        sparkleNormalBias = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_NORMAL_BIAS,
                        sparkleMultiplier = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_MULTIPLIER,
                        sparkleFadeStart = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_FADE_START,
                        sparklePower = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_POWER,
                        sparkleThreshold = (int)CA_ENVIRONMENT.PARAMETERS.SPARKLE_THRESHOLD,
                        dirtBlendMultSpecPower = (int)CA_ENVIRONMENT.PARAMETERS.DIRT_BLEND_MULT_SPEC_POWER,
                        dirtUvMult = (int)CA_ENVIRONMENT.PARAMETERS.DIRT_UV_MULT,
                        dirtAoAmount = (int)CA_ENVIRONMENT.PARAMETERS.DIRT_AO_AMOUNT,
                        wetLevel = (int)CA_ENVIRONMENT.PARAMETERS.WET_LEVEL,
                        wetnessUvMult = (int)CA_ENVIRONMENT.PARAMETERS.WETNESS_UV_MULT,
                        customTintColour = (int)CA_ENVIRONMENT.PARAMETERS.CUSTOM_TINT_COLOUR,
                        tessellationFactor = (int)CA_ENVIRONMENT.PARAMETERS.TESSELLATION_FACTOR,
                        minTessellationDistance = (int)CA_ENVIRONMENT.PARAMETERS.MIN_TESSELLATION_DISTANCE,
                        tessellationRange = (int)CA_ENVIRONMENT.PARAMETERS.TESSELLATION_RANGE,
                        shapeFactor = (int)CA_ENVIRONMENT.PARAMETERS.SHAPE_FACTOR,
                        displacementFactor = (int)CA_ENVIRONMENT.PARAMETERS.DISPLACEMENT_FACTOR,
                        displacementMapUvScale = (int)CA_ENVIRONMENT.PARAMETERS.DISPLACEMENT_MAP_UV_SCALE,
                        vertexColour = (int)CA_ENVIRONMENT.FEATURES.VERTEX_COLOUR,
                        fogAlpha = (int)CA_ENVIRONMENT.FEATURES.FOG_ALPHA,
                        reflectivePlastic = (int)CA_ENVIRONMENT.FEATURES.REFLECTIVE_PLASTIC,
                        doubleSided = (int)CA_ENVIRONMENT.FEATURES.DOUBLE_SIDED,
                        useAlphaAsBlendFactor = (int)CA_ENVIRONMENT.FEATURES.USE_ALPHA_AS_BLENDFACTOR,
                        forceToAlpha = (int)CA_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA,
                        alphaTest = (int)CA_ENVIRONMENT.FEATURES.ALPHA_TEST,
                        textureLodBiasNone = (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_NONE,
                        textureLodBiasSlight = (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_SLIGHT,
                        textureLodBiasHigh = (int)CA_ENVIRONMENT.FEATURES.TEXTURE_LOD_BIAS_HIGH,
                        planarReflective = (int)CA_ENVIRONMENT.FEATURES.PLANAR_REFLECTIVE,
                        separateAlpha = (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        signedDistanceField = (int)CA_ENVIRONMENT.FEATURES.SIGNED_DISTANCE_FIELD,
                        diffuseMappingParallax = (int)CA_ENVIRONMENT.FEATURES.DIFFUSE_MAPPING_PARALLAX,
                        secondaryDiffuseMapping = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_DIFFUSE_MAPPING,
                        secondaryDiffuseBlendMultiply = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_DIFFUSE_BLEND_MULTIPLY,
                        normalMapping = (int)CA_ENVIRONMENT.FEATURES.NORMAL_MAPPING,
                        normalMappingParallax = (int)CA_ENVIRONMENT.FEATURES.NORMAL_MAPPING_PARALLAX,
                        secondaryNormalMapping = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_NORMAL_MAPPING,
                        secondaryNormalBlendAdd = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_NORMAL_BLEND_ADD,
                        specularMapping = (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING,
                        specularMappingParallax = (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING_PARALLAX,
                        secondarySpecularMapping = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_MAPPING,
                        secondarySpecularMappingParallax = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_MAPPING_PARALLAX,
                        secondarySpecularBlendMultiply = (int)CA_ENVIRONMENT.FEATURES.SECONDARY_SPECULAR_BLEND_MULTIPLY,
                        glass = (int)CA_ENVIRONMENT.FEATURES.GLASS,
                        diffuseRoughness = (int)CA_ENVIRONMENT.FEATURES.DIFFUSE_ROUGHNESS,
                        frontRoughness = (int)CA_ENVIRONMENT.FEATURES.FRONT_ROUGHNESS,
                        additiveRoughness = (int)CA_ENVIRONMENT.FEATURES.ADDITIVE_ROUGHNESS,
                        environmentMapping = (int)CA_ENVIRONMENT.FEATURES.ENVIRONMENT_MAPPING,
                        ambientOcclusionMapping = (int)CA_ENVIRONMENT.FEATURES.AMBIENT_OCCLUSION_MAPPING,
                        ambientOcclusionUV = (int)CA_ENVIRONMENT.FEATURES.AMBIENT_OCCLUSION_UV,
                        vertexAmbientOcclusion = (int)CA_ENVIRONMENT.FEATURES.VERTEX_AMBIENT_OCCLUSION,
                        emissive = (int)CA_ENVIRONMENT.FEATURES.EMISSIVE,
                        dustMapping = (int)CA_ENVIRONMENT.FEATURES.DUST_MAPPING,
                        dustMappingParallax = (int)CA_ENVIRONMENT.FEATURES.DUST_MAPPING_PARALLAX,
                        ssr = (int)CA_ENVIRONMENT.FEATURES.SSR,
                        irradianceCube = (int)CA_ENVIRONMENT.FEATURES.IRRADIANCE_CUBE,
                        radiosityDynamic = (int)CA_ENVIRONMENT.FEATURES.RADIOSITY_DYNAMIC,
                        furRimLighting = (int)CA_ENVIRONMENT.FEATURES.FUR_RIM_LIGHTING,
                        parallaxMapping = (int)CA_ENVIRONMENT.FEATURES.PARALLAX_MAPPING,
                        decal = (int)CA_ENVIRONMENT.FEATURES.DECAL,
                        decalDiffuse = (int)CA_ENVIRONMENT.FEATURES.DECAL_DIFFUSE,
                        decalNormal = (int)CA_ENVIRONMENT.FEATURES.DECAL_NORMAL,
                        decalSpecularEmissive = (int)CA_ENVIRONMENT.FEATURES.DECAL_SPECULAR_EMISSIVE,
                        specularMappingMetalnessMasking = (int)CA_ENVIRONMENT.FEATURES.SPECULAR_MAPPING_METALNESS_MASKING,
                        alphablendNoise = (int)CA_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE,
                        alphaLighting = (int)CA_ENVIRONMENT.FEATURES.ALPHA_LIGHTING,
                        sparkle = (int)CA_ENVIRONMENT.FEATURES.SPARKLE,
                        radiosityStatic = (int)CA_ENVIRONMENT.FEATURES.RADIOSITY_STATIC,
                        dirtBlendMultiply = (int)CA_ENVIRONMENT.FEATURES.DIRT_BLEND_MULTIPLY,
                        dirtMappingParallax = (int)CA_ENVIRONMENT.FEATURES.DIRT_MAPPING_PARALLAX,
                        wetness = (int)CA_ENVIRONMENT.FEATURES.WETNESS,
                        hiLodCustomCharacterCorpseConstants = (int)CA_ENVIRONMENT.FEATURES.HI_LOD_CUSTOM_CHARACTER_CORPSE_CONSTANTS,
                        noClip = (int)CA_ENVIRONMENT.FEATURES.NO_CLIP,
                        tessellation = (int)CA_ENVIRONMENT.FEATURES.TESSELLATION,
                        orientationAdaptiveTessellation = (int)CA_ENVIRONMENT.FEATURES.ORIENTATION_ADAPTIVE_TESSELLATION,
                        phongTessellation = (int)CA_ENVIRONMENT.FEATURES.PHONG_TESSELLATION,
                        displacementMapping = (int)CA_ENVIRONMENT.FEATURES.DISPLACEMENT_MAPPING,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.GLASS)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_ENVIRONMENT.FEATURES.SEPARATE_ALPHA)) != 0
                    });
                    break;
                case SHADER_LIST.CA_DECAL_ENVIRONMENT:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_DECAL_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP,
                        separateAlphaMap = (int)CA_DECAL_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP,
                        normalMap = (int)CA_DECAL_ENVIRONMENT.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT,
                        separateAlphaUvMult = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT,
                        normalUvMult = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_DECAL_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT,
                        emissive = (int)CA_DECAL_ENVIRONMENT.FEATURES.EMISSIVE,
                        normalMapping = (int)CA_DECAL_ENVIRONMENT.FEATURES.NORMAL_MAPPING,
                        separateAlpha = (int)CA_DECAL_ENVIRONMENT.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_DECAL_ENVIRONMENT.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_CHARACTER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_CHARACTER.SAMPLERS.DIFFUSE_MAP,
                        separateAlphaMap = (int)CA_CHARACTER.SAMPLERS.SEPARATE_ALPHA_MAP,
                        normalMap = (int)CA_CHARACTER.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_CHARACTER.PARAMETERS.DIFFUSE_UV_MULT,
                        separateAlphaUvMult = (int)CA_CHARACTER.PARAMETERS.SEPARATE_ALPHA_UV_MULT,
                        normalUvMult = (int)CA_CHARACTER.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_CHARACTER.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_CHARACTER.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_CHARACTER.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_CHARACTER.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_CHARACTER.PARAMETERS.EMISSIVE_TINT,
                        emissive = (int)CA_CHARACTER.FEATURES.EMISSIVE,
                        normalMapping = (int)CA_CHARACTER.FEATURES.NORMAL_MAPPING,
                        separateAlpha = (int)CA_CHARACTER.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_CHARACTER.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_CHARACTER.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_CHARACTER.FEATURES.FORCE_TO_ALPHA)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_CHARACTER.FEATURES.ALPHABLEND_NOISE)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_CHARACTER.FEATURES.SEPARATE_ALPHA)) != 0
                    });
                    break;
                case SHADER_LIST.CA_SKIN:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_SKIN.SAMPLERS.DIFFUSE_MAP,
                        normalMap = (int)CA_SKIN.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_SKIN.PARAMETERS.DIFFUSE_UV_MULT,
                        normalUvMult = (int)CA_SKIN.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_SKIN.PARAMETERS.NORMAL_MAP_STRENGTH_DIFFUSE,
                        diffuseTint = (int)CA_SKIN.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_SKIN.PARAMETERS.SPECULAR_TINT,
                        normalMapping = (int)CA_SKIN.FEATURES.NORMAL_MAPPING
                    });
                    break;
                case SHADER_LIST.CA_HAIR:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_HAIR.SAMPLERS.DIFFUSE_MAP,
                        normalMap = (int)CA_HAIR.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_HAIR.PARAMETERS.DIFFUSE_UV_MULT,
                        normalUvMult = (int)CA_HAIR.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_HAIR.PARAMETERS.NORMAL_MAP_STRENGTH_DIFFUSE,
                        diffuseTint = (int)CA_HAIR.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_HAIR.PARAMETERS.SPECULAR_TINT,
                        normalMapping = (int)CA_HAIR.FEATURES.NORMAL_MAPPING
                    });
                    break;
                case SHADER_LIST.CA_EYE:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_EYE.SAMPLERS.IRIS_MAP,
                        normalMap = (int)CA_EYE.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_EYE.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_EYE.PARAMETERS.NORMAL_MAP_STRENGTH_SPEC,
                        normalMapping = (int)CA_EYE.FEATURES.NORMAL_MAPPING
                    });
                    break;
                case SHADER_LIST.CA_SKIN_OCCLUSION:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_SKIN_OCCLUSION.SAMPLERS.DIFFUSE_MAP,
                        diffuseUvMult = (int)CA_SKIN_OCCLUSION.PARAMETERS.DIFFUSE_UV_MULT,
                        diffuseTint = (int)CA_SKIN_OCCLUSION.PARAMETERS.DIFFUSE_TINT
                    });
                    break;
                /*
                case SHADER_LIST.CA_DECAL:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_DECAL.SAMPLERS.DIFFUSE_MAP,
                        separateAlphaMap = (int)CA_DECAL.SAMPLERS.SEPARATE_ALPHA_MAP,
                        normalMap = (int)CA_DECAL.SAMPLERS.NORMAL_MAP,
                        normalMapStrength = (int)CA_DECAL.PARAMETERS.NORMAL_MAP_EASE_DURATION,
                        normalMapping = (int)CA_DECAL.FEATURES.NORMAL_MAPPING,
                        separateAlpha = (int)CA_DECAL.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_DECAL.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_FOGPLANE:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_FOGPLANE.SAMPLERS.DIFFUSE_MAP_0,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_FOGSPHERE:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_DEBUG:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseTint = (int)CA_DEBUG.PARAMETERS.COLOUR_TINT
                    });
                    break;
                case SHADER_LIST.CA_EFFECT:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_EFFECT.SAMPLERS.DIFFUSE_MAP_0,
                        diffuseTint = (int)CA_EFFECT.PARAMETERS.COLOUR_TINT,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_LIQUID_ENVIRONMENT:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_LIQUID_ENVIRONMENT.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_LIQUID_ENVIRONMENT.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapStrength = (int)CA_LIQUID_ENVIRONMENT.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapping = (int)CA_LIQUID_ENVIRONMENT.FEATURES.NORMAL_MAPPING,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_LIQUID_CHARACTER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_LIQUID_CHARACTER.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_LIQUID_CHARACTER.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapStrength = (int)CA_LIQUID_CHARACTER.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapping = (int)CA_LIQUID_CHARACTER.FEATURES.NORMAL_MAPPING,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_REFRACTION:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_REFRACTION.SAMPLERS.NORMAL_MAP,
                        normalMapping = (int)CA_REFRACTION.FEATURES.SECONDARY_NORMAL_MAPPING,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_SIMPLE_REFRACTION:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_SIMPLE_REFRACTION.SAMPLERS.NORMAL_MAP,
                        normalMapping = (int)CA_SIMPLE_REFRACTION.FEATURES.SECONDARY_NORMAL_MAPPING,
                        transparent = true
                    });
                    break;
                */
                case SHADER_LIST.CA_SKYDOME:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_SKYDOME.SAMPLERS.SKYDOME_MAP
                    });
                    break;
                case SHADER_LIST.CA_SURFACE_EFFECTS:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_SURFACE_EFFECTS.SAMPLERS.DIFFUSE_MAP,
                        normalMap = (int)CA_SURFACE_EFFECTS.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_SURFACE_EFFECTS.PARAMETERS.DIFFUSE_UV_MULT,
                        normalUvMult = (int)CA_SURFACE_EFFECTS.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_SURFACE_EFFECTS.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_SURFACE_EFFECTS.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_SURFACE_EFFECTS.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_SURFACE_EFFECTS.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_SURFACE_EFFECTS.PARAMETERS.EMISSIVE_TINT,
                        emissive = (int)CA_SURFACE_EFFECTS.FEATURES.EMISSIVE,
                        normalMapping = (int)CA_SURFACE_EFFECTS.FEATURES.NORMAL_MAPPING,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_SURFACE_EFFECTS.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_SURFACE_EFFECTS.FEATURES.FORCE_TO_ALPHA)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_SURFACE_EFFECTS.FEATURES.ALPHA_LIGHTING)) != 0
                    });
                    break;
                case SHADER_LIST.CA_EFFECT_OVERLAY:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_EFFECT_OVERLAY.SAMPLERS.TEXTURE_MAP,
                        diffuseTint = (int)CA_EFFECT_OVERLAY.PARAMETERS.COLOUR_TINT,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_TERRAIN:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_TERRAIN.SAMPLERS.DIFFUSE_MAP,
                        normalMap = (int)CA_TERRAIN.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_TERRAIN.PARAMETERS.DIFFUSE_UV_MULT,
                        normalUvMult = (int)CA_TERRAIN.PARAMETERS.NORMAL_UV_MULT,
                        diffuseTint = (int)CA_TERRAIN.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_TERRAIN.PARAMETERS.SPECULAR_TINT,
                        normalMapping = (int)CA_TERRAIN.FEATURES.NORMAL_MAPPING
                    });
                    break;
                case SHADER_LIST.CA_NONINTERACTIVE_WATER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_NONINTERACTIVE_WATER.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_NONINTERACTIVE_WATER.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapStrength = (int)CA_NONINTERACTIVE_WATER.PARAMETERS.NORMAL_MAP_STRENGTH,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_SIMPLEWATER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_SIMPLEWATER.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_SIMPLEWATER.PARAMETERS.ENVIRONMENT_MAP_MULT,
                        normalMapStrength = (int)CA_SIMPLEWATER.PARAMETERS.NORMAL_MAP_STRENGTH,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_PLANET:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_PLANET.SAMPLERS.ATMOSPHERE_MAP,
                        diffuseTint = (int)CA_PLANET.PARAMETERS.ATMOSPHERE_RIM_COLOUR
                    });
                    break;
                case SHADER_LIST.CA_LIGHTMAP_ENVIRONMENT:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.DIFFUSE_MAP,
                        separateAlphaMap = (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.SEPARATE_ALPHA_MAP,
                        normalMap = (int)CA_LIGHTMAP_ENVIRONMENT.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.DIFFUSE_UV_MULT,
                        separateAlphaUvMult = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.SEPARATE_ALPHA_UV_MULT,
                        normalUvMult = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_LIGHTMAP_ENVIRONMENT.PARAMETERS.EMISSIVE_TINT,
                        emissive = (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.EMISSIVE,
                        normalMapping = (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.NORMAL_MAPPING,
                        separateAlpha = (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.FORCE_TO_ALPHA)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.ALPHABLEND_NOISE)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LIGHTMAP_ENVIRONMENT.FEATURES.SEPARATE_ALPHA)) != 0
                    });
                    break;
                case SHADER_LIST.CA_STREAMER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_STREAMER.SAMPLERS.DIFFUSE_MAP,
                        separateAlphaMap = (int)CA_STREAMER.SAMPLERS.SEPARATE_ALPHA_MAP,
                        normalMap = (int)CA_STREAMER.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_STREAMER.PARAMETERS.DIFFUSE_UV_MULT,
                        separateAlphaUvMult = (int)CA_STREAMER.PARAMETERS.SEPARATE_ALPHA_UV_MULT,
                        normalUvMult = (int)CA_STREAMER.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_STREAMER.PARAMETERS.NORMAL_MAP_STRENGTH,
                        emissiveMult = (int)CA_STREAMER.PARAMETERS.EMISSIVE_MULT,
                        diffuseTint = (int)CA_STREAMER.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_STREAMER.PARAMETERS.SPECULAR_TINT,
                        emissiveTint = (int)CA_STREAMER.PARAMETERS.EMISSIVE_TINT,
                        emissive = (int)CA_STREAMER.FEATURES.EMISSIVE,
                        normalMapping = (int)CA_STREAMER.FEATURES.NORMAL_MAPPING,
                        separateAlpha = (int)CA_STREAMER.FEATURES.SEPARATE_ALPHA,
                        separateAlphaUseGreen = (int)CA_STREAMER.FEATURES.SEPARATE_ALPHA_MAP_USE_GREEN_CHANNEL,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_STREAMER.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_STREAMER.FEATURES.FORCE_TO_ALPHA)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_STREAMER.FEATURES.ALPHABLEND_NOISE)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_STREAMER.FEATURES.SEPARATE_ALPHA)) != 0
                    });
                    break;
                case SHADER_LIST.CA_LOW_LOD_CHARACTER:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_LOW_LOD_CHARACTER.SAMPLERS.DIFFUSE_MAP,
                        normalMap = (int)CA_LOW_LOD_CHARACTER.SAMPLERS.NORMAL_MAP,
                        diffuseUvMult = (int)CA_LOW_LOD_CHARACTER.PARAMETERS.DIFFUSE_UV_MULT,
                        normalUvMult = (int)CA_LOW_LOD_CHARACTER.PARAMETERS.NORMAL_UV_MULT,
                        normalMapStrength = (int)CA_LOW_LOD_CHARACTER.PARAMETERS.NORMAL_MAP_STRENGTH,
                        diffuseTint = (int)CA_LOW_LOD_CHARACTER.PARAMETERS.DIFFUSE_TINT,
                        specularTint = (int)CA_LOW_LOD_CHARACTER.PARAMETERS.SPECULAR_TINT,
                        normalMapping = (int)CA_LOW_LOD_CHARACTER.FEATURES.NORMAL_MAPPING,
                        transparent =
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LOW_LOD_CHARACTER.FEATURES.ALPHA_TEST)) != 0 ||
                            (shader.UbershaderFeatureFlags & (1L << (int)CA_LOW_LOD_CHARACTER.FEATURES.FORCE_TO_ALPHA)) != 0
                    });
                    break;
                case SHADER_LIST.CA_SPACESUIT_VISOR:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        normalMap = (int)CA_SPACESUIT_VISOR.SAMPLERS.NORMAL_MAP,
                        normalUvMult = (int)CA_SPACESUIT_VISOR.PARAMETERS.NORMAL_MAP_MULT,
                        normalMapStrength = (int)CA_SPACESUIT_VISOR.PARAMETERS.NORMAL_MAPPING_STRENGTH,
                        normalMapping = (int)CA_SPACESUIT_VISOR.FEATURES.NORMAL_MAPPING,
                        transparent = true
                    });
                    break;
                case SHADER_LIST.CA_CAMERA_MAP:
                    UpdateMaterial(material, shader, unityMaterial, new MaterialProps()
                    {
                        diffuseMap = (int)CA_CAMERA_MAP.SAMPLERS.DIFFUSE_MAP
                    });
                    break;
                default:
                    unityMaterial.name += " (NOT RENDERED)";
                    _materialSupport.Add(unityMaterial, false);
                    _materials.Add(MTLIndex, unityMaterial);
                    return unityMaterial;
            }

            _materialSupport.Add(unityMaterial, true);
            _materials.Add(MTLIndex, unityMaterial);
        }
        return _materials[MTLIndex];
    }

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

    class MaterialProps
    {
        //Sampler indexes
        public int diffuseMap = -1;
        public int separateAlphaMap = -1;
        public int normalMap = -1;
        public int secondaryDiffuseMap = -1;
        public int secondaryNormalMap = -1;
        public int specularMap = -1;
        public int secondarySpecularMap = -1;
        public int environmentMap = -1;
        public int ambientOcclusionMap = -1;
        public int dustMap = -1;
        public int irradianceCubeMap = -1;
        public int parallaxMap = -1;
        public int alphablendNoiseMap = -1;
        public int sparkleMap = -1;
        public int dirtMap = -1;
        public int wetnessNoise = -1;
        public int displacementMap = -1;

        //Parameter indexes
        public int diffuseUvMult = -1;
        public int separateAlphaUvMult = -1;
        public int normalUvMult = -1;
        public int normalMapStrength = -1;
        public int emissiveMult = -1;
        public int diffuseTint = -1;
        public int specularTint = -1;
        public int emissiveTint = -1;
        public int secondaryDiffuseUvMult = -1;
        public int secondaryDiffuseTint = -1;
        public int secondaryNormalUvMult = -1;
        public int secondaryNormalMapStrength = -1;
        public int specularUvMult = -1;
        public int specularPower = -1;
        public int secondarySpecularTint = -1;
        public int secondarySpecularUvMult = -1;
        public int secondarySpecularPower = -1;
        public int glassDensity = -1;
        public int glassLightness = -1;
        public int glassTint = -1;
        public int diffuseRoughnessFactor = -1;
        public int environmentEmissiveFactor = -1;
        public int environmentMapMult = -1;
        public int aoTint = -1;
        public int ambientOcclusionMapMult = -1;
        public int vertAoTint = -1;
        public int dustUvMult = -1;
        public int dustFalloff = -1;
        public int ssrAmount = -1;
        public int furRimLightingFactor = -1;
        public int parallaxUvMult = -1;
        public int parallaxScale = -1;
        public int parallaxBias = -1;
        public int opacityModifierValue = -1;
        public int alphablendNoiseUvMult = -1;
        public int alphablendNoisePower = -1;
        public int sparkleUvScale = -1;
        public int sparkleNormalBias = -1;
        public int sparkleMultiplier = -1;
        public int sparkleFadeStart = -1;
        public int sparklePower = -1;
        public int sparkleThreshold = -1;
        public int dirtBlendMultSpecPower = -1;
        public int dirtUvMult = -1;
        public int dirtAoAmount = -1;
        public int wetLevel = -1;
        public int wetnessUvMult = -1;
        public int customTintColour = -1;
        public int tessellationFactor = -1;
        public int minTessellationDistance = -1;
        public int tessellationRange = -1;
        public int shapeFactor = -1;
        public int displacementFactor = -1;
        public int displacementMapUvScale = -1;

        //Feature flag indexes
        public int emissive = -1;
        public int normalMapping = -1;
        public int separateAlpha = -1;
        public int separateAlphaUseGreen = -1;
        public int vertexColour = -1;
        public int fogAlpha = -1;
        public int reflectivePlastic = -1;
        public int doubleSided = -1;
        public int useAlphaAsBlendFactor = -1;
        public int forceToAlpha = -1;
        public int alphaTest = -1;
        public int textureLodBiasNone = -1;
        public int textureLodBiasSlight = -1;
        public int textureLodBiasHigh = -1;
        public int planarReflective = -1;
        public int signedDistanceField = -1;
        public int diffuseMappingParallax = -1;
        public int secondaryDiffuseMapping = -1;
        public int secondaryDiffuseBlendMultiply = -1;
        public int normalMappingParallax = -1;
        public int secondaryNormalMapping = -1;
        public int secondaryNormalBlendAdd = -1;
        public int specularMapping = -1;
        public int specularMappingParallax = -1;
        public int secondarySpecularMapping = -1;
        public int secondarySpecularMappingParallax = -1;
        public int secondarySpecularBlendMultiply = -1;
        public int glass = -1;
        public int diffuseRoughness = -1;
        public int frontRoughness = -1;
        public int additiveRoughness = -1;
        public int environmentMapping = -1;
        public int ambientOcclusionMapping = -1;
        public int ambientOcclusionUV = -1;
        public int vertexAmbientOcclusion = -1;
        public int dustMapping = -1;
        public int dustMappingParallax = -1;
        public int ssr = -1;
        public int irradianceCube = -1;
        public int radiosityDynamic = -1;
        public int furRimLighting = -1;
        public int parallaxMapping = -1;
        public int decal = -1;
        public int decalDiffuse = -1;
        public int decalNormal = -1;
        public int decalSpecularEmissive = -1;
        public int specularMappingMetalnessMasking = -1;
        public int alphablendNoise = -1;
        public int alphaLighting = -1;
        public int sparkle = -1;
        public int radiosityStatic = -1;
        public int dirtMapping = -1;
        public int dirtBlendMultiply = -1;
        public int dirtMappingParallax = -1;
        public int wetness = -1;
        public int hiLodCustomCharacterCorpseConstants = -1;
        public int noClip = -1;
        public int tessellation = -1;
        public int orientationAdaptiveTessellation = -1;
        public int phongTessellation = -1;
        public int displacementMapping = -1;

        public bool transparent = false;
    }
    private void UpdateMaterial(Materials.Material material, Shaders.Shader shader, Material unityMaterial, MaterialProps props)
    {
        unityMaterial.SetOverrideTag("RenderType", props.transparent ? "Transparent" : "Opaque");
        unityMaterial.SetInt("_SrcBlend", props.transparent ? (int)UnityEngine.Rendering.BlendMode.SrcAlpha : (int)UnityEngine.Rendering.BlendMode.One);
        unityMaterial.SetInt("_DstBlend", props.transparent ? (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : (int)UnityEngine.Rendering.BlendMode.Zero);
        unityMaterial.SetInt("_ZWrite", props.transparent ? 0 : 1);
        unityMaterial.DisableKeyword("_ALPHATEST_ON");
        if (props.transparent) unityMaterial.EnableKeyword("_ALPHABLEND_ON");
        else unityMaterial.DisableKeyword("_ALPHATEST_ON");
        unityMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        unityMaterial.renderQueue = props.transparent ? 3000 : 2000;

        if (props.diffuseMap != -1)
            ApplySampler(material, shader, unityMaterial, "_DiffuseMap", props.diffuseMap);
        if (props.separateAlphaMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SeparateAlphaMap", props.separateAlphaMap);
        if (props.normalMap != -1)
            ApplySampler(material, shader, unityMaterial, "_NormalMap", props.normalMap, "NORMAL_MAPPING");
        if (props.secondaryDiffuseMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SecondaryDiffuseMap", props.secondaryDiffuseMap);
        if (props.secondaryNormalMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SecondaryNormalMap", props.secondaryNormalMap);
        if (props.specularMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SpecularMap", props.specularMap);
        if (props.secondarySpecularMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SecondarySpecularMap", props.secondarySpecularMap);
        if (props.environmentMap != -1)
            ApplySampler(material, shader, unityMaterial, "_EnvironmentMap", props.environmentMap);
        if (props.ambientOcclusionMap != -1)
            ApplySampler(material, shader, unityMaterial, "_AmbientOcclusionMap", props.ambientOcclusionMap);
        if (props.dustMap != -1)
            ApplySampler(material, shader, unityMaterial, "_DustMap", props.dustMap);
        if (props.irradianceCubeMap != -1)
            ApplySampler(material, shader, unityMaterial, "_IrradianceCubeMap", props.irradianceCubeMap);
        if (props.parallaxMap != -1)
            ApplySampler(material, shader, unityMaterial, "_ParallaxMap", props.parallaxMap);
        if (props.alphablendNoiseMap != -1)
            ApplySampler(material, shader, unityMaterial, "_AlphablendNoiseMap", props.alphablendNoiseMap);
        if (props.sparkleMap != -1)
            ApplySampler(material, shader, unityMaterial, "_SparkleMap", props.sparkleMap);
        if (props.dirtMap != -1)
            ApplySampler(material, shader, unityMaterial, "_DirtMap", props.dirtMap);
        if (props.wetnessNoise != -1)
            ApplySampler(material, shader, unityMaterial, "_WetnessNoise", props.wetnessNoise);
        if (props.displacementMap != -1)
            ApplySampler(material, shader, unityMaterial, "_DisplacementMap", props.displacementMap);

        if (props.diffuseUvMult != -1)
            unityMaterial.SetFloat("_DiffuseUvMult", GetShaderFloat(shader, material, props.diffuseUvMult, 1.0f));
        if (props.separateAlphaUvMult != -1)
            unityMaterial.SetFloat("_SeparateAlphaUvMult", GetShaderFloat(shader, material, props.separateAlphaUvMult, 1.0f));
        if (props.normalUvMult != -1)
            unityMaterial.SetFloat("_NormalUvMult", GetShaderFloat(shader, material, props.normalUvMult, 1.0f));
        if (props.normalMapStrength != -1)
            unityMaterial.SetFloat("_NormalMapStrength", GetShaderFloat(shader, material, props.normalMapStrength, 1.0f));
        if (props.emissiveMult != -1)
            unityMaterial.SetFloat("_EmissiveMult", GetShaderFloat(shader, material, props.emissiveMult, 1.0f));

        if (props.diffuseTint != -1)
        {
            Vector4 diffuseTint = GetShaderVector4(shader, material, props.diffuseTint, Vector4.one);
            unityMaterial.SetColor("_DiffuseTint", new Color(diffuseTint.x, diffuseTint.y, diffuseTint.z, diffuseTint.w));
        }
        if (props.specularTint != -1)
        {
            Vector3 specularTint = GetShaderVector3(shader, material, props.specularTint, Vector3.one);
            unityMaterial.SetColor("_SpecularTint", new Color(specularTint.x, specularTint.y, specularTint.z, 1.0f));
        }
        if (props.emissiveTint != -1)
        {
            Vector3 emissiveTint = GetShaderVector3(shader, material, props.emissiveTint, Vector3.one);
            unityMaterial.SetColor("_EmissiveTint", new Color(emissiveTint.x, emissiveTint.y, emissiveTint.z, 1.0f));
        }
        if (props.secondaryDiffuseUvMult != -1)
            unityMaterial.SetFloat("_SecondaryDiffuseUvMult", GetShaderFloat(shader, material, props.secondaryDiffuseUvMult, 1.0f));
        if (props.secondaryDiffuseTint != -1)
        {
            Vector4 secondaryDiffuseTint = GetShaderVector4(shader, material, props.secondaryDiffuseTint, Vector4.one);
            unityMaterial.SetColor("_SecondaryDiffuseTint", new Color(secondaryDiffuseTint.x, secondaryDiffuseTint.y, secondaryDiffuseTint.z, secondaryDiffuseTint.w));
        }
        if (props.secondaryNormalUvMult != -1)
            unityMaterial.SetFloat("_SecondaryNormalUvMult", GetShaderFloat(shader, material, props.secondaryNormalUvMult, 1.0f));
        if (props.secondaryNormalMapStrength != -1)
            unityMaterial.SetFloat("_SecondaryNormalMapStrength", GetShaderFloat(shader, material, props.secondaryNormalMapStrength, 1.0f));
        if (props.specularUvMult != -1)
            unityMaterial.SetFloat("_SpecularUvMult", GetShaderFloat(shader, material, props.specularUvMult, 1.0f));
        if (props.specularPower != -1)
            unityMaterial.SetFloat("_SpecularPower", GetShaderFloat(shader, material, props.specularPower, 32.0f));
        if (props.secondarySpecularTint != -1)
        {
            Vector3 secondarySpecularTint = GetShaderVector3(shader, material, props.secondarySpecularTint, Vector3.one);
            unityMaterial.SetColor("_SecondarySpecularTint", new Color(secondarySpecularTint.x, secondarySpecularTint.y, secondarySpecularTint.z, 1.0f));
        }
        if (props.secondarySpecularUvMult != -1)
            unityMaterial.SetFloat("_SecondarySpecularUvMult", GetShaderFloat(shader, material, props.secondarySpecularUvMult, 1.0f));
        if (props.secondarySpecularPower != -1)
            unityMaterial.SetFloat("_SecondarySpecularPower", GetShaderFloat(shader, material, props.secondarySpecularPower, 32.0f));
        if (props.glassDensity != -1)
            unityMaterial.SetFloat("_GlassDensity", GetShaderFloat(shader, material, props.glassDensity, 1.0f));
        if (props.glassLightness != -1)
            unityMaterial.SetFloat("_GlassLightness", GetShaderFloat(shader, material, props.glassLightness, 1.0f));
        if (props.glassTint != -1)
        {
            Vector4 glassTint = GetShaderVector4(shader, material, props.glassTint, Vector4.one);
            unityMaterial.SetColor("_GlassTint", new Color(glassTint.x, glassTint.y, glassTint.z, glassTint.w));
        }
        if (props.diffuseRoughnessFactor != -1)
            unityMaterial.SetFloat("_DiffuseRoughnessFactor", GetShaderFloat(shader, material, props.diffuseRoughnessFactor, 1.0f));
        if (props.environmentEmissiveFactor != -1)
            unityMaterial.SetFloat("_EnvironmentEmissiveFactor", GetShaderFloat(shader, material, props.environmentEmissiveFactor, 1.0f));
        if (props.environmentMapMult != -1)
            unityMaterial.SetFloat("_EnvironmentMapMult", GetShaderFloat(shader, material, props.environmentMapMult, 1.0f));
        if (props.aoTint != -1)
        {
            Vector3 aoTint = GetShaderVector3(shader, material, props.aoTint, Vector3.one);
            unityMaterial.SetColor("_AoTint", new Color(aoTint.x, aoTint.y, aoTint.z, 1.0f));
        }
        if (props.ambientOcclusionMapMult != -1)
            unityMaterial.SetFloat("_AmbientOcclusionMapMult", GetShaderFloat(shader, material, props.ambientOcclusionMapMult, 1.0f));
        if (props.vertAoTint != -1)
        {
            Vector3 vertAoTint = GetShaderVector3(shader, material, props.vertAoTint, Vector3.one);
            unityMaterial.SetColor("_VertAoTint", new Color(vertAoTint.x, vertAoTint.y, vertAoTint.z, 1.0f));
        }
        if (props.dustUvMult != -1)
            unityMaterial.SetFloat("_DustUvMult", GetShaderFloat(shader, material, props.dustUvMult, 1.0f));
        if (props.dustFalloff != -1)
            unityMaterial.SetFloat("_DustFalloff", GetShaderFloat(shader, material, props.dustFalloff, 1.0f));
        if (props.ssrAmount != -1)
            unityMaterial.SetFloat("_SsrAmount", GetShaderFloat(shader, material, props.ssrAmount, 1.0f));
        if (props.furRimLightingFactor != -1)
            unityMaterial.SetFloat("_FurRimLightingFactor", GetShaderFloat(shader, material, props.furRimLightingFactor, 1.0f));
        if (props.parallaxUvMult != -1)
            unityMaterial.SetFloat("_ParallaxUvMult", GetShaderFloat(shader, material, props.parallaxUvMult, 1.0f));
        if (props.parallaxScale != -1)
            unityMaterial.SetFloat("_ParallaxScale", GetShaderFloat(shader, material, props.parallaxScale, 0.1f));
        if (props.parallaxBias != -1)
            unityMaterial.SetFloat("_ParallaxBias", GetShaderFloat(shader, material, props.parallaxBias, 0.02f));
        if (props.opacityModifierValue != -1)
            unityMaterial.SetFloat("_OpacityModifierValue", GetShaderFloat(shader, material, props.opacityModifierValue, 1.0f));
        if (props.alphablendNoiseUvMult != -1)
            unityMaterial.SetFloat("_AlphablendNoiseUvMult", GetShaderFloat(shader, material, props.alphablendNoiseUvMult, 1.0f));
        if (props.alphablendNoisePower != -1)
            unityMaterial.SetFloat("_AlphablendNoisePower", GetShaderFloat(shader, material, props.alphablendNoisePower, 1.0f));
        if (props.sparkleUvScale != -1)
            unityMaterial.SetFloat("_SparkleUvScale", GetShaderFloat(shader, material, props.sparkleUvScale, 1.0f));
        if (props.sparkleNormalBias != -1)
            unityMaterial.SetFloat("_SparkleNormalBias", GetShaderFloat(shader, material, props.sparkleNormalBias, 0.0f));
        if (props.sparkleMultiplier != -1)
            unityMaterial.SetFloat("_SparkleMultiplier", GetShaderFloat(shader, material, props.sparkleMultiplier, 1.0f));
        if (props.sparkleFadeStart != -1)
            unityMaterial.SetFloat("_SparkleFadeStart", GetShaderFloat(shader, material, props.sparkleFadeStart, 0.0f));
        if (props.sparklePower != -1)
            unityMaterial.SetFloat("_SparklePower", GetShaderFloat(shader, material, props.sparklePower, 1.0f));
        if (props.sparkleThreshold != -1)
            unityMaterial.SetFloat("_SparkleThreshold", GetShaderFloat(shader, material, props.sparkleThreshold, 0.5f));
        if (props.dirtBlendMultSpecPower != -1)
            unityMaterial.SetFloat("_DirtBlendMultSpecPower", GetShaderFloat(shader, material, props.dirtBlendMultSpecPower, 1.0f));
        if (props.dirtUvMult != -1)
            unityMaterial.SetFloat("_DirtUvMult", GetShaderFloat(shader, material, props.dirtUvMult, 1.0f));
        if (props.dirtAoAmount != -1)
            unityMaterial.SetFloat("_DirtAoAmount", GetShaderFloat(shader, material, props.dirtAoAmount, 1.0f));
        if (props.wetLevel != -1)
            unityMaterial.SetFloat("_WetLevel", GetShaderFloat(shader, material, props.wetLevel, 0.0f));
        if (props.wetnessUvMult != -1)
            unityMaterial.SetFloat("_WetnessUvMult", GetShaderFloat(shader, material, props.wetnessUvMult, 1.0f));
        if (props.customTintColour != -1)
        {
            Vector3 customTintColour = GetShaderVector3(shader, material, props.customTintColour, Vector3.one);
            unityMaterial.SetColor("_CustomTintColour", new Color(customTintColour.x, customTintColour.y, customTintColour.z, 1.0f));
        }
        if (props.tessellationFactor != -1)
            unityMaterial.SetFloat("_TessellationFactor", GetShaderFloat(shader, material, props.tessellationFactor, 1.0f));
        if (props.minTessellationDistance != -1)
            unityMaterial.SetFloat("_MinTessellationDistance", GetShaderFloat(shader, material, props.minTessellationDistance, 1.0f));
        if (props.tessellationRange != -1)
            unityMaterial.SetFloat("_TessellationRange", GetShaderFloat(shader, material, props.tessellationRange, 10.0f));
        if (props.shapeFactor != -1)
            unityMaterial.SetFloat("_ShapeFactor", GetShaderFloat(shader, material, props.shapeFactor, 1.0f));
        if (props.displacementFactor != -1)
            unityMaterial.SetFloat("_DisplacementFactor", GetShaderFloat(shader, material, props.displacementFactor, 1.0f));
        if (props.displacementMapUvScale != -1)
            unityMaterial.SetFloat("_DisplacementMapUvScale", GetShaderFloat(shader, material, props.displacementMapUvScale, 1.0f));

        if (props.emissive != -1)
            unityMaterial.SetFloat("_Emissive", (shader.UbershaderFeatureFlags & (1L << props.emissive)) != 0 ? 1.0f : 0.0f);
        if (props.normalMapping != -1)
            unityMaterial.SetFloat("_NormalMapping", (shader.UbershaderFeatureFlags & (1L << props.normalMapping)) != 0 ? 1.0f : 0.0f);
        if (props.separateAlpha != -1)
            unityMaterial.SetFloat("_SeparateAlpha", (shader.UbershaderFeatureFlags & (1L << props.separateAlpha)) != 0 ? 1.0f : 0.0f);
        if (props.separateAlphaUseGreen != -1)
            unityMaterial.SetFloat("_SeparateAlphaMapUseGreenChannel", (shader.UbershaderFeatureFlags & (1L << props.separateAlphaUseGreen)) != 0 ? 1.0f : 0.0f);
        if (props.vertexColour != -1)
            unityMaterial.SetFloat("_VertexColour", (shader.UbershaderFeatureFlags & (1L << props.vertexColour)) != 0 ? 1.0f : 0.0f);
        if (props.fogAlpha != -1)
            unityMaterial.SetFloat("_FogAlpha", (shader.UbershaderFeatureFlags & (1L << props.fogAlpha)) != 0 ? 1.0f : 0.0f);
        if (props.reflectivePlastic != -1)
            unityMaterial.SetFloat("_ReflectivePlastic", (shader.UbershaderFeatureFlags & (1L << props.reflectivePlastic)) != 0 ? 1.0f : 0.0f);
        if (props.doubleSided != -1)
            unityMaterial.SetFloat("_DoubleSided", (shader.UbershaderFeatureFlags & (1L << props.doubleSided)) != 0 ? 1.0f : 0.0f);
        if (props.useAlphaAsBlendFactor != -1)
            unityMaterial.SetFloat("_UseAlphaAsBlendFactor", (shader.UbershaderFeatureFlags & (1L << props.useAlphaAsBlendFactor)) != 0 ? 1.0f : 0.0f);
        if (props.forceToAlpha != -1)
            unityMaterial.SetFloat("_ForceToAlpha", (shader.UbershaderFeatureFlags & (1L << props.forceToAlpha)) != 0 ? 1.0f : 0.0f);
        if (props.alphaTest != -1)
            unityMaterial.SetFloat("_AlphaTest", (shader.UbershaderFeatureFlags & (1L << props.alphaTest)) != 0 ? 1.0f : 0.0f);
        if (props.textureLodBiasNone != -1)
            unityMaterial.SetFloat("_TextureLodBiasNone", (shader.UbershaderFeatureFlags & (1L << props.textureLodBiasNone)) != 0 ? 1.0f : 0.0f);
        if (props.textureLodBiasSlight != -1)
            unityMaterial.SetFloat("_TextureLodBiasSlight", (shader.UbershaderFeatureFlags & (1L << props.textureLodBiasSlight)) != 0 ? 1.0f : 0.0f);
        if (props.textureLodBiasHigh != -1)
            unityMaterial.SetFloat("_TextureLodBiasHigh", (shader.UbershaderFeatureFlags & (1L << props.textureLodBiasHigh)) != 0 ? 1.0f : 0.0f);
        if (props.planarReflective != -1)
            unityMaterial.SetFloat("_PlanarReflective", (shader.UbershaderFeatureFlags & (1L << props.planarReflective)) != 0 ? 1.0f : 0.0f);
        if (props.signedDistanceField != -1)
            unityMaterial.SetFloat("_SignedDistanceField", (shader.UbershaderFeatureFlags & (1L << props.signedDistanceField)) != 0 ? 1.0f : 0.0f);
        if (props.diffuseMappingParallax != -1)
            unityMaterial.SetFloat("_DiffuseMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.diffuseMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.secondaryDiffuseMapping != -1)
            unityMaterial.SetFloat("_SecondaryDiffuseMapping", (shader.UbershaderFeatureFlags & (1L << props.secondaryDiffuseMapping)) != 0 ? 1.0f : 0.0f);
        if (props.secondaryDiffuseBlendMultiply != -1)
            unityMaterial.SetFloat("_SecondaryDiffuseBlendMultiply", (shader.UbershaderFeatureFlags & (1L << props.secondaryDiffuseBlendMultiply)) != 0 ? 1.0f : 0.0f);
        if (props.normalMappingParallax != -1)
            unityMaterial.SetFloat("_NormalMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.normalMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.secondaryNormalMapping != -1)
            unityMaterial.SetFloat("_SecondaryNormalMapping", (shader.UbershaderFeatureFlags & (1L << props.secondaryNormalMapping)) != 0 ? 1.0f : 0.0f);
        if (props.secondaryNormalBlendAdd != -1)
            unityMaterial.SetFloat("_SecondaryNormalBlendAdd", (shader.UbershaderFeatureFlags & (1L << props.secondaryNormalBlendAdd)) != 0 ? 1.0f : 0.0f);
        if (props.specularMapping != -1)
            unityMaterial.SetFloat("_SpecularMapping", (shader.UbershaderFeatureFlags & (1L << props.specularMapping)) != 0 ? 1.0f : 0.0f);
        if (props.specularMappingParallax != -1)
            unityMaterial.SetFloat("_SpecularMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.specularMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.secondarySpecularMapping != -1)
            unityMaterial.SetFloat("_SecondarySpecularMapping", (shader.UbershaderFeatureFlags & (1L << props.secondarySpecularMapping)) != 0 ? 1.0f : 0.0f);
        if (props.secondarySpecularMappingParallax != -1)
            unityMaterial.SetFloat("_SecondarySpecularMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.secondarySpecularMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.secondarySpecularBlendMultiply != -1)
            unityMaterial.SetFloat("_SecondarySpecularBlendMultiply", (shader.UbershaderFeatureFlags & (1L << props.secondarySpecularBlendMultiply)) != 0 ? 1.0f : 0.0f);
        if (props.glass != -1)
            unityMaterial.SetFloat("_Glass", (shader.UbershaderFeatureFlags & (1L << props.glass)) != 0 ? 1.0f : 0.0f);
        if (props.diffuseRoughness != -1)
            unityMaterial.SetFloat("_DiffuseRoughness", (shader.UbershaderFeatureFlags & (1L << props.diffuseRoughness)) != 0 ? 1.0f : 0.0f);
        if (props.frontRoughness != -1)
            unityMaterial.SetFloat("_FrontRoughness", (shader.UbershaderFeatureFlags & (1L << props.frontRoughness)) != 0 ? 1.0f : 0.0f);
        if (props.additiveRoughness != -1)
            unityMaterial.SetFloat("_AdditiveRoughness", (shader.UbershaderFeatureFlags & (1L << props.additiveRoughness)) != 0 ? 1.0f : 0.0f);
        if (props.environmentMapping != -1)
            unityMaterial.SetFloat("_EnvironmentMapping", (shader.UbershaderFeatureFlags & (1L << props.environmentMapping)) != 0 ? 1.0f : 0.0f);
        if (props.ambientOcclusionMapping != -1)
            unityMaterial.SetFloat("_AmbientOcclusionMapping", (shader.UbershaderFeatureFlags & (1L << props.ambientOcclusionMapping)) != 0 ? 1.0f : 0.0f);
        if (props.ambientOcclusionUV != -1)
            unityMaterial.SetFloat("_AmbientOcclusionUV", (shader.UbershaderFeatureFlags & (1L << props.ambientOcclusionUV)) != 0 ? 1.0f : 0.0f);
        if (props.vertexAmbientOcclusion != -1)
            unityMaterial.SetFloat("_VertexAmbientOcclusion", (shader.UbershaderFeatureFlags & (1L << props.vertexAmbientOcclusion)) != 0 ? 1.0f : 0.0f);
        if (props.dustMapping != -1)
            unityMaterial.SetFloat("_DustMapping", (shader.UbershaderFeatureFlags & (1L << props.dustMapping)) != 0 ? 1.0f : 0.0f);
        if (props.dustMappingParallax != -1)
            unityMaterial.SetFloat("_DustMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.dustMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.ssr != -1)
            unityMaterial.SetFloat("_SSR", (shader.UbershaderFeatureFlags & (1L << props.ssr)) != 0 ? 1.0f : 0.0f);
        if (props.irradianceCube != -1)
            unityMaterial.SetFloat("_IrradianceCube", (shader.UbershaderFeatureFlags & (1L << props.irradianceCube)) != 0 ? 1.0f : 0.0f);
        if (props.radiosityDynamic != -1)
            unityMaterial.SetFloat("_RadiosityDynamic", (shader.UbershaderFeatureFlags & (1L << props.radiosityDynamic)) != 0 ? 1.0f : 0.0f);
        if (props.furRimLighting != -1)
            unityMaterial.SetFloat("_FurRimLighting", (shader.UbershaderFeatureFlags & (1L << props.furRimLighting)) != 0 ? 1.0f : 0.0f);
        if (props.parallaxMapping != -1)
            unityMaterial.SetFloat("_ParallaxMapping", (shader.UbershaderFeatureFlags & (1L << props.parallaxMapping)) != 0 ? 1.0f : 0.0f);
        if (props.decal != -1)
            unityMaterial.SetFloat("_Decal", (shader.UbershaderFeatureFlags & (1L << props.decal)) != 0 ? 1.0f : 0.0f);
        if (props.decalDiffuse != -1)
            unityMaterial.SetFloat("_DecalDiffuse", (shader.UbershaderFeatureFlags & (1L << props.decalDiffuse)) != 0 ? 1.0f : 0.0f);
        if (props.decalNormal != -1)
            unityMaterial.SetFloat("_DecalNormal", (shader.UbershaderFeatureFlags & (1L << props.decalNormal)) != 0 ? 1.0f : 0.0f);
        if (props.decalSpecularEmissive != -1)
            unityMaterial.SetFloat("_DecalSpecularEmissive", (shader.UbershaderFeatureFlags & (1L << props.decalSpecularEmissive)) != 0 ? 1.0f : 0.0f);
        if (props.specularMappingMetalnessMasking != -1)
            unityMaterial.SetFloat("_SpecularMappingMetalnessMasking", (shader.UbershaderFeatureFlags & (1L << props.specularMappingMetalnessMasking)) != 0 ? 1.0f : 0.0f);
        if (props.alphablendNoise != -1)
            unityMaterial.SetFloat("_AlphablendNoise", (shader.UbershaderFeatureFlags & (1L << props.alphablendNoise)) != 0 ? 1.0f : 0.0f);
        if (props.alphaLighting != -1)
            unityMaterial.SetFloat("_AlphaLighting", (shader.UbershaderFeatureFlags & (1L << props.alphaLighting)) != 0 ? 1.0f : 0.0f);
        if (props.sparkle != -1)
            unityMaterial.SetFloat("_Sparkle", (shader.UbershaderFeatureFlags & (1L << props.sparkle)) != 0 ? 1.0f : 0.0f);
        if (props.radiosityStatic != -1)
            unityMaterial.SetFloat("_RadiosityStatic", (shader.UbershaderFeatureFlags & (1L << props.radiosityStatic)) != 0 ? 1.0f : 0.0f);
        if (props.dirtBlendMultiply != -1)
            unityMaterial.SetFloat("_DirtBlendMultiply", (shader.UbershaderFeatureFlags & (1L << props.dirtBlendMultiply)) != 0 ? 1.0f : 0.0f);
        if (props.dirtMappingParallax != -1)
            unityMaterial.SetFloat("_DirtMappingParallax", (shader.UbershaderFeatureFlags & (1L << props.dirtMappingParallax)) != 0 ? 1.0f : 0.0f);
        if (props.wetness != -1)
            unityMaterial.SetFloat("_Wetness", (shader.UbershaderFeatureFlags & (1L << props.wetness)) != 0 ? 1.0f : 0.0f);
        if (props.hiLodCustomCharacterCorpseConstants != -1)
            unityMaterial.SetFloat("_HiLodCustomCharacterCorpseConstants", (shader.UbershaderFeatureFlags & (1L << props.hiLodCustomCharacterCorpseConstants)) != 0 ? 1.0f : 0.0f);
        if (props.noClip != -1)
            unityMaterial.SetFloat("_NoClip", (shader.UbershaderFeatureFlags & (1L << props.noClip)) != 0 ? 1.0f : 0.0f);
        if (props.tessellation != -1)
            unityMaterial.SetFloat("_Tessellation", (shader.UbershaderFeatureFlags & (1L << props.tessellation)) != 0 ? 1.0f : 0.0f);
        if (props.orientationAdaptiveTessellation != -1)
            unityMaterial.SetFloat("_OrientationAdaptiveTessellation", (shader.UbershaderFeatureFlags & (1L << props.orientationAdaptiveTessellation)) != 0 ? 1.0f : 0.0f);
        if (props.phongTessellation != -1)
            unityMaterial.SetFloat("_PhongTessellation", (shader.UbershaderFeatureFlags & (1L << props.phongTessellation)) != 0 ? 1.0f : 0.0f);
        if (props.displacementMapping != -1)
            unityMaterial.SetFloat("_DisplacementMapping", (shader.UbershaderFeatureFlags & (1L << props.displacementMapping)) != 0 ? 1.0f : 0.0f);
    }

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
                case 7:
                    TexturesPAK = new Textures(renderablePath + "LEVEL_TEXTURES.ALL.PAK");
                    break;
                case 8:
                    TexturesPAK_GLOBAL = new Textures(aiPath + "/DATA/ENV/GLOBAL/WORLD/GLOBAL_TEXTURES.ALL.PAK");
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