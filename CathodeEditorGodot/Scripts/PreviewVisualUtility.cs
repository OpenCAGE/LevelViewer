using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using System.Collections.Generic;

public static class PreviewVisualUtility
{
    private static ShaderMaterial _sharedTransparentMaterial;
    private static ShaderMaterial _sharedOpaqueMaterial;
    private static ShaderMaterial _sharedIconBillboardMaterial;
    private static ShaderMaterial _sharedOverlayLineMaterial;
    private static ArrayMesh _billboardQuadMesh;

    /* One material per colour, shared by every preview mesh that uses it.
     *
     * These used to be Duplicate(true)'d per mesh on every Refresh - a new ShaderMaterial AND a new
     * Shader each time, released only when the .NET wrapper was eventually garbage collected. A
     * render-filter toggle on a big level therefore minted thousands of shaders and materials, the
     * RenderingServer's RID pool (262,144 per resource type) ran out after enough refreshes
     * ("ERROR: Element limit reached" in _allocate_rid), and the next use of the invalid RID took the
     * process down with an access violation - the viewer just vanishing, with nothing logged, after
     * a filter toggle, a selection or a long session (issue #628). The preview palette is a handful
     * of colours, so these caches stay tiny and the shader is compiled once. */
    private static readonly Dictionary<Color, ShaderMaterial> _opaqueByColour = new Dictionary<Color, ShaderMaterial>();
    private static readonly Dictionary<Color, ShaderMaterial> _transparentByColour = new Dictionary<Color, ShaderMaterial>();
    private static readonly Dictionary<Color, ShaderMaterial> _overlayLineByColour = new Dictionary<Color, ShaderMaterial>();
    private static readonly Dictionary<(Texture2D icon, Color tint), ShaderMaterial> _iconBillboardByIcon = new Dictionary<(Texture2D icon, Color tint), ShaderMaterial>();

    private static ShaderMaterial GetColourMaterial(Dictionary<Color, ShaderMaterial> cache, Color color, ShaderMaterial shared)
    {
        if (cache.TryGetValue(color, out ShaderMaterial material) && GodotObject.IsInstanceValid(material))
            return material;

        //Share the shader; only the colour differs
        material = new ShaderMaterial
        {
            Shader = shared.Shader,
            RenderPriority = shared.RenderPriority,
        };
        material.SetShaderParameter("albedo_color", color);
        cache[color] = material;
        return material;
    }

    /// <summary>World-space width/height of icon billboard quads (see preview_icon_billboard.gdshader).</summary>
    public const float IconBillboardWorldSize = 0.25f;

    private const int OpaqueRenderPriority = 0;
    private const int TransparentRenderPriority = 1;
    private const float LookParallelEpsilon = 0.99f;

    private static bool IsParallel(Vector3 a, Vector3 b)
    {
        if (a.LengthSquared() < 0.0001f || b.LengthSquared() < 0.0001f)
            return false;

        return Mathf.Abs(Mathf.Abs(a.Normalized().Dot(b.Normalized())) - 1f) < 0.01f;
    }

    /// <summary>Pick an up vector that is not parallel (or anti-parallel) to the look target.</summary>
    public static Vector3 GetSafeLookUpVector(Vector3 lookTarget)
    {
        lookTarget = lookTarget.Normalized();
        if (lookTarget.LengthSquared() < 0.0001f)
            return Vector3.Up;

        Vector3[] candidates = { Vector3.Up, Vector3.Right, Vector3.Back, Vector3.Forward, Vector3.Left };
        foreach (Vector3 candidate in candidates)
        {
            if (!IsParallel(lookTarget, candidate))
                return candidate;
        }

        return Vector3.Right;
    }

    public static Vector3 GetLookEuler(Vector3 lookTarget, Vector3 extraEuler = default)
    {
        lookTarget = lookTarget.Normalized();
        if (lookTarget.LengthSquared() < 0.0001f)
            lookTarget = Vector3.Back;

        Vector3 euler = Basis.LookingAt(lookTarget, GetSafeLookUpVector(lookTarget)).GetEuler();
        return extraEuler + euler;
    }

    /// <summary>CylinderMesh is Y-aligned; rotate local +Y onto <paramref name="axis"/> (matches Unity FromToRotation(Vector3.up, axis)).</summary>
    public static Vector3 GetAxisStubEuler(Vector3 axis)
    {
        axis = axis.Normalized();
        if (axis.LengthSquared() < 0.0001f)
            return Vector3.Zero;

        if (axis.Dot(Vector3.Up) > LookParallelEpsilon)
            return Vector3.Zero;

        if (axis.Dot(Vector3.Down) > LookParallelEpsilon)
            return new Vector3(Mathf.Pi, 0f, 0f);

        return new Quaternion(Vector3.Up, axis).GetEuler();
    }

    public static ShaderMaterial SharedTransparentMaterial
    {
        get
        {
            if (_sharedTransparentMaterial == null)
            {
                Shader shader = GD.Load<Shader>("res://shaders/preview_transparent.gdshader");
                _sharedTransparentMaterial = new ShaderMaterial { Shader = shader };
                ConfigureTransparentMaterial(_sharedTransparentMaterial);
            }
            return _sharedTransparentMaterial;
        }
    }

    public static bool UsesTransparentPreview(FunctionEntity entity)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        return RenderFilterDefinitions.UsesTransparentPreview(entity.function.AsFunctionType);
    }

    public static ShaderMaterial SharedOpaqueMaterial
    {
        get
        {
            if (_sharedOpaqueMaterial == null)
            {
                Shader shader = GD.Load<Shader>("res://shaders/preview_opaque.gdshader");
                _sharedOpaqueMaterial = new ShaderMaterial { Shader = shader };
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
        color.A = 1f;
        return color;
    }

    private static void ConfigureTransparentMaterial(ShaderMaterial material)
    {
        if (material == null)
            return;

        material.RenderPriority = TransparentRenderPriority;
    }

    private static void ConfigureOpaqueMaterial(ShaderMaterial material)
    {
        if (material == null)
            return;

        material.RenderPriority = OpaqueRenderPriority;
    }

    public static bool IsIconBillboardMaterial(Material material)
    {
        if (material is not ShaderMaterial shaderMaterial || shaderMaterial.Shader == null)
            return false;

        string path = shaderMaterial.Shader.ResourcePath;
        return !string.IsNullOrEmpty(path) && path.Contains("preview_icon_billboard");
    }

    /// <summary>
    /// Ray test against a camera-facing icon billboard quad (matches preview_icon_billboard.gdshader).
    /// </summary>
    public static bool TryRayIntersectIconBillboard(
        MeshInstance3D meshInstance,
        Camera3D camera,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out float distance)
    {
        distance = 0f;
        if (meshInstance == null || !GodotObject.IsInstanceValid(meshInstance)
            || camera == null || !GodotObject.IsInstanceValid(camera))
        {
            return false;
        }

        Transform3D model = meshInstance.GlobalTransform;
        Vector3 scale = new Vector3(
            model.Basis.Column0.Length(),
            model.Basis.Column1.Length(),
            model.Basis.Column2.Length());
        Vector3 center = model.Origin;

        Basis cameraBasis = camera.GlobalTransform.Basis;
        Vector3 cameraRight = cameraBasis.Column0;
        Vector3 cameraUp = cameraBasis.Column1;
        Vector3 cameraForward = -cameraBasis.Column2;

        float denom = rayDirection.Dot(cameraForward);
        if (Mathf.Abs(denom) < 0.000001f)
            return false;

        float planeDistance = (center - rayOrigin).Dot(cameraForward) / denom;
        if (planeDistance < 0.000001f)
            return false;

        Vector3 hit = rayOrigin + rayDirection * planeDistance;
        Vector3 offset = hit - center;
        float localX = offset.Dot(cameraRight) / scale.X;
        float localY = offset.Dot(cameraUp) / scale.Y;
        if (localX < -0.5f || localX > 0.5f || localY < -0.5f || localY > 0.5f)
            return false;

        distance = planeDistance;
        return true;
    }

    public static Aabb GetIconBillboardPickBounds(MeshInstance3D meshInstance)
    {
        Transform3D model = meshInstance.GlobalTransform;
        Vector3 halfExtents = Vector3.One * (IconBillboardWorldSize * 0.5f);
        return new Aabb(model.Origin - halfExtents, halfExtents * 2f);
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

    /// <summary>
    /// Transform gizmos only apply to entities that can appear in the viewer (render-filter previews or model references).
    /// </summary>
    public static bool SupportsTransformGizmo(FunctionEntity entity, uint ownerCompositeId)
    {
        if (entity == null || !entity.function.IsFunctionType)
            return false;

        FunctionType functionType = entity.function.AsFunctionType;

        if (functionType == FunctionType.ModelReference)
            return true;

        if (!RenderFilterDefinitions.IsSupported(functionType))
            return false;

        return IsPreviewVisible(entity, ownerCompositeId);
    }

    public static bool HasValidWorldAnchor(Node3D node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
            return false;

        Vector3 position = node.GlobalPosition;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            return false;

        Basis basis = node.GlobalBasis;
        return IsFiniteVector(basis.Column0)
            && IsFiniteVector(basis.Column1)
            && IsFiniteVector(basis.Column2);
    }

    private static bool IsFiniteVector(Vector3 vector)
        => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);

    public static void CollectMeshInstances(Node node, List<MeshInstance3D> meshes)
    {
        if (node == null || meshes == null)
            return;

        var pending = new System.Collections.Generic.Stack<Node>();
        pending.Push(node);
        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current == null || !GodotObject.IsInstanceValid(current))
                continue;

            if (current is MeshInstance3D meshInstance)
                meshes.Add(meshInstance);

            foreach (Node child in current.GetChildren())
                pending.Push(child);
        }
    }

    /// <summary>
    /// Collects meshes on <paramref name="root"/> without descending into nested entity nodes.
    /// </summary>
    public static void CollectMeshInstancesForEntityVisual(Node3D root, List<MeshInstance3D> meshes)
    {
        if (root == null || meshes == null)
            return;

        var pending = new System.Collections.Generic.Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current == null || !GodotObject.IsInstanceValid(current))
                continue;

            if (current is MeshInstance3D meshInstance)
                meshes.Add(meshInstance);

            foreach (Node child in current.GetChildren())
            {
                if (child is Node3D child3D
                    && child3D != root
                    && AlienScene.HasOwnerComposite(child3D))
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    public static void PreparePreviewObject(Node3D node, bool opaque = true)
    {
        MeshInstance3D meshInstance = node as MeshInstance3D;
        if (meshInstance == null)
            meshInstance = node.GetNodeOrNull<MeshInstance3D>(".");

        if (meshInstance != null)
        {
            meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            meshInstance.MaterialOverride = opaque ? SharedOpaqueMaterial : SharedTransparentMaterial;
        }
    }

    public static void ApplyColor(MeshInstance3D meshInstance, Color color, bool opaque = true)
    {
        if (meshInstance == null)
            return;

        if (opaque)
            ApplyOpaqueColor(meshInstance, color);
        else
            ApplyTransparentColor(meshInstance, color);
    }

    public static void ApplyFunctionPreviewColor(MeshInstance3D meshInstance, FunctionEntity entity)
    {
        if (meshInstance == null || entity == null)
            return;

        bool transparent = UsesTransparentPreview(entity);
        Color color = transparent ? GetPreviewColor(entity) : GetOpaquePreviewColor(entity);
        ApplyColor(meshInstance, color, opaque: !transparent);
    }

    public static void ApplyTransparentColor(MeshInstance3D meshInstance, Color color)
    {
        if (meshInstance == null)
            return;

        meshInstance.MaterialOverride = GetColourMaterial(_transparentByColour, color, SharedTransparentMaterial);
    }

    public static void ApplyOpaqueColor(MeshInstance3D meshInstance, Color color)
    {
        if (meshInstance == null)
            return;

        color.A = 1f;
        meshInstance.MaterialOverride = GetColourMaterial(_opaqueByColour, color, SharedOpaqueMaterial);
    }

    public static Node3D CreateMeshPreview(string name, Node3D parent, Mesh mesh, Color color, bool opaque = true)
    {
        MeshInstance3D meshInstance = new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
        };
        parent.AddChild(meshInstance);
        PreparePreviewObject(meshInstance, opaque);
        ApplyColor(meshInstance, color, opaque);
        return meshInstance;
    }

    public static Node3D CreatePrimitivePreview(string name, Node3D parent, PrimitiveMesh primitive, Color color, bool opaque = true)
    {
        MeshInstance3D meshInstance = new MeshInstance3D
        {
            Name = name,
            Mesh = primitive,
        };
        parent.AddChild(meshInstance);
        PreparePreviewObject(meshInstance, opaque);
        ApplyColor(meshInstance, color, opaque);
        return meshInstance;
    }

    private static readonly Color PositionMarkerAxisX = Colors.Red;
    private static readonly Color PositionMarkerAxisY = Colors.Green;
    private static readonly Color PositionMarkerAxisZ = Colors.Blue;

    public static Node3D CreatePositionStyleMarker(
        string name,
        Node3D parent,
        Color torusColor,
        float torusRadius,
        float tubeRadius,
        float axisLength,
        float axisWidth)
    {
        Node3D root = new Node3D { Name = name };
        parent.AddChild(root);

        ArrayMesh torusMesh = CreateTorusMesh(torusRadius, tubeRadius);
        CreateMeshPreview("Torus", root, torusMesh, torusColor);
        CreateAxisStub("AxisX", root, Vector3.Right, axisLength, axisWidth, PositionMarkerAxisX);
        CreateAxisStub("AxisY", root, Vector3.Up, axisLength, axisWidth, PositionMarkerAxisY);
        CreateAxisStub("AxisZ", root, Vector3.Back, axisLength, axisWidth, PositionMarkerAxisZ);
        return root;
    }

    private static void CreateAxisStub(string name, Node3D parent, Vector3 direction, float length, float width, Color color)
    {
        Vector3 axis = direction.Normalized();
        CylinderMesh cylinder = new CylinderMesh
        {
            TopRadius = width,
            BottomRadius = width,
            Height = length,
        };
        Node3D axisObject = CreatePrimitivePreview(name, parent, cylinder, color);
        axisObject.Rotation = GetAxisStubEuler(axis);
        axisObject.Position = axis * (length * 0.5f);
    }

    public static ShaderMaterial SharedOverlayLineMaterial
    {
        get
        {
            if (_sharedOverlayLineMaterial == null)
            {
                Shader shader = GD.Load<Shader>("res://shaders/preview_overlay_line.gdshader");
                _sharedOverlayLineMaterial = new ShaderMaterial
                {
                    Shader = shader,
                    RenderPriority = 127,
                };
            }
            return _sharedOverlayLineMaterial;
        }
    }

    public static Node3D CreateLineSegment(string name, Node3D parent, Vector3 localStart, Vector3 localEnd, float width, Color color)
    {
        Vector3 delta = localEnd - localStart;
        float length = delta.Length();
        if (length < 0.001f)
            return null;

        CylinderMesh cylinder = new CylinderMesh
        {
            TopRadius = width,
            BottomRadius = width,
            Height = length,
        };

        Node3D line = CreatePrimitivePreview(name, parent, cylinder, color);
        line.Position = localStart + delta * 0.5f;
        line.Rotation = GetLookEuler(delta, new Vector3(Mathf.Pi / 2f, 0f, 0f));
        MeshInstance3D meshInstance = line as MeshInstance3D;
        if (meshInstance != null)
            meshInstance.MaterialOverride = GetColourMaterial(_overlayLineByColour, color, SharedOverlayLineMaterial);
        return line;
    }

    public static Node3D CreateDirectionArrow(
        string name,
        Node3D parent,
        Vector3 localPosition,
        Vector3 localDirection,
        Color color,
        float headLength,
        float headWidth)
    {
        Node3D arrowRoot = new Node3D { Name = name };
        parent.AddChild(arrowRoot);
        arrowRoot.Position = localPosition;

        Vector3 direction = localDirection.Normalized();
        if (direction.LengthSquared() < 0.0001f)
            direction = Vector3.Back;

        arrowRoot.Rotation = GetLookEuler(direction);

        float wingBack = headLength * 0.35f;
        Vector3 tip = Vector3.Back * headLength;
        Vector3 wingA = Vector3.Back * wingBack + Vector3.Right * headWidth;
        Vector3 wingB = Vector3.Back * wingBack - Vector3.Right * headWidth;

        CreateLineSegment("ArrowShaft", arrowRoot, Vector3.Zero, tip, headWidth * 0.35f, color);
        CreateLineSegment("ArrowWingA", arrowRoot, tip, wingA, headWidth * 0.3f, color);
        CreateLineSegment("ArrowWingB", arrowRoot, tip, wingB, headWidth * 0.3f, color);
        return arrowRoot;
    }

    public static ArrayMesh CreateTorusMesh(float outerRadius, float tubeRadius, int segments = 24, int tubeSegments = 12)
    {
        ArrayMesh mesh = new ArrayMesh();
        int vertexCount = segments * tubeSegments;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uvs = new Vector2[vertexCount];
        int[] triangles = new int[segments * tubeSegments * 6];

        int vert = 0;
        for (int seg = 0; seg < segments; seg++)
        {
            float segAngle = seg / (float)segments * Mathf.Pi * 2f;
            Vector3 ringOffset = new Vector3(Mathf.Cos(segAngle), 0f, Mathf.Sin(segAngle)) * outerRadius;

            for (int tube = 0; tube < tubeSegments; tube++)
            {
                float tubeAngle = tube / (float)tubeSegments * Mathf.Pi * 2f;
                Vector3 local = new Vector3(Mathf.Cos(tubeAngle), Mathf.Sin(tubeAngle), 0f) * tubeRadius;
                Vector3 rotated = ringOffset + new Vector3(local.X * Mathf.Cos(segAngle), local.Y, local.X * Mathf.Sin(segAngle));
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

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = triangles;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    public static ShaderMaterial SharedIconBillboardMaterial
    {
        get
        {
            if (_sharedIconBillboardMaterial == null)
            {
                Shader shader = GD.Load<Shader>("res://shaders/preview_icon_billboard.gdshader");
                _sharedIconBillboardMaterial = new ShaderMaterial { Shader = shader };
                ConfigureTransparentMaterial(_sharedIconBillboardMaterial);
            }
            return _sharedIconBillboardMaterial;
        }
    }

    public static Node3D CreateIconBillboard(string name, Node3D parent, Texture2D icon, Color tint, float size = IconBillboardWorldSize)
    {
        if (icon == null || parent == null)
            return null;

        var root = new Node3D
        {
            Name = name,
            Scale = Vector3.One * size,
        };
        parent.AddChild(root);

        MeshInstance3D meshInstance = new MeshInstance3D
        {
            Name = "Quad",
            Mesh = GetBillboardQuadMesh(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        root.AddChild(meshInstance);

        (Texture2D icon, Color tint) key = (icon, tint);
        if (!_iconBillboardByIcon.TryGetValue(key, out ShaderMaterial material) || !GodotObject.IsInstanceValid(material))
        {
            material = new ShaderMaterial
            {
                Shader = SharedIconBillboardMaterial.Shader,
                RenderPriority = SharedIconBillboardMaterial.RenderPriority,
            };
            material.SetShaderParameter("albedo_texture", icon);
            material.SetShaderParameter("albedo_color", tint);
            _iconBillboardByIcon[key] = material;
        }
        meshInstance.MaterialOverride = material;

        return root;
    }

    private static ArrayMesh GetBillboardQuadMesh()
    {
        if (_billboardQuadMesh != null)
            return _billboardQuadMesh;

        _billboardQuadMesh = new ArrayMesh();
        Vector3[] vertices =
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3(0.5f, -0.5f, 0f),
            new Vector3(-0.5f, 0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f),
        };
        Vector2[] uvs =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
        };
        int[] triangles = { 0, 2, 1, 2, 3, 1 };

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = triangles;
        _billboardQuadMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return _billboardQuadMesh;
    }

    public static void DestroyNode(Node node)
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return;

        node.QueueFree();
    }

    public static void CleanupAllFunctionEntityPreviews(Node searchRoot = null)
    {
        if (searchRoot == null)
        {
            SceneTree tree = Engine.GetMainLoop() as SceneTree;
            searchRoot = tree?.CurrentScene;
        }

        if (searchRoot == null)
            return;

        FunctionEntityPreview[] previews = EntityNodeUtil.FindAllPreviews(searchRoot);
        for (int i = 0; i < previews.Length; i++)
        {
            if (previews[i] != null && GodotObject.IsInstanceValid(previews[i]))
                previews[i].CleanupPreviewVisuals();
        }
    }
}
