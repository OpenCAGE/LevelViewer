using CATHODE.Scripting;
using OpenCAGE;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class PreviewVisualUtility
{
#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    private static void ResetSharedMaterialsOnDomainReload()
    {
        _sharedBoxMaterial = null;
        _sharedOpaqueMaterial = null;
        _sharedIconBillboardMaterial = null;
        _sharedOverlayLineMaterial = null;
    }
#endif

    private static Material _sharedBoxMaterial;
    private static Material _sharedOpaqueMaterial;
    private static Material _sharedIconBillboardMaterial;
    private static Mesh _billboardQuadMesh;
    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
    private static readonly int MainTexPropertyId = Shader.PropertyToID("_MainTex");
    private const int OpaqueRenderQueue = (int)RenderQueue.Geometry;
    private const int TransparentRenderQueue = (int)RenderQueue.Transparent;

    /// <summary>Semi-transparent box volume previews (Legacy transparent diffuse).</summary>
    public static Material SharedBoxMaterial
    {
        get
        {
            if (_sharedBoxMaterial == null)
            {
                Shader shader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (shader == null)
                    shader = Shader.Find("Unlit/Transparent");

                _sharedBoxMaterial = new Material(shader);
                ConfigureTransparentMaterial(_sharedBoxMaterial);
            }
            return _sharedBoxMaterial;
        }
    }

    private const string PreviewOpaqueShaderName = "CathodeEditor/PreviewOpaque";
#if UNITY_EDITOR
    private const string PreviewOpaqueShaderAssetPath = "Assets/Shaders/PreviewOpaque.shader";
#endif

    /// <summary>Opaque mesh previews (markers, characters, etc.).</summary>
    public static Material SharedOpaqueMaterial
    {
        get
        {
            EnsureSharedOpaqueMaterial();
            return _sharedOpaqueMaterial;
        }
    }

    private static void EnsureSharedOpaqueMaterial()
    {
        Shader shader = FindPreviewOpaqueShader();
        if (_sharedOpaqueMaterial != null && _sharedOpaqueMaterial.shader != shader)
        {
            DestroyObject(_sharedOpaqueMaterial);
            _sharedOpaqueMaterial = null;
        }

        if (_sharedOpaqueMaterial != null)
            return;

        _sharedOpaqueMaterial = new Material(shader);
        ConfigureOpaqueMaterial(_sharedOpaqueMaterial);
    }

    private static Shader FindPreviewOpaqueShader()
    {
        Shader shader = Shader.Find(PreviewOpaqueShaderName);
#if UNITY_EDITOR
        if (shader == null)
            shader = AssetDatabase.LoadAssetAtPath<Shader>(PreviewOpaqueShaderAssetPath);
#endif
        if (shader != null)
            return shader;

        shader = Shader.Find("Unlit/Color");
        if (shader != null)
            return shader;

        return Shader.Find("Legacy Shaders/Diffuse");
    }

    public static Color GetPreviewColor(FunctionEntity entity)
    {
        RenderFilterDefinitions.RenderFilterColor color = RenderFilterDefinitions.GetColor(entity.function.AsFunctionType);
        return new Color(color.R, color.G, color.B, color.A);
    }

    public static Color GetOpaquePreviewColor(FunctionEntity entity)
    {
        Color color = GetPreviewColor(entity);
        color.a = 1f;
        return color;
    }

    private static void ConfigureTransparentMaterial(Material material)
    {
        if (material == null)
            return;

        material.renderQueue = TransparentRenderQueue;
        material.SetOverrideTag("RenderType", "Transparent");
    }

    private static void ConfigureOpaqueMaterial(Material material)
    {
        if (material == null)
            return;

        material.renderQueue = OpaqueRenderQueue;
        material.SetOverrideTag("RenderType", "Opaque");

        if (material.HasProperty("_ZWrite"))
            material.SetInt("_ZWrite", 1);
        if (material.HasProperty("_SrcBlend"))
            material.SetInt("_SrcBlend", (int)BlendMode.One);
        if (material.HasProperty("_DstBlend"))
            material.SetInt("_DstBlend", (int)BlendMode.Zero);

        // Legacy/Standard shaders: force opaque mode so previews never pick up fade/transparent state.
        if (material.HasProperty("_Mode"))
            material.SetFloat("_Mode", 0f);
        material.DisableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHATEST_ON");

        // Standard/Legacy: 0 = Off (double-sided). Custom PreviewOpaque sets Cull Off in the pass.
        if (material.HasProperty("_Cull"))
            material.SetInt("_Cull", 0);
    }

    public static bool IsVisible(FunctionEntity entity)
    {
        return IsPreviewVisible(entity, 0);
    }

    public static bool IsPreviewVisible(FunctionEntity entity, uint ownerCompositeId)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        FunctionType functionType = entity.function.AsFunctionType;

        if (functionType == FunctionType.ModelReference)
            return true;

        if (RenderFilterDefinitions.IsSupported(functionType) && !RenderFilters.IsEnabled(functionType))
            return false;

        if (PreviewVisibilitySettings.HideNestedScriptEntities
            && PreviewVisibilitySettings.ActiveCompositeId != 0
            && ownerCompositeId != PreviewVisibilitySettings.ActiveCompositeId)
        {
            return false;
        }

        return true;
    }

    public static void PreparePreviewObject(GameObject gameObject, bool opaque = false, bool hideInHierarchy = true)
    {
        Collider collider = gameObject.GetComponent<Collider>();
        if (collider != null)
            Object.Destroy(collider);

        MeshRenderer renderer = gameObject.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = opaque ? SharedOpaqueMaterial : SharedBoxMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.SetPropertyBlock(null);
        }

#if UNITY_EDITOR && !LOCAL_DEV
        if (hideInHierarchy)
            gameObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
    }

    public static void ApplyColor(MeshRenderer renderer, Color color, ref MaterialPropertyBlock propertyBlock, bool opaque = false)
    {
        if (renderer == null)
            return;

        if (opaque)
            ApplyOpaqueColor(renderer, color, ref propertyBlock);
        else
            ApplyTransparentColor(renderer, color, ref propertyBlock);
    }

    public static void ApplyTransparentColor(MeshRenderer renderer, Color color, ref MaterialPropertyBlock propertyBlock)
    {
        if (renderer == null)
            return;

        renderer.sharedMaterial = SharedBoxMaterial;
        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();

        propertyBlock.SetColor(ColorPropertyId, color);
        renderer.SetPropertyBlock(propertyBlock);
    }

    public static void ApplyOpaqueColor(MeshRenderer renderer, Color color, ref MaterialPropertyBlock propertyBlock)
    {
        if (renderer == null)
            return;

        EnsureSharedOpaqueMaterial();
        renderer.sharedMaterial = SharedOpaqueMaterial;

        // Per-renderer block so torus/axis colours cannot share one live property block instance.
        var block = new MaterialPropertyBlock();
        color.a = 1f;
        block.SetColor(ColorPropertyId, color);
        renderer.SetPropertyBlock(block);
        propertyBlock = block;
    }

    public static GameObject CreateMeshPreview(string name, Transform parent, Mesh mesh, Color color, ref MaterialPropertyBlock propertyBlock, bool opaque = false)
    {
        GameObject gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
        PreparePreviewObject(gameObject, opaque);
        ApplyColor(renderer, color, ref propertyBlock, opaque);
        return gameObject;
    }

    public static GameObject CreatePrimitivePreview(string name, Transform parent, PrimitiveType primitiveType, Color color, ref MaterialPropertyBlock propertyBlock, bool opaque = false)
    {
        GameObject gameObject = GameObject.CreatePrimitive(primitiveType);
        gameObject.name = name;
        gameObject.transform.SetParent(parent, false);
        PreparePreviewObject(gameObject, opaque);
        ApplyColor(gameObject.GetComponent<MeshRenderer>(), color, ref propertyBlock, opaque);
        return gameObject;
    }

    private static readonly Color PositionMarkerAxisX = Color.red;
    private static readonly Color PositionMarkerAxisY = Color.green;
    private static readonly Color PositionMarkerAxisZ = Color.blue;

    /// <summary>
    /// Torus ring plus RGB axis stubs (same visual language as PositionMarkerPreview).
    /// </summary>
    public static GameObject CreatePositionStyleMarker(
        string name,
        Transform parent,
        Color torusColor,
        float torusRadius,
        float tubeRadius,
        float axisLength,
        float axisWidth,
        ref MaterialPropertyBlock propertyBlock)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(parent, false);
#if UNITY_EDITOR && !LOCAL_DEV
        root.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif

        Mesh torusMesh = CreateTorusMesh(torusRadius, tubeRadius);
        CreateMeshPreview("Torus", root.transform, torusMesh, torusColor, ref propertyBlock, opaque: true);
        CreateAxisStub("AxisX", root.transform, Vector3.right, axisLength, axisWidth, PositionMarkerAxisX, ref propertyBlock);
        CreateAxisStub("AxisY", root.transform, Vector3.up, axisLength, axisWidth, PositionMarkerAxisY, ref propertyBlock);
        CreateAxisStub("AxisZ", root.transform, Vector3.forward, axisLength, axisWidth, PositionMarkerAxisZ, ref propertyBlock);
        return root;
    }

    private static void CreateAxisStub(string name, Transform parent, Vector3 direction, float length, float width, Color color, ref MaterialPropertyBlock propertyBlock)
    {
        Vector3 axis = direction.normalized;
        GameObject axisObject = CreatePrimitivePreview(name, parent, PrimitiveType.Cylinder, color, ref propertyBlock, opaque: true);
        axisObject.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);
        axisObject.transform.localPosition = axis * (length * 0.5f);
        axisObject.transform.localScale = new Vector3(width, length * 0.5f, width);
    }

    private static Material _sharedOverlayLineMaterial;

    /// <summary>
    /// LineRenderer configured to draw on top of scene geometry (orange-friendly spline paths).
    /// </summary>
    public static void ConfigureOverlayLineRenderer(LineRenderer lineRenderer, Color color, float width = 0.012f)
    {
        if (lineRenderer == null)
            return;

        if (_sharedOverlayLineMaterial == null)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            _sharedOverlayLineMaterial = new Material(shader);
            _sharedOverlayLineMaterial.renderQueue = (int)RenderQueue.Overlay;
            if (_sharedOverlayLineMaterial.HasProperty("_ZTest"))
                _sharedOverlayLineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            if (_sharedOverlayLineMaterial.HasProperty("_ZWrite"))
                _sharedOverlayLineMaterial.SetInt("_ZWrite", 0);
        }

        lineRenderer.sharedMaterial = _sharedOverlayLineMaterial;
        lineRenderer.useWorldSpace = false;
        lineRenderer.loop = false;
        lineRenderer.alignment = LineAlignment.View;
        lineRenderer.startWidth = width;
        lineRenderer.endWidth = width;
        lineRenderer.startColor = color;
        lineRenderer.endColor = color;
        lineRenderer.numCornerVertices = 2;
        lineRenderer.numCapVertices = 2;
        lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        lineRenderer.textureMode = LineTextureMode.Stretch;
        lineRenderer.generateLightingData = false;
    }

    public static GameObject CreateDirectionArrow(
        string name,
        Transform parent,
        Vector3 localPosition,
        Vector3 localDirection,
        Color color,
        float headLength,
        float headWidth,
        ref MaterialPropertyBlock propertyBlock)
    {
        GameObject arrowRoot = new GameObject(name);
        arrowRoot.transform.SetParent(parent, false);
        arrowRoot.transform.localPosition = localPosition;

        Vector3 direction = localDirection.normalized;
        if (direction.sqrMagnitude < 0.0001f)
            direction = Vector3.forward;

        arrowRoot.transform.localRotation = Quaternion.LookRotation(direction, Mathf.Abs(direction.y) > 0.95f ? Vector3.forward : Vector3.up);

        float wingBack = headLength * 0.35f;
        Vector3 tip = Vector3.forward * headLength;
        Vector3 wingA = Vector3.forward * wingBack + Vector3.right * headWidth;
        Vector3 wingB = Vector3.forward * wingBack - Vector3.right * headWidth;

        CreateArrowLine("ArrowShaft", arrowRoot.transform, Vector3.zero, tip, headWidth * 0.35f, color, ref propertyBlock);
        CreateArrowLine("ArrowWingA", arrowRoot.transform, tip, wingA, headWidth * 0.3f, color, ref propertyBlock);
        CreateArrowLine("ArrowWingB", arrowRoot.transform, tip, wingB, headWidth * 0.3f, color, ref propertyBlock);
#if UNITY_EDITOR && !LOCAL_DEV
        arrowRoot.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
        return arrowRoot;
    }

    private static void CreateArrowLine(string name, Transform parent, Vector3 localStart, Vector3 localEnd, float width, Color color, ref MaterialPropertyBlock propertyBlock)
    {
        Vector3 delta = localEnd - localStart;
        float length = delta.magnitude;
        if (length < 0.001f)
            return;

        GameObject line = CreatePrimitivePreview(name, parent, PrimitiveType.Cylinder, color, ref propertyBlock, opaque: true);
        line.transform.localPosition = localStart + delta * 0.5f;
        line.transform.localRotation = Quaternion.FromToRotation(Vector3.up, delta.normalized);
        line.transform.localScale = new Vector3(width, length * 0.5f, width);
    }

    public static Mesh CreateTorusMesh(float outerRadius, float tubeRadius, int segments = 24, int tubeSegments = 12)
    {
        Mesh mesh = new Mesh();
        int vertexCount = segments * tubeSegments;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[segments * tubeSegments * 6];

        int vert = 0;
        for (int seg = 0; seg < segments; seg++)
        {
            float segAngle = seg / (float)segments * Mathf.PI * 2f;
            Vector3 ringOffset = new Vector3(Mathf.Cos(segAngle), 0f, Mathf.Sin(segAngle)) * outerRadius;

            for (int tube = 0; tube < tubeSegments; tube++)
            {
                float tubeAngle = tube / (float)tubeSegments * Mathf.PI * 2f;
                Vector3 local = new Vector3(Mathf.Cos(tubeAngle), Mathf.Sin(tubeAngle), 0f) * tubeRadius;
                Vector3 rotated = ringOffset + new Vector3(local.x * Mathf.Cos(segAngle), local.y, local.x * Mathf.Sin(segAngle));
                vertices[vert] = rotated;
                uvs[vert] = new Vector2(seg / (float)segments, tube / (float)tubeSegments);
                vert++;
            }
        }

        int tri = 0;
        for (int seg = 0; seg < segments; seg++)
        {
            int segNext = (seg + 1) % segments;
            for (int tube = 0; tube < tubeSegments; tube++)
            {
                int tubeNext = (tube + 1) % tubeSegments;
                int a = seg * tubeSegments + tube;
                int b = segNext * tubeSegments + tube;
                int c = segNext * tubeSegments + tubeNext;
                int d = seg * tubeSegments + tubeNext;
                // Winding flipped vs Godot copy: Unity is left-handed so the shared strip order faces inward with Cull Back.
                triangles[tri++] = a;
                triangles[tri++] = c;
                triangles[tri++] = b;
                triangles[tri++] = a;
                triangles[tri++] = d;
                triangles[tri++] = c;
            }
        }

        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    public static Material SharedIconBillboardMaterial
    {
        get
        {
            if (_sharedIconBillboardMaterial == null)
            {
                Shader shader = Shader.Find("Unlit/Transparent");
                if (shader == null)
                    shader = Shader.Find("Sprites/Default");

                _sharedIconBillboardMaterial = new Material(shader);
                ConfigureTransparentMaterial(_sharedIconBillboardMaterial);
            }
            return _sharedIconBillboardMaterial;
        }
    }

    public static GameObject CreateIconBillboard(string name, Transform parent, Texture2D icon, float size = 0.5f)
    {
        if (icon == null)
            return null;

        GameObject gameObject = new GameObject(name);
        gameObject.transform.SetParent(parent, false);
        gameObject.transform.localScale = Vector3.one * size;
        gameObject.AddComponent<IconBillboardBehaviour>();

        MeshFilter meshFilter = gameObject.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = GetBillboardQuadMesh();

        MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = SharedIconBillboardMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;

        MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
        propertyBlock.SetTexture(MainTexPropertyId, icon);
        propertyBlock.SetColor(ColorPropertyId, Color.white);
        renderer.SetPropertyBlock(propertyBlock);

        Collider collider = gameObject.GetComponent<Collider>();
        if (collider != null)
            Object.Destroy(collider);

#if UNITY_EDITOR && !LOCAL_DEV
        gameObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
#endif
        return gameObject;
    }

    private static Mesh GetBillboardQuadMesh()
    {
        if (_billboardQuadMesh != null)
            return _billboardQuadMesh;

        _billboardQuadMesh = new Mesh
        {
            name = "IconBillboardQuad",
            vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            },
            uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
            },
            triangles = new[] { 0, 2, 1, 2, 3, 1 },
        };
        _billboardQuadMesh.RecalculateNormals();
        _billboardQuadMesh.RecalculateBounds();
        return _billboardQuadMesh;
    }

    public static void DestroyObject(Object obj)
    {
        if (obj == null)
            return;

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            Object.DestroyImmediate(obj);
            return;
        }
#endif
        Object.Destroy(obj);
    }

    /// <summary>
    /// Tear down all function-entity preview visuals (e.g. before destroying the level root or exiting play mode).
    /// </summary>
    public static void CleanupAllFunctionEntityPreviews()
    {
        FunctionEntityPreview[] previews = Object.FindObjectsOfType<FunctionEntityPreview>(true);
        for (int i = 0; i < previews.Length; i++)
        {
            if (previews[i] != null)
                previews[i].CleanupPreviewVisuals();
        }

#if UNITY_EDITOR
        DestroyOrphanedPreviewObjects();
#endif
    }

#if UNITY_EDITOR
    private static readonly string[] OrphanedPreviewRootNames =
    {
        "SplinePathPreview",
        "BoxPreview",
        "PositionMarkerPreview",
        "SoundEnvironmentMarkerPreview",
        "CharacterPreview",
        "IconBillboard",
    };

    private static void DestroyOrphanedPreviewObjects()
    {
        GameObject[] gameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
        for (int i = 0; i < gameObjects.Length; i++)
        {
            GameObject gameObject = gameObjects[i];
            if (gameObject == null)
                continue;

            if ((gameObject.hideFlags & HideFlags.DontSave) == 0)
                continue;

            if (!IsOrphanedPreviewObject(gameObject))
                continue;

            DestroyObject(gameObject);
        }
    }

    private static bool IsOrphanedPreviewObject(GameObject gameObject)
    {
        if (IsPartOfLivePreviewHierarchy(gameObject))
            return false;

        string name = gameObject.name;
        for (int i = 0; i < OrphanedPreviewRootNames.Length; i++)
        {
            if (name == OrphanedPreviewRootNames[i])
                return true;
        }

        if (name.StartsWith("Segment"))
            return true;

        return false;
    }

    private static bool IsPartOfLivePreviewHierarchy(GameObject gameObject)
    {
        FunctionEntityPreview owner = gameObject.GetComponentInParent<FunctionEntityPreview>(true);
        if (owner == null)
            return false;

        GameObject visualRoot = owner.PreviewVisualRoot;
        if (visualRoot == null)
            return false;

        return gameObject == visualRoot || gameObject.transform.IsChildOf(visualRoot.transform);
    }
#endif
}
