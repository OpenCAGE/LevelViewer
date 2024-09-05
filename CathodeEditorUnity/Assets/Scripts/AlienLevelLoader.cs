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
using UnityEditor.Experimental.GraphView;
using CATHODE.Scripting.Internal;
using UnityEngine.UIElements;
using System;
using Newtonsoft.Json;
using System.Reflection;
using UnityEditor;
using System.Collections;
using CATHODE.EXPERIMENTAL;
using System.Text.RegularExpressions;
using UnityEditor.PackageManager.UI;
using static CATHODE.Textures.TEX4;
using System.Xml;
using static CATHODE.Materials.Material;

public class AlienLevelLoader : MonoBehaviour
{
    //Support soon for combined Commands and Mover - but for now, lets let people toggle 
    [Tooltip("Enable this option to load data from the MVR file, which will apply additional instance-specific properties, such as cubemaps and texture overrides. Toggle this setting before hitting play.")]
    [SerializeField] private bool _loadMoverData = false;

    [Tooltip("Enable this to include objects in the scene that are of an unsupported material type (they will still be inactive by default).")]
    [SerializeField] private bool _populateObjectsWithUnsupportedMaterials = false;

    public Action OnLoaded;

    private string _levelName = "";
    public string LevelName => _levelName;

    private GameObject _loadedCompositeGO = null;
    private Composite _loadedComposite = null;
    public string CompositeIDString => _loadedComposite == null || _loadedComposite.shortGUID.val == null ? "" : _loadedComposite.shortGUID.ToByteString();
    public string CompositeName => _loadedComposite == null ? "" : _loadedComposite.name;

    private LevelContent _levelContent = null;
    private Textures _globalTextures = null;

    private Dictionary<int, TexOrCube> _texturesGlobal = new Dictionary<int, TexOrCube>();
    private Dictionary<int, TexOrCube> _texturesLevel = new Dictionary<int, TexOrCube>();
    private Dictionary<string, TexOrCube> _texturesGobo = new Dictionary<string, TexOrCube>();
    private Dictionary<int, Material> _materials = new Dictionary<int, Material>();
    private Dictionary<Material, bool> _materialSupport = new Dictionary<Material, bool>();
    private Dictionary<int, GameObjectHolder> _modelGOs = new Dictionary<int, GameObjectHolder>();

    private List<ReflectionProbe> _envMaps = new List<ReflectionProbe>();

    public class TexOrCube
    {
        public Texture2D Texture = null;
        public Cubemap Cubemap = null;
    }

    private WebsocketClient _client;

    IEnumerator Start()
    {
        Debug.Log("Starting AlienLevelLoader");

        yield return new WaitForEndOfFrame();
    }

    private void Awake()
    {
        Debug.Log("Awaking AlienLevelLoader");
        _client = GetComponent<WebsocketClient>();
    }

    private void ResetLevel()
    {
        if (_loadedCompositeGO != null)
            Destroy(_loadedCompositeGO);

        _texturesGlobal.Clear();
        _texturesLevel.Clear();
        _materials.Clear();
        _materialSupport.Clear();
        _modelGOs.Clear();
        _envMaps.Clear();

        _levelContent = null;

        if (_globalTextures == null && _client)
            _globalTextures = new Textures(_client.PathToAI + "/DATA/ENV/GLOBAL/WORLD/GLOBAL_TEXTURES.ALL.PAK");
    }

    public void LoadLevel(string level)
    {
        Debug.Log("Loading level " + level + "...");

        ResetLevel();

        _levelName = level;

        _levelContent = new LevelContent(_client.PathToAI, level);

        //Load cubemaps to reflection probes
        List<Textures.TEX4> cubemaps = _levelContent.LevelTextures.Entries.Where(o => o.Type == Textures.AlienTextureType.ENVIRONMENT_MAP).ToList();
        GameObject probeHolder = new GameObject("Reflection Probes");
        for (int i = 0; i < cubemaps.Count; i++)
        {
            Cubemap cubemap = GetCubemap(_levelContent.LevelTextures.GetWriteIndex(cubemaps[i]), false);
            ReflectionProbe probe = new GameObject(cubemaps[i].Name).AddComponent<ReflectionProbe>();
            probe.transform.parent = probeHolder.transform;
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
            probe.customBakedTexture = cubemap;
            _envMaps.Add(probe);
        }

        if (_loadMoverData)
        {
            LoadMVR();
        }

        // Focus on SceneView, close GameView
        try
        {
            SceneView.FocusWindowIfItsOpen(typeof(SceneView));
            EditorWindow.GetWindow(typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView")).Close();
        }
        catch { }
    }
    public void LoadComposite(ShortGuid guid)
    {
        if (_loadMoverData || _levelContent == null) return;

        if (_loadedCompositeGO != null)
            Destroy(_loadedCompositeGO);
        _loadedCompositeGO = new GameObject(_levelName);
        Selection.activeGameObject = _loadedCompositeGO;

        Composite comp = _levelContent.CommandsPAK.GetComposite(guid);
        Debug.Log("Loading composite " + comp?.name + "...");
        LoadCommands(comp);

        OnLoaded?.Invoke();
    }

    /* Load MVR data */
    private void LoadMVR()
    {
        _loadedCompositeGO = new GameObject(_levelName);
        Selection.activeGameObject = _loadedCompositeGO;

        for (int i = 0; i < _levelContent.ModelsMVR.Entries.Count; i++)
        {
            GameObject thisParent = new GameObject("MVR: " + i + "/" + _levelContent.ModelsMVR.Entries[i].renderableElementIndex + "/" + _levelContent.ModelsMVR.Entries[i].renderableElementCount);
            Matrix4x4 m = _levelContent.ModelsMVR.Entries[i].transform;
            thisParent.transform.position = m.GetColumn(3);
            thisParent.transform.rotation = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
            thisParent.transform.localScale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
            thisParent.transform.parent = _loadedCompositeGO.transform;
            for (int x = 0; x < _levelContent.ModelsMVR.Entries[i].renderableElementCount; x++)
            {
                RenderableElements.Element RenderableElement = _levelContent.RenderableREDS.Entries[(int)_levelContent.ModelsMVR.Entries[i].renderableElementIndex + x];
                MeshRenderer renderer = SpawnModel(RenderableElement.ModelIndex, RenderableElement.MaterialIndex, thisParent);
                if (renderer != null && _levelContent.ModelsMVR.Entries[i].environmentMapIndex != -1)
                {
                    renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.BlendProbes;
                    int index = _levelContent.EnvironmentMap.Entries[_levelContent.ModelsMVR.Entries[i].environmentMapIndex].EnvMapIndex;
                    if (index != -1)
                        renderer.probeAnchor = _envMaps[index]?.transform;
                }
            }
        }

        OnLoaded?.Invoke();
    }

    /* Load Commands data */
    private void LoadCommands(Composite composite)
    {
        _loadedComposite = composite;
        ParseComposite(composite, _loadedCompositeGO, Vector3.zero, Quaternion.identity, new List<AliasEntity>());
    }

    void ParseComposite(Composite composite, GameObject parentGO, Vector3 parentPos, Quaternion parentRot, List<AliasEntity> aliases)
    {
        if (composite == null) return;
        GameObject compositeGO = new GameObject("[COMPOSITE INSTANCE] " + composite.name);
        compositeGO.transform.parent = parentGO.transform;
        compositeGO.transform.SetLocalPositionAndRotation(parentPos, parentRot);

        //Compile all appropriate overrides, and keep the hierarchies trimmed so that index zero is accurate to this composite
        List<AliasEntity> trimmedAliases = new List<AliasEntity>();
        for (int i = 0; i < aliases.Count; i++)
        {
            aliases[i].alias.path.RemoveAt(0);
            if (aliases[i].alias.path.Count != 0)
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
                    AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Count == (o.alias.path[o.alias.path.Count - 1] == ShortGuid.Invalid ? 2 : 1) && o.alias.path[0] == function.shortGUID);
                    if (!GetEntityTransform(ovrride, out position, out rotation))
                        GetEntityTransform(function, out position, out rotation);

                    //Continue
                    ParseComposite(compositeNext, compositeGO, position, Quaternion.Euler(rotation), overridesNext);
                }
            }

            //Parse model data
            else
            {
                FunctionType currentFunctionType = CommandsUtils.GetFunctionType(function.function);

                if (currentFunctionType == FunctionType.ModelReference || currentFunctionType == FunctionType.LightReference)
                {
                    //Work out our position, accounting for overrides
                    Vector3 position, rotation;
                    AliasEntity ovrride = trimmedAliases.FirstOrDefault(o => o.alias.path.Count == 1 && o.alias.path[0] == function.shortGUID);
                    if (!GetEntityTransform(ovrride, out position, out rotation))
                        GetEntityTransform(function, out position, out rotation);

                    GameObject nodeModel = new GameObject("[FUNCTION ENTITY] [" + currentFunctionType + "] " + function.shortGUID.ToByteString());
                    nodeModel.transform.parent = compositeGO.transform;
                    nodeModel.transform.SetLocalPositionAndRotation(position, Quaternion.Euler(rotation));

                    if (currentFunctionType == FunctionType.ModelReference)
                    {
                        ProcessModelReference(function, nodeModel);
                    } else if (currentFunctionType == FunctionType.LightReference)
                    {
                        ProcessLightReference(function, nodeModel);
                    }
                }
            }
        }
    }

    void ProcessModelReference(FunctionEntity function, GameObject nodeModel)
    {
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
                            switch (resourceRef.entryType)
                            {
                                case ResourceType.RENDERABLE_INSTANCE:
                                    SpawnModel(renderable.ModelIndex, renderable.MaterialIndex, nodeModel);
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
    void ProcessLightReference(FunctionEntity function, GameObject nodeModel)
    {

        Parameter typeParam = function.GetParameter("type");
        Parameter colourParam = function.GetParameter("colour");
        Parameter intensityMultParam = function.GetParameter("intensity_multiplier");
        Parameter outerConeAngle = function.GetParameter("outer_cone_angle");
        Parameter endAttenuation = function.GetParameter("end_attenuation");
        Parameter goboTextureParam = function.GetParameter("gobo_texture");
        Parameter stripLengthParam = function.GetParameter("strip_length");
        Parameter aspectRatioParam = function.GetParameter("aspect_ratio");
        
        // Only process known light types
        if (typeParam != null && typeParam.content != null)
        {

            GameObject newLightSpawn = new GameObject();
            if (nodeModel != null) newLightSpawn.transform.parent = nodeModel.transform;

            newLightSpawn.transform.localPosition = Vector3.zero;
            newLightSpawn.transform.localRotation = Quaternion.identity;

            Light lightComp = newLightSpawn.AddComponent<Light>();

            switch (typeParam.content.dataType)
            {
                case DataType.ENUM:

                    cEnum lightType = (cEnum)typeParam.content;

                    switch (lightType.enumIndex)
                    {
                        // Omni
                        case 0:
                            newLightSpawn.name = "[LIGHT][OMNI]";
                            lightComp.type = LightType.Point;
                            break;
                        // Spot
                        case 1:
                            newLightSpawn.name = "[LIGHT][SPOT]";
                            lightComp.type = LightType.Spot;
                            break;
                        // Strip
                        case 2:
                            newLightSpawn.name = "[LIGHT][STRIP]";
                            lightComp.type = LightType.Area;
                            break;
                    }

                    break;
            }

            if (colourParam?.content != null)
            {
                cVector3 color = (cVector3)colourParam.content;
                lightComp.color = new Color(color.value.x / 255, color.value.y / 255, color.value.z / 255);
            }

            if (endAttenuation?.content != null) lightComp.range = ((cFloat)endAttenuation.content).value;

            // Only spotlight
            if (outerConeAngle?.content != null) lightComp.spotAngle = ((cFloat)outerConeAngle.content).value;

            if (goboTextureParam?.content != null) 
            {
                lightComp.cookie = GetGoboTexture(((cString)goboTextureParam.content).value); 

                if (lightComp.cookie != null) newLightSpawn.name += "[GOBO:" + lightComp.cookie.name + "]";
            }

            if (intensityMultParam?.content != null) lightComp.intensity = ((cFloat)intensityMultParam.content).value;

            if (stripLengthParam?.content != null)
            {
                float stripLength = ((cFloat)stripLengthParam.content).value;

                if (aspectRatioParam?.content != null)
                {
                    float stipWidth = ((cFloat)aspectRatioParam.content).value * stripLength;
                    lightComp.areaSize = new Vector2(stripLength, stipWidth);
                }
                else
                {
                    lightComp.areaSize = new Vector2(stripLength, 1);
                }
            }

            newLightSpawn.name += " " + nodeModel.name;
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

    /* Force select the function entity if someone clicks on the resource */
    private GameObject _prevSelection = null;
    private void Update()
    {
        if (_prevSelection != Selection.activeGameObject)
        {
            if (Selection.activeGameObject != null)
            {
                string entityName = Selection.activeGameObject.name;
                if (entityName.Length > ("[RESOURCE]").Length && entityName.Substring(0, ("[RESOURCE]").Length) == "[RESOURCE]")
                {
                    Selection.activeGameObject = Selection.activeGameObject.transform.parent.transform.parent.gameObject;
                }
            }
            _prevSelection = Selection.activeGameObject;
        }
    }

    #region Asset Handlers
    private MeshRenderer SpawnModel(int binIndex, int mtlIndex, GameObject parent)
    {
        GameObjectHolder holder = GetModel(binIndex);
        if (holder == null)
        {
            Debug.Log("Attempted to load non-parsed model (" + binIndex + "). Skipping!");
            return null;
        }

        Material material = GetMaterial((mtlIndex == -1) ? holder.DefaultMaterial : mtlIndex);
        if (!_populateObjectsWithUnsupportedMaterials && !_materialSupport[material])
            return null;

        //Hack: we spawn the resource in a child of the GameObject hidden in the hierarchy, so that it's selectable in editor still
        GameObject newModelSpawnParent = new GameObject();
        if (parent != null) newModelSpawnParent.transform.parent = parent.transform;
        newModelSpawnParent.transform.localPosition = Vector3.zero;
        newModelSpawnParent.transform.localRotation = Quaternion.identity;
        newModelSpawnParent.name = "[RESOURCE] " + holder.Name;
        newModelSpawnParent.hideFlags = HideFlags.HideInHierarchy;

        GameObject newModelSpawn = new GameObject();
        newModelSpawn.transform.parent = newModelSpawnParent.transform;
        newModelSpawn.transform.localPosition = Vector3.zero;
        newModelSpawn.transform.localRotation = Quaternion.identity;
        newModelSpawn.name = newModelSpawnParent.name;
        newModelSpawn.AddComponent<MeshFilter>().sharedMesh = holder.MainMesh;
        MeshRenderer renderer = newModelSpawn.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        newModelSpawn.SetActive(_materialSupport[material]);

        //todo apply mvr colour scale here

        return renderer;
    }

    private Texture2D GetTexture(int index, bool global)
    {
        return GetTexOrCube(index, global)?.Texture;
    }
    private Cubemap GetCubemap(int index, bool global)
    {
        return GetTexOrCube(index, global)?.Cubemap;
    }

    private Texture2D GetGoboTexture(string texPath)
    {
        Texture2D returnTexture = null;

        if (_texturesGobo.ContainsKey(texPath))
        {
            returnTexture = _texturesGobo[texPath]?.Texture;
        }
        else if (!string.IsNullOrEmpty(texPath))
        {
            var regex = new Regex(@".*\\(.*\.(dds|tga))");
            var match = regex.Match(texPath);
            string texName = "";

            if (match.Success)
                texName = "gobo\\" + match.Groups[1].Value.ToLower();

            Textures.TEX4 matchingTexture = _levelContent.LevelTextures.Entries.FirstOrDefault(texture => texture.Name.Equals(texName));

            // GOBOs seem to be in BGR24 (unsupported as is), forcing format to DXT1 allows them to be loaded
            TexOrCube texOrCube = LoadTexOrCube(matchingTexture, TextureWrapMode.Clamp, UnityEngine.TextureFormat.DXT1.ToString());

            if (texOrCube != null)
            {
                _texturesGobo.Add(texPath, texOrCube);
                returnTexture = texOrCube?.Texture;
            }
        }

        return returnTexture;
    }

    private TexOrCube GetTexOrCube(int index, bool global)
    {
        if ((global && !_texturesGlobal.ContainsKey(index)) || (!global && !_texturesLevel.ContainsKey(index)))
        {
            Textures.TEX4 InTexture = (global ? _globalTextures : _levelContent.LevelTextures).GetAtWriteIndex(index);

            TexOrCube tex = LoadTexOrCube(InTexture);

            if (global)
                _texturesGlobal.Add(index, tex);
            else
                _texturesLevel.Add(index, tex);
        }

        if (global)
            return _texturesGlobal[index];
        else
            return _texturesLevel[index];
    }

    private TexOrCube LoadTexOrCube(Textures.TEX4 InTexture) {
        
        return LoadTexOrCube(InTexture, TextureWrapMode.Repeat, null);
    }
    private TexOrCube LoadTexOrCube(Textures.TEX4 InTexture, TextureWrapMode wrapMode, string formatOverride)
    {
        if (InTexture == null) return null;
        Textures.TEX4.Part TexPart = InTexture.tex_HighRes;

        Vector2 textureDims;
        int textureLength = 0;
        int mipLevels = 0;

        // Check for content validity - fallback on lowres if highrest doesn't exist
        if (TexPart.Content == null || TexPart.Content.Length == 0)
        {
            TexPart = InTexture.tex_LowRes;

            if (TexPart.Content == null || TexPart.Content.Length == 0)
            {
                Debug.LogWarning("Found a 0-length texture - skipping");
                return null;
            }
        }

        textureDims = new Vector2(TexPart.Width, TexPart.Height);
        textureLength = TexPart.Content.Length;
        mipLevels = TexPart.MipLevels;

        UnityEngine.TextureFormat format = UnityEngine.TextureFormat.BC7;
        
        if (string.IsNullOrEmpty(formatOverride)) {
        
            switch (InTexture.Format)
            {
                case Textures.TextureFormat.DXGI_FORMAT_BC1_UNORM:
                    format = UnityEngine.TextureFormat.DXT1;
                    break;
                case Textures.TextureFormat.DXGI_FORMAT_BC3_UNORM:
                    format = UnityEngine.TextureFormat.DXT5;
                    break;
                case Textures.TextureFormat.DXGI_FORMAT_BC5_UNORM:
                    format = UnityEngine.TextureFormat.BC5;
                    break;
                case Textures.TextureFormat.DXGI_FORMAT_BC7_UNORM:
                    format = UnityEngine.TextureFormat.BC7;
                    break;
                case Textures.TextureFormat.DXGI_FORMAT_B8G8R8_UNORM:
                    //Debug.LogWarning("BGR24 UNSUPPORTED!");
                    return null;
                case Textures.TextureFormat.DXGI_FORMAT_B8G8R8A8_UNORM:
                    format = UnityEngine.TextureFormat.BGRA32;
                    break;
            }
        } 
        else
        {
            format = (UnityEngine.TextureFormat)Enum.Parse(typeof(UnityEngine.TextureFormat), formatOverride);
        }

        TexOrCube tex = new TexOrCube();
        using (BinaryReader tempReader = new BinaryReader(new MemoryStream(TexPart.Content)))
        {
            switch (InTexture.Type)
            {
                case Textures.AlienTextureType.ENVIRONMENT_MAP:
                    tex.Cubemap = new Cubemap((int)textureDims.x, format, false);
                    tex.Cubemap.name = InTexture.Name;
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveX);
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeX);
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveY);
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeY);
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.PositiveZ);
                    tex.Cubemap.SetPixelData(tempReader.ReadBytes(textureLength / 6), 0, CubemapFace.NegativeZ);
                    tex.Cubemap.Apply(false, true);
                    break;
                default:
                    tex.Texture = new Texture2D((int)textureDims[0], (int)textureDims[1], format, mipLevels, true);
                    tex.Texture.name = InTexture.Name;
                    tex.Texture.LoadRawTextureData(tempReader.ReadBytes(textureLength));
                    tex.Texture.wrapMode = wrapMode;
                    tex.Texture.Apply();
                    break;
            }
        }

        return tex;
    }

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
            int RemappedIndex = _levelContent.ShadersIDXRemap.Datas[InMaterial.UberShaderIndex].Index;
            ShadersPAK.ShaderEntry Shader = _levelContent.ShadersPAK.Shaders[RemappedIndex];

            Material toReturn = new Material(UnityEngine.Shader.Find("Standard"));
            toReturn.name = InMaterial.Name;

            ShaderMaterialMetadata metadata = _levelContent.ShadersPAK.GetMaterialMetadataFromShader(InMaterial, _levelContent.ShadersIDXRemap);

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

            List<Texture2D> availableTextures = new List<Texture2D>();
            for (int SlotIndex = 0; SlotIndex < Shader.Header.TextureLinkCount; ++SlotIndex)
            {
                int PairIndex = Shader.TextureLinks[SlotIndex];
                // NOTE: PairIndex == 255 means no index.
                if (PairIndex < InMaterial.TextureReferences.Length)
                {
                    Materials.Material.Texture Pair = InMaterial.TextureReferences[PairIndex];
                    availableTextures.Add(Pair.BinIndex == -1 ? null : GetTexture(Pair.BinIndex, Pair.Source == Materials.Material.Texture.TextureSource.GLOBAL));
                }
                else
                {
                    availableTextures.Add(null);
                }
            }

            //Apply materials
            for (int i = 0; i < metadata.textures.Count; i++)
            {
                if (i >= availableTextures.Count) continue;
                switch (metadata.textures[i].Type)
                {
                    case ShaderSlot.DIFFUSE_MAP:
                        toReturn.SetTexture("_MainTex", availableTextures[i]);
                        break;
                    case ShaderSlot.DETAIL_MAP:
                        toReturn.EnableKeyword("_DETAIL_MULX2");
                        toReturn.SetTexture("_DetailMask", availableTextures[i]);
                        break;
                    case ShaderSlot.EMISSIVE:
                        toReturn.EnableKeyword("_EMISSION");
                        toReturn.SetTexture("_EmissionMap", availableTextures[i]);
                        break;
                    case ShaderSlot.PARALLAX_MAP:
                        toReturn.EnableKeyword("_PARALLAXMAP");
                        toReturn.SetTexture("_ParallaxMap", availableTextures[i]);
                        break;
                    case ShaderSlot.OCCLUSION:
                        toReturn.SetTexture("_OcclusionMap", availableTextures[i]);
                        break;
                    case ShaderSlot.SPECULAR_MAP:
                        toReturn.EnableKeyword("_METALLICGLOSSMAP");
                        toReturn.SetTexture("_MetallicGlossMap", availableTextures[i]); //TODO _SPECGLOSSMAP?
                        toReturn.SetFloat("_Glossiness", 0.0f);
                        toReturn.SetFloat("_GlossMapScale", 0.0f);
                        break;
                    case ShaderSlot.NORMAL_MAP:
                        toReturn.EnableKeyword("_NORMALMAP");
                        toReturn.SetTexture("_BumpMap", availableTextures[i]);
                        break;
                }
            }

            float emissiveFactor = 1;
            Vector4 emissiveColour = Vector4.zero;

            //Apply properties
            for (int i = 0; i < Shader.Header.CSTCounts.Length; i++)
            {
                using (BinaryReader cstReader = new BinaryReader(new MemoryStream(_levelContent.ModelsMTL.CSTData[i])))
                {
                    int baseOffset = (InMaterial.ConstantBuffers[i].Offset * 4);

                    if (CSTIndexValid(metadata.cstIndexes.Diffuse0, ref Shader, i))
                    {
                        Vector4 colour = LoadFromCST<Vector4>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.Diffuse0] * 4));
                        toReturn.SetColor("_Color", colour);
                        //if (colour.w != 1)
                        //{
                        //    toReturn.SetFloat("_Mode", 1.0f);
                        //    toReturn.EnableKeyword("_ALPHATEST_ON");
                        //}
                    }
                    if (CSTIndexValid(metadata.cstIndexes.DiffuseMap0UVMultiplier, ref Shader, i))
                    {
                        float offset = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.DiffuseMap0UVMultiplier] * 4));
                        toReturn.SetTextureScale("_MainTex", new Vector2(offset, offset));
                    }
                    if (CSTIndexValid(metadata.cstIndexes.NormalMap0UVMultiplier, ref Shader, i))
                    {
                        float offset = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.NormalMap0UVMultiplier] * 4));
                        toReturn.SetTextureScale("_BumpMap", new Vector2(offset, offset));
                        toReturn.SetFloat("_BumpScale", offset);
                    }
                    if (CSTIndexValid(metadata.cstIndexes.OcclusionMapUVMultiplier, ref Shader, i))
                    {
                        float offset = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.OcclusionMapUVMultiplier] * 4));
                        toReturn.SetTextureScale("_OcclusionMap", new Vector2(offset, offset));
                    }
                    if (CSTIndexValid(metadata.cstIndexes.SpecularMap0UVMultiplier, ref Shader, i))
                    {
                        float offset = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.SpecularMap0UVMultiplier] * 4));
                        toReturn.SetTextureScale("_MetallicGlossMap", new Vector2(offset, offset));
                        toReturn.SetFloat("_GlossMapScale", offset);
                    }
                    if (CSTIndexValid(metadata.cstIndexes.SpecularFactor0, ref Shader, i))
                    {
                        float spec = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.SpecularFactor0] * 4));
                        toReturn.SetFloat("_Glossiness", spec);
                        toReturn.SetFloat("_GlossMapScale", spec);
                    }
                    if (CSTIndexValid(metadata.cstIndexes.Emission, ref Shader, i))
                    {
                        emissiveColour = LoadFromCST<Vector4>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.Emission] * 4));
                    }
                    if (CSTIndexValid(metadata.cstIndexes.EmissiveFactor, ref Shader, i))
                    {
                        emissiveFactor = LoadFromCST<float>(cstReader, baseOffset + (Shader.CSTLinks[i][metadata.cstIndexes.EmissiveFactor] * 4));
                    }
                }
            }

            if (!emissiveColour.Equals(Vector4.zero))
            {
                toReturn.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                toReturn.EnableKeyword("_EMISSION");
                toReturn.SetColor("_EmissionColor", emissiveColour * emissiveFactor);
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

        Parallel.For(0, 15, (i) =>
        {
            switch (i)
            {
                case 0:
                    ModelsMVR = new Movers(worldPath + "MODELS.MVR");
                    break;
                case 1:
                    CommandsPAK = new Commands(worldPath + "COMMANDS.PAK");
                    break;
                case 2:
                    RenderableREDS = new RenderableElements(worldPath + "REDS.BIN");
                    break;
                case 3:
                    ResourcesBIN = new CATHODE.Resources(worldPath + "RESOURCES.BIN");
                    break;
                case 4:
                    PhysicsMap = new PhysicsMaps(worldPath + "PHYSICS.MAP");
                    break;
                case 5:
                    EnvironmentMap = new EnvironmentMaps(worldPath + "ENVIRONMENTMAP.BIN");
                    break;
                case 6:
                    CollisionMap = new CollisionMaps(worldPath + "COLLISION.MAP");
                    break;
                case 7:
                    EnvironmentAnimation = new EnvironmentAnimations(worldPath + "ENVIRONMENT_ANIMATION.DAT");
                    break;
                case 8:
                    LevelLights = new Lights(worldPath + "LIGHTS.BIN");
                    break;
                case 9:
                    ModelsCST = File.ReadAllBytes(renderablePath + "LEVEL_MODELS.CST");
                    break;
                case 10:
                    ModelsMTL = new Materials(renderablePath + "LEVEL_MODELS.MTL");
                    break;
                case 11:
                    ModelsPAK = new Models(renderablePath + "LEVEL_MODELS.PAK");
                    break;
                case 12:
                    ShadersPAK = new ShadersPAK(renderablePath + "LEVEL_SHADERS_DX11.PAK");
                    break;
                case 13:
                    ShadersIDXRemap = new IDXRemap(renderablePath + "LEVEL_SHADERS_DX11_IDX_REMAP.PAK");
                    break;
                case 14:
                    LevelTextures = new Textures(renderablePath + "LEVEL_TEXTURES.ALL.PAK");
                    break;
            }
        });
    }

    public Movers ModelsMVR;
    public Commands CommandsPAK;
    public RenderableElements RenderableREDS;
    public CATHODE.Resources ResourcesBIN;
    public PhysicsMaps PhysicsMap;
    public EnvironmentMaps EnvironmentMap;
    public CollisionMaps CollisionMap;
    public EnvironmentAnimations EnvironmentAnimation;
    public byte[] ModelsCST;
    public Materials ModelsMTL;
    public Models ModelsPAK;
    public Textures LevelTextures;
    public Lights LevelLights;
    public ShadersPAK ShadersPAK;
    public alien_shader_bin_pak ShadersBIN;
    public IDXRemap ShadersIDXRemap;
};