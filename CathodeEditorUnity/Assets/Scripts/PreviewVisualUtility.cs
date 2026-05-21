using CATHODE.Scripting;
using OpenCAGE;
using UnityEngine;
using UnityEngine.Rendering;

public static class PreviewVisualUtility
{
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

    /// <summary>Opaque mesh previews (markers, characters, etc.).</summary>
    public static Material SharedOpaqueMaterial
    {
        get
        {
            if (_sharedOpaqueMaterial == null)
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null)
                    shader = Shader.Find("Legacy Shaders/Diffuse");

                _sharedOpaqueMaterial = new Material(shader);
                ConfigureOpaqueMaterial(_sharedOpaqueMaterial);
            }
            return _sharedOpaqueMaterial;
        }
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
        material.SetInt("_ZWrite", 1);
        material.SetInt("_SrcBlend", (int)BlendMode.One);
        material.SetInt("_DstBlend", (int)BlendMode.Zero);
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
            ApplyOpaqueColor(renderer, color);
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

    public static void ApplyOpaqueColor(MeshRenderer renderer, Color color)
    {
        if (renderer == null)
            return;

        renderer.sharedMaterial = SharedOpaqueMaterial;
        Material instance = renderer.material;
        color.a = 1f;
        instance.SetColor(ColorPropertyId, color);
        ConfigureOpaqueMaterial(instance);
        renderer.SetPropertyBlock(null);
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
                triangles[tri++] = a;
                triangles[tri++] = b;
                triangles[tri++] = c;
                triangles[tri++] = a;
                triangles[tri++] = c;
                triangles[tri++] = d;
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
}
