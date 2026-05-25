using CATHODE.Scripting;
using Godot;
using OpenCAGE;
using System.Collections.Generic;

public static class PreviewVisualUtility
{
    private static ShaderMaterial _sharedBoxMaterial;
    private static ShaderMaterial _sharedOpaqueMaterial;
    private static ShaderMaterial _sharedIconBillboardMaterial;
    private static ShaderMaterial _sharedOverlayLineMaterial;
    private static ArrayMesh _billboardQuadMesh;

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

    /// <summary>CylinderMesh is Y-aligned; rotate to the cardinal axis without Basis.LookingAt.</summary>
    public static Vector3 GetAxisStubEuler(Vector3 axis)
    {
        axis = axis.Normalized();
        if (axis.Dot(Vector3.Right) > LookParallelEpsilon)
            return new Vector3(0f, 0f, -Mathf.Pi * 0.5f);
        if (axis.Dot(Vector3.Up) > LookParallelEpsilon)
            return new Vector3(Mathf.Pi * 0.5f, 0f, 0f);
        if (axis.Dot(Vector3.Back) > LookParallelEpsilon)
            return new Vector3(-Mathf.Pi * 0.5f, 0f, 0f);
        if (axis.Dot(Vector3.Left) > LookParallelEpsilon)
            return new Vector3(0f, 0f, Mathf.Pi * 0.5f);
        if (axis.Dot(Vector3.Down) > LookParallelEpsilon)
            return new Vector3(-Mathf.Pi * 0.5f, 0f, 0f);

        return GetLookEuler(axis, new Vector3(Mathf.Pi * 0.5f, 0f, 0f));
    }

    public static ShaderMaterial SharedBoxMaterial
    {
        get
        {
            if (_sharedBoxMaterial == null)
            {
                Shader shader = GD.Load<Shader>("res://shaders/preview_transparent.gdshader");
                _sharedBoxMaterial = new ShaderMaterial { Shader = shader };
                ConfigureTransparentMaterial(_sharedBoxMaterial);
            }
            return _sharedBoxMaterial;
        }
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

    public static void PreparePreviewObject(Node3D node, bool opaque = false)
    {
        MeshInstance3D meshInstance = node as MeshInstance3D;
        if (meshInstance == null)
            meshInstance = node.GetNodeOrNull<MeshInstance3D>(".");

        if (meshInstance != null)
        {
            meshInstance.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            meshInstance.MaterialOverride = opaque ? SharedOpaqueMaterial : SharedBoxMaterial;
        }
    }

    public static void ApplyColor(MeshInstance3D meshInstance, Color color, bool opaque = false)
    {
        if (meshInstance == null)
            return;

        if (opaque)
            ApplyOpaqueColor(meshInstance, color);
        else
            ApplyTransparentColor(meshInstance, color);
    }

    public static void ApplyTransparentColor(MeshInstance3D meshInstance, Color color)
    {
        if (meshInstance == null)
            return;

        ShaderMaterial material = (ShaderMaterial)SharedBoxMaterial.Duplicate(true);
        material.SetShaderParameter("albedo_color", color);
        meshInstance.MaterialOverride = material;
    }

    public static void ApplyOpaqueColor(MeshInstance3D meshInstance, Color color)
    {
        if (meshInstance == null)
            return;

        ShaderMaterial material = (ShaderMaterial)SharedOpaqueMaterial.Duplicate(true);
        color.A = 1f;
        material.SetShaderParameter("albedo_color", color);
        meshInstance.MaterialOverride = material;
    }

    public static Node3D CreateMeshPreview(string name, Node3D parent, Mesh mesh, Color color, bool opaque = false)
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

    public static Node3D CreatePrimitivePreview(string name, Node3D parent, PrimitiveMesh primitive, Color color, bool opaque = false)
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
        CreateMeshPreview("Torus", root, torusMesh, torusColor, opaque: true);
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
        Node3D axisObject = CreatePrimitivePreview(name, parent, cylinder, color, opaque: true);
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

        Node3D line = CreatePrimitivePreview(name, parent, cylinder, color, opaque: true);
        line.Position = localStart + delta * 0.5f;
        line.Rotation = GetLookEuler(delta, new Vector3(Mathf.Pi / 2f, 0f, 0f));
        MeshInstance3D meshInstance = line as MeshInstance3D;
        if (meshInstance != null)
        {
            ShaderMaterial material = (ShaderMaterial)SharedOverlayLineMaterial.Duplicate(true);
            material.SetShaderParameter("albedo_color", color);
            meshInstance.MaterialOverride = material;
        }
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

    public static IconBillboardBehaviour CreateIconBillboard(string name, Node3D parent, Texture2D icon, Color tint, float size = 0.75f)
    {
        if (icon == null || parent == null)
            return null;

        IconBillboardBehaviour root = new IconBillboardBehaviour
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

        ShaderMaterial material = (ShaderMaterial)SharedIconBillboardMaterial.Duplicate(true);
        material.SetShaderParameter("albedo_texture", icon);
        material.SetShaderParameter("albedo_color", tint);
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
