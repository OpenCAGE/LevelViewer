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
using static CATHODE.Materials;
using UnityEngine.Profiling.Memory.Experimental;
using static UnityEditor.Rendering.BuiltIn.ShaderGraph.BuiltInBaseShaderGUI;
using UnityGLTF;
using UnityEngine.Experimental.Rendering;
using UnityGLTF.Plugins;
using System.Reflection.Emit;

public class AlienLevelLoader : MonoBehaviour
{
    //Support soon for combined Commands and Mover - but for now, lets let people toggle 
    [Tooltip("Enable this option to load data from the MVR file, which will apply additional instance-specific properties, such as cubemaps and texture overrides. Toggle this setting before hitting play.")]
    [SerializeField] private bool _loadMoverData = false;

    [Tooltip("Enable this to include objects in the scene that are of an unsupported material type (they will still be inactive by default).")]
    [SerializeField] private bool _populateObjectsWithUnsupportedMaterials = false;

    [Tooltip("Enable this to use UnityGLTF Shaders by default if possible.")]
    [SerializeField] private bool _useUnityGLTFMaterials = true;

    [Tooltip("Disable loading mipmaps (results in better texture visuals but increased memory consumption)")]
    [SerializeField] private bool _disableMipMaps = true;

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
    private Dictionary<int, OpenCAGEShaderMaterial> _materials = new Dictionary<int, OpenCAGEShaderMaterial>();
    private Dictionary<OpenCAGEShaderMaterial, bool> _materialSupport = new Dictionary<OpenCAGEShaderMaterial, bool>();
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
        _texturesGobo.Clear();
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

        EditorUtility.DisplayProgressBar("Loading composite" + composite.name, "Initialization", 0);

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
        for(int funcIdx=0; funcIdx < composite.functions.Count; funcIdx++)
        {
            FunctionEntity function = composite.functions[funcIdx];
            EditorUtility.DisplayProgressBar("Loading function " + function.shortGUID, "Processing function data", (funcIdx + 1 / composite.functions.Count));

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
        EditorUtility.ClearProgressBar();
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

    #region Asset Handlers
    private MeshRenderer SpawnModel(int binIndex, int mtlIndex, GameObject parent)
    {
        GameObjectHolder holder = GetModel(binIndex);
        if (holder == null)
        {
            Debug.Log("Attempted to load non-parsed model (" + binIndex + "). Skipping!");
            return null;
        }

        OpenCAGEShaderMaterial material = GetMaterial((mtlIndex == -1) ? holder.DefaultMaterial : mtlIndex);
        if (!_populateObjectsWithUnsupportedMaterials && !_materialSupport[material])
            return null;

        //Hack: we spawn the resource in a child of the GameObject hidden in the hierarchy, so that it's selectable in editor still
        GameObject newModelSpawnParent = new GameObject();
        
        if (parent != null) newModelSpawnParent.transform.parent = parent.transform;
        
        newModelSpawnParent.transform.localPosition = Vector3.zero;
        newModelSpawnParent.transform.localRotation = Quaternion.identity;
        newModelSpawnParent.name = "[RESOURCE] " + holder.Name;

        newModelSpawnParent.AddComponent<MeshFilter>().sharedMesh = holder.MainMesh;
        MeshRenderer renderer = newModelSpawnParent.AddComponent<MeshRenderer>();

        // Add shader component for export 
        OpenCAGEShaderMaterialWrapper materialWrapper = newModelSpawnParent.AddComponent<OpenCAGEShaderMaterialWrapper>();
        materialWrapper.openCAGEShaderMaterial = material;
        materialWrapper.materialName = material.baseMaterial?.name;

        renderer.sharedMaterial = material.baseMaterial;
        
        newModelSpawnParent.SetActive(_materialSupport[material]);

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

                    if (_disableMipMaps)
                        tex.Texture = new Texture2D((int)textureDims[0], (int)textureDims[1], format, false);
                    else
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

    public OpenCAGEShaderMaterial GetMaterial(int MTLIndex)
    {
        if (!_materials.ContainsKey(MTLIndex))
        {
            Materials.Material InMaterial = _levelContent.ModelsMTL.GetAtWriteIndex(MTLIndex);
            int RemappedIndex = _levelContent.ShadersIDXRemap.Datas[InMaterial.UberShaderIndex].Index;
            ShadersPAK.ShaderEntry currentShader = _levelContent.ShadersPAK.Shaders[RemappedIndex];

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

                    OpenCAGEShaderMaterial blankMat = new OpenCAGEShaderMaterial(UnityEngine.Shader.Find("Standard"));
                    blankMat.baseMaterial.name += " (NOT RENDERED: " + metadata.shaderCategory.ToString() + ")";
                    _materialSupport.Add(blankMat, false);
                    return blankMat;
            }

            List<Texture2D> availableTextures = new List<Texture2D>();
            for (int SlotIndex = 0; SlotIndex < currentShader.Header.TextureLinkCount; ++SlotIndex)
            {
                int PairIndex = currentShader.TextureLinks[SlotIndex];
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

            OpenCAGEShaderMaterial toReturn;

            // Use GLTF Mats only if shader is available
            if (_useUnityGLTFMaterials && Shader.Find("UnityGLTF/PBRGraph") != null)
            {
                toReturn = GetUnityGltfMaterial(metadata, availableTextures, InMaterial, currentShader);
            }
            else
            {
                toReturn = GetStandardMaterial(metadata, availableTextures, InMaterial, currentShader);
            }

            _materialSupport.Add(toReturn, true);
            _materials.Add(MTLIndex, toReturn);
        }
        return _materials[MTLIndex];
    } 

    public OpenCAGEShaderMaterial GetStandardMaterial(ShaderMaterialMetadata metadata, List<Texture2D> availableTextures, Materials.Material InMaterial, ShadersPAK.ShaderEntry currentShader)
    {

        OpenCAGEShaderMaterial openCAGEShaderMaterial = new OpenCAGEShaderMaterial(UnityEngine.Shader.Find("Standard"));
        UnityEngine.Material baseMaterial = openCAGEShaderMaterial.baseMaterial;
        baseMaterial.name = InMaterial.Name;

        //Apply materials
        for (int i = 0; i < metadata.textures.Count; i++)
        {
            if (i >= availableTextures.Count) continue;
            switch (metadata.textures[i].Type)
            {
                case ShaderSlot.DIFFUSE_MAP:
                    baseMaterial.SetTexture("_MainTex", availableTextures[i]);
                    break;
                case ShaderSlot.DETAIL_MAP:
                    baseMaterial.EnableKeyword("_DETAIL_MULX2");
                    baseMaterial.SetTexture("_DetailMask", availableTextures[i]);
                    break;
                case ShaderSlot.EMISSIVE:
                    baseMaterial.EnableKeyword("_EMISSION");
                    baseMaterial.SetTexture("_EmissionMap", availableTextures[i]);
                    break;
                case ShaderSlot.PARALLAX_MAP:
                    baseMaterial.EnableKeyword("_PARALLAXMAP");
                    baseMaterial.SetTexture("_ParallaxMap", availableTextures[i]);
                    break;
                case ShaderSlot.OCCLUSION:
                    baseMaterial.SetTexture("_OcclusionMap", availableTextures[i]);
                    break;
                case ShaderSlot.SPECULAR_MAP:
                    baseMaterial.EnableKeyword("_METALLICGLOSSMAP");
                    baseMaterial.SetTexture("_MetallicGlossMap", availableTextures[i]); //TODO _SPECGLOSSMAP?
                    baseMaterial.SetFloat("_Glossiness", 0.0f);
                    baseMaterial.SetFloat("_GlossMapScale", 0.0f);
                    break;
                case ShaderSlot.NORMAL_MAP:
                    baseMaterial.EnableKeyword("_NORMALMAP");
                    baseMaterial.SetTexture("_BumpMap", availableTextures[i]);
                    break;
            }
        }

        float emissiveFactor = 1;
        Vector4 emissiveColour = Vector4.zero;

        //Apply properties
        for (int i = 0; i < currentShader.Header.CSTCounts.Length; i++)
        {
            using (BinaryReader cstReader = new BinaryReader(new MemoryStream(_levelContent.ModelsMTL.CSTData[i])))
            {
                int baseOffset = (InMaterial.ConstantBuffers[i].Offset * 4);

                if (CSTIndexValid(metadata.cstIndexes.Diffuse0, ref currentShader, i))
                {
                    Vector4 colour = LoadFromCST<Vector4>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Diffuse0] * 4));
                    baseMaterial.SetColor("_Color", colour);
                }
                if (CSTIndexValid(metadata.cstIndexes.DiffuseMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DiffuseMap0UVMultiplier] * 4));
                    baseMaterial.SetTextureScale("_MainTex", new Vector2(offset, offset));
                }
                if (CSTIndexValid(metadata.cstIndexes.NormalMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap0UVMultiplier] * 4));
                    baseMaterial.SetTextureScale("_BumpMap", new Vector2(offset, offset));
                    baseMaterial.SetFloat("_BumpScale", offset);
                }
                if (CSTIndexValid(metadata.cstIndexes.OcclusionMapUVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OcclusionMapUVMultiplier] * 4));
                    baseMaterial.SetTextureScale("_OcclusionMap", new Vector2(offset, offset));
                }
                if (CSTIndexValid(metadata.cstIndexes.SpecularMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularMap0UVMultiplier] * 4));
                    baseMaterial.SetTextureScale("_MetallicGlossMap", new Vector2(offset, offset));
                    baseMaterial.SetFloat("_GlossMapScale", offset);
                }
                if (CSTIndexValid(metadata.cstIndexes.SpecularFactor0, ref currentShader, i))
                {
                    float spec = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularFactor0] * 4));
                    baseMaterial.SetFloat("_Glossiness", spec);
                    baseMaterial.SetFloat("_GlossMapScale", spec);
                }
                if (CSTIndexValid(metadata.cstIndexes.Emission, ref currentShader, i))
                {
                    emissiveColour = LoadFromCST<Vector4>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Emission] * 4));
                }
                if (CSTIndexValid(metadata.cstIndexes.EmissiveFactor, ref currentShader, i))
                {
                    emissiveFactor = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.EmissiveFactor] * 4));
                }
            }
        }

        if (!emissiveColour.Equals(Vector4.zero))
        {
            baseMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            baseMaterial.EnableKeyword("_EMISSION");
            baseMaterial.SetColor("_EmissionColor", emissiveColour * emissiveFactor);
        }

        baseMaterial.name += " " + metadata.shaderCategory.ToString();

        return openCAGEShaderMaterial;
    }
    public OpenCAGEShaderMaterial GetUnityGltfMaterial(ShaderMaterialMetadata metadata, List<Texture2D> availableTextures, Materials.Material InMaterial, ShadersPAK.ShaderEntry currentShader)
    {
        OpenCAGEShaderMaterial openCAGEShaderMaterial = new OpenCAGEShaderMaterial(UnityEngine.Shader.Find("UnityGLTF/PBRGraph"));
        UnityEngine.Material baseMaterial = openCAGEShaderMaterial.baseMaterial;
        baseMaterial.name = InMaterial.Name;

        openCAGEShaderMaterial.shaderCategory = metadata.shaderCategory.ToString();

        //Apply materials
        for (int i = 0; i < metadata.textures.Count; i++)
        {
            if (i >= availableTextures.Count) continue;
            switch (metadata.textures[i].Type)
            {
                case ShaderSlot.DIFFUSE_MAP:
                    baseMaterial.SetTexture("baseColorTexture", availableTextures[i]);
                    openCAGEShaderMaterial.shaderTextures["DiffuseMap"] = availableTextures[i];
                    break;
                case ShaderSlot.COLOR_RAMP_MAP:
                    openCAGEShaderMaterial.shaderTextures["ColorRampMap"] = availableTextures[i];
                    break;
                case ShaderSlot.SECONDARY_DIFFUSE_MAP:
                    openCAGEShaderMaterial.shaderTextures["SecondaryDiffuseMap"] = availableTextures[i];
                    break;
                case ShaderSlot.DIFFUSE_MAP_STATIC:
                    openCAGEShaderMaterial.shaderTextures["DiffuseMapStatic"] = availableTextures[i];
                    break;
                case ShaderSlot.OPACITY:
                    openCAGEShaderMaterial.shaderTextures["Opacity"] = availableTextures[i];
                    break;
                case ShaderSlot.NORMAL_MAP:
                    baseMaterial.SetTexture("normalTexture", availableTextures[i]);
                    openCAGEShaderMaterial.shaderTextures["NormalMap"] = availableTextures[i];
                    break;
                case ShaderSlot.SECONDARY_NORMAL_MAP:
                    openCAGEShaderMaterial.shaderTextures["SecondaryNormalMap"] = availableTextures[i];
                    break;
                case ShaderSlot.SPECULAR_MAP:
                    openCAGEShaderMaterial.shaderTextures["SpecularMap"] = availableTextures[i];
                    break;
                case ShaderSlot.SECONDARY_SPECULAR_MAP:
                    openCAGEShaderMaterial.shaderTextures["SecondarySpecularMap"] = availableTextures[i];
                    break;
                case ShaderSlot.ENVIRONMENT_MAP:
                    openCAGEShaderMaterial.shaderTextures["EnvironmentMap"] = availableTextures[i];
                    break;
                case ShaderSlot.OCCLUSION:
                    baseMaterial.SetTexture("occlusionTexture", availableTextures[i]);
                    openCAGEShaderMaterial.shaderTextures["Occlusion"] = availableTextures[i];
                    break;
                case ShaderSlot.FRESNEL_LUT:
                    openCAGEShaderMaterial.shaderTextures["FresnelLut"] = availableTextures[i];
                    break;
                case ShaderSlot.PARALLAX_MAP:
                    openCAGEShaderMaterial.shaderTextures["ParallaxMap"] = availableTextures[i];
                    break;
                case ShaderSlot.OPACITY_NOISE_MAP:
                    openCAGEShaderMaterial.shaderTextures["OpacityNoiseMap"] = availableTextures[i];
                    break;
                case ShaderSlot.DIRT_MAP:
                    openCAGEShaderMaterial.shaderTextures["DirtMap"] = availableTextures[i];
                    break;
                case ShaderSlot.WETNESS_NOISE:
                    openCAGEShaderMaterial.shaderTextures["WetnessNoise"] = availableTextures[i];
                    break;
                case ShaderSlot.ALPHA_THRESHOLD:
                    openCAGEShaderMaterial.shaderTextures["AlphaThreshold"] = availableTextures[i];
                    break;
                case ShaderSlot.IRRADIANCE_MAP:
                    openCAGEShaderMaterial.shaderTextures["IrradianceMap"] = availableTextures[i];
                    break;
                case ShaderSlot.CONVOLVED_DIFFUSE:
                    openCAGEShaderMaterial.shaderTextures["ConvolvedDiffuse"] = availableTextures[i];
                    break;
                case ShaderSlot.WRINKLE_MASK:
                    openCAGEShaderMaterial.shaderTextures["WrinkleMask"] = availableTextures[i];
                    break;
                case ShaderSlot.WRINKLE_NORMAL_MAP:
                    openCAGEShaderMaterial.shaderTextures["WrinkleNormalMap"] = availableTextures[i];
                    break;
                case ShaderSlot.SCATTER_MAP:
                    openCAGEShaderMaterial.shaderTextures["ScatterMap"] = availableTextures[i];
                    break;
                case ShaderSlot.EMISSIVE:
                    baseMaterial.SetTexture("emissiveTexture", availableTextures[i]);
                    openCAGEShaderMaterial.shaderTextures["Emissive"] = availableTextures[i];
                    break;
                case ShaderSlot.BURN_THROUGH:
                    openCAGEShaderMaterial.shaderTextures["BurnThrough"] = availableTextures[i];
                    break;
                case ShaderSlot.LIQUIFY:
                    openCAGEShaderMaterial.shaderTextures["Liquify"] = availableTextures[i];
                    break;
                case ShaderSlot.LIQUIFY2:
                    openCAGEShaderMaterial.shaderTextures["Liquify2"] = availableTextures[i];
                    break;
                case ShaderSlot.COLOR_RAMP:
                    openCAGEShaderMaterial.shaderTextures["ColorRamp"] = availableTextures[i];
                    break;
                case ShaderSlot.FLOW_MAP:
                    openCAGEShaderMaterial.shaderTextures["FlowMap"] = availableTextures[i];
                    break;
                case ShaderSlot.FLOW_TEXTURE_MAP:
                    openCAGEShaderMaterial.shaderTextures["FlowTextureMap"] = availableTextures[i];
                    break;
                case ShaderSlot.ALPHA_MASK:
                    openCAGEShaderMaterial.shaderTextures["AlphaMask"] = availableTextures[i];
                    break;
                case ShaderSlot.LOW_LOD_CHARACTER_MASK:
                    openCAGEShaderMaterial.shaderTextures["LowLodCharacterMask"] = availableTextures[i];
                    break;
                case ShaderSlot.UNSCALED_DIRT_MAP:
                    openCAGEShaderMaterial.shaderTextures["UnscaledDirtMap"] = availableTextures[i];
                    break;
                case ShaderSlot.FACE_MAP:
                    openCAGEShaderMaterial.shaderTextures["FaceMap"] = availableTextures[i];
                    break;
                case ShaderSlot.MASKING_MAP:
                    openCAGEShaderMaterial.shaderTextures["MaskingMap"] = availableTextures[i];
                    break;
                case ShaderSlot.ATMOSPHERE_MAP:
                    openCAGEShaderMaterial.shaderTextures["AtmosphereMap"] = availableTextures[i];
                    break;
                case ShaderSlot.DETAIL_MAP:
                    openCAGEShaderMaterial.shaderTextures["DetailMap"] = availableTextures[i];
                    break;
                case ShaderSlot.LIGHT_MAP:
                    openCAGEShaderMaterial.shaderTextures["LightMap"] = availableTextures[i];
                    break;
            }
        }

        float emissiveFactor = 1;
        Vector4 emissiveColour = Vector4.zero;
        
        //Apply properties
        for (int i = 0; i < currentShader.Header.CSTCounts.Length; i++)
        {
            using (BinaryReader cstReader = new BinaryReader(new MemoryStream(_levelContent.ModelsMTL.CSTData[i])))
            {
                int baseOffset = (InMaterial.ConstantBuffers[i].Offset * 4);

                if (CSTIndexValid(metadata.cstIndexes.Diffuse0, ref currentShader, i))
                {
                    Vector4 colour = LoadFromCST<Vector4>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Diffuse0] * 4));
                    baseMaterial.SetColor("baseColorFactor", colour);
                    openCAGEShaderMaterial.shaderParams["Diffuse0"] = colour;
                }
                if (CSTIndexValid(metadata.cstIndexes.DiffuseMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DiffuseMap0UVMultiplier] * 4));
                    baseMaterial.SetTextureScale("baseColorTexture", new Vector2(offset, offset));
                    openCAGEShaderMaterial.shaderParams["DiffuseMap0UVMultiplier"] = offset;
                }
                if (CSTIndexValid(metadata.cstIndexes.NormalMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap0UVMultiplier] * 4));
                    baseMaterial.SetTextureScale("normalTexture", new Vector2(offset, offset));
                    openCAGEShaderMaterial.shaderParams["NormalMap0UVMultiplier"] = offset;
                }
                if (CSTIndexValid(metadata.cstIndexes.OcclusionMapUVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OcclusionMapUVMultiplier] * 4));
                    baseMaterial.SetTextureScale("occlusionTexture", new Vector2(offset, offset));
                    openCAGEShaderMaterial.shaderParams["OcclusionMapUVMultiplier"] = offset;
                }
                if (CSTIndexValid(metadata.cstIndexes.SpecularMap0UVMultiplier, ref currentShader, i))
                {
                    float offset = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularMap0UVMultiplier] * 4));
                    openCAGEShaderMaterial.shaderParams["SpecularMap0UVMultiplier"] = offset;
                }
                if (CSTIndexValid(metadata.cstIndexes.Emission, ref currentShader, i))
                { 
                    emissiveColour = LoadFromCST<Vector4>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Emission] * 4));
                    openCAGEShaderMaterial.shaderParams["Emission"] = emissiveColour;
                }
                if (CSTIndexValid(metadata.cstIndexes.EmissiveFactor, ref currentShader, i))
                {
                    emissiveFactor = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.EmissiveFactor] * 4));
                    openCAGEShaderMaterial.shaderParams["EmissiveFactor"] = emissiveFactor;
                }

                if (CSTIndexValid(metadata.cstIndexes.Diffuse1, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Diffuse1"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Diffuse1] * 4));
                if (CSTIndexValid(metadata.cstIndexes.DiffuseMap1UVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["DiffuseMap1UVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DiffuseMap1UVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.DirtPower, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["DirtPower"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DirtPower] * 4));
                if (CSTIndexValid(metadata.cstIndexes.DirtStrength, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["DirtStrength"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DirtStrength] * 4));
                if (CSTIndexValid(metadata.cstIndexes.DirtUVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["DirtUVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.DirtUVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.Emission, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Emission"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Emission] * 4));
                if (CSTIndexValid(metadata.cstIndexes.EmissiveFactor, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["EmissiveFactor"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.EmissiveFactor] * 4));
                if (CSTIndexValid(metadata.cstIndexes.EnvironmentMapEmission, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["EnvironmentMapEmission"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.EnvironmentMapEmission] * 4));
                if (CSTIndexValid(metadata.cstIndexes.EnvironmentMapStrength, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["EnvironmentMapStrength"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.EnvironmentMapStrength] * 4));
                if (CSTIndexValid(metadata.cstIndexes.Iris0, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Iris0"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Iris0] * 4));
                if (CSTIndexValid(metadata.cstIndexes.Iris1, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Iris1"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Iris1] * 4));
                if (CSTIndexValid(metadata.cstIndexes.Iris2, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Iris2"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Iris2] * 4));
                if (CSTIndexValid(metadata.cstIndexes.IrisParallaxDisplacement, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["IrisParallaxDisplacement"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.IrisParallaxDisplacement] * 4));
                if (CSTIndexValid(metadata.cstIndexes.IsTransparent, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["IsTransparent"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.IsTransparent] * 4));
                if (CSTIndexValid(metadata.cstIndexes.LimbalSmoothRadius, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["LimbalSmoothRadius"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.LimbalSmoothRadius] * 4));
                if (CSTIndexValid(metadata.cstIndexes.Metallic, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["Metallic"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.Metallic] * 4));
                if (CSTIndexValid(metadata.cstIndexes.MetallicFactor0, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["MetallicFactor0"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.MetallicFactor0] * 4));
                if (CSTIndexValid(metadata.cstIndexes.MetallicFactor1, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["MetallicFactor1"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.MetallicFactor1] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMap0Strength, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMap0Strength"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap0Strength] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMap0UVMultiplierOfMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMap0UVMultiplierOfMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap0UVMultiplierOfMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMap1Strength, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMap1Strength"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap1Strength] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMap1UVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMap1UVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMap1UVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMapStrength, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMapStrength"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMapStrength] * 4));
                if (CSTIndexValid(metadata.cstIndexes.NormalMapUVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["NormalMapUVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.NormalMapUVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.OcclusionTint, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["OcclusionTint"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OcclusionTint] * 4));
                if (CSTIndexValid(metadata.cstIndexes.OpacityMapUVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["OpacityMapUVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OpacityMapUVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.OpacityNoiseAmplitude, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["OpacityNoiseAmplitude"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OpacityNoiseAmplitude] * 4));
                if (CSTIndexValid(metadata.cstIndexes.OpacityNoiseMapUVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["OpacityNoiseMapUVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.OpacityNoiseMapUVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.ParallaxFactor, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["ParallaxFactor"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.ParallaxFactor] * 4));
                if (CSTIndexValid(metadata.cstIndexes.ParallaxMapUVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["ParallaxMapUVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.ParallaxMapUVMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.ParallaxOffset, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["ParallaxOffset"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.ParallaxOffset] * 4));
                if (CSTIndexValid(metadata.cstIndexes.PupilDilation, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["PupilDilation"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.PupilDilation] * 4));
                if (CSTIndexValid(metadata.cstIndexes.RetinaIndexOfRefraction, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["RetinaIndexOfRefraction"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.RetinaIndexOfRefraction] * 4));
                if (CSTIndexValid(metadata.cstIndexes.RetinaRadius, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["RetinaRadius"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.RetinaRadius] * 4));
                if (CSTIndexValid(metadata.cstIndexes.ScatterMapMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["ScatterMapMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.ScatterMapMultiplier] * 4));
                if (CSTIndexValid(metadata.cstIndexes.SpecularFactor0, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["SpecularFactor0"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularFactor0] * 4));
                if (CSTIndexValid(metadata.cstIndexes.SpecularFactor1, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["SpecularFactor1"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularFactor1] * 4));
                if (CSTIndexValid(metadata.cstIndexes.SpecularMap1UVMultiplier, ref currentShader, i))
                    openCAGEShaderMaterial.shaderParams["SpecularMap1UVMultiplier"] = LoadFromCST<float>(cstReader, baseOffset + (currentShader.CSTLinks[i][metadata.cstIndexes.SpecularMap1UVMultiplier] * 4));
            }
        }

        if (!emissiveColour.Equals(Vector4.zero))
        {

            baseMaterial.SetColor("emissiveFactor", new Color(emissiveColour.x * emissiveFactor,
                emissiveColour.y * emissiveFactor,
                emissiveColour.z * emissiveFactor,
                emissiveColour.w * emissiveFactor));
        }

        baseMaterial.name += " " + metadata.shaderCategory.ToString();

        return openCAGEShaderMaterial;
    }

    void setMaterialTransparent(UnityEngine.Material toReturn)
    {
        toReturn.SetOverrideTag("RenderType", "Transparent");
        toReturn.SetFloat("_BUILTIN_SrcBlend", 5);
        toReturn.SetFloat("_BUILTIN_DstBlend", 10);
        toReturn.SetFloat("_BUILTIN_ZWrite", 0);
        toReturn.SetFloat("_BUILTIN_Surface", 1);
        toReturn.DisableKeyword("_ALPHATEST_ON");
        toReturn.EnableKeyword("_ALPHABLEND_ON");
        toReturn.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        toReturn.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;  //3000
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

        //Parallel.For(0, 15, (i) =>
        //{
        for(int i=0; i < 15; i++) { 

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
        };//);
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