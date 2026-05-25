using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public partial class AlienScene : Node3D
{
	public Action OnLoaded;

	private string _levelName = "";
	public string LevelName => _levelName;

	private Node3D _parentNode = null;
	public Node3D ParentNode => _parentNode;

	private Composite _loadedComposite = null;
	public uint CompositeID => _loadedComposite == null ? 0 : _loadedComposite.shortGUID.AsUInt32;
	public string CompositeIDString => _loadedComposite == null || _loadedComposite.shortGUID == ShortGuid.Invalid ? "" : _loadedComposite.shortGUID.ToByteString();
	public string CompositeName => _loadedComposite == null ? "" : _loadedComposite.name;

	private Dictionary<Textures.TEX4, TexOrCube> _texturesGlobal = new Dictionary<Textures.TEX4, TexOrCube>();
	private Dictionary<Textures.TEX4, TexOrCube> _texturesLevel = new Dictionary<Textures.TEX4, TexOrCube>();
	private Dictionary<Materials.Material, StandardMaterial3D> _materials = new Dictionary<Materials.Material, StandardMaterial3D>();
	private Dictionary<Materials.Material, ShaderMaterial> _wireframeMaterials = new Dictionary<Materials.Material, ShaderMaterial>();
	private Dictionary<StandardMaterial3D, bool> _materialSupport = new Dictionary<StandardMaterial3D, bool>();
	private Dictionary<MeshInstance3D, Materials.Material> _modelReferenceMeshes = new Dictionary<MeshInstance3D, Materials.Material>();
	private Dictionary<Models.CS2.Component.LOD.Submesh, MeshHolder> _modelMeshes = new Dictionary<Models.CS2.Component.LOD.Submesh, MeshHolder>();

	/// <summary>Subtracted from Cathode world space so geometry sits near the origin (reduces float jitter / cull pops).</summary>
	private Vector3 _contentOrigin = Vector3.Zero;

	private Dictionary<ShortGuid, List<Node3D>> _compositeNodes = new Dictionary<ShortGuid, List<Node3D>>();
	private Dictionary<Node3D, Entity> _nodeEntities = new Dictionary<Node3D, Entity>();
	private readonly Dictionary<ulong, List<Node3D>> _entityNodesByKey = new Dictionary<ulong, List<Node3D>>();

	private FunctionEntityPreview[] _cachedFunctionEntityPreviews = Array.Empty<FunctionEntityPreview>();
	private readonly Dictionary<uint, List<FunctionEntityPreview>> _previewsByOwnerComposite = new Dictionary<uint, List<FunctionEntityPreview>>();
	private bool _functionEntityPreviewsCacheDirty = true;

	public class ParameterVisualContext
	{
		public Composite Composite;
		public Entity Entity;
		public Node3D EntityNode;
		public SyncedParameter Sync;
		public bool FromPointer;
		public bool PointedOverride;
	}

	public delegate void ParameterVisualHandler(ParameterVisualContext context);

	private readonly Dictionary<DataType, ParameterVisualHandler> _parameterVisualHandlers = new Dictionary<DataType, ParameterVisualHandler>();

	public LevelContent Content => _content;
	private LevelContent _content = new LevelContent();

	public class TexOrCube
	{
		public Texture2D Texture = null;
		public Texture2D Cubemap = null;
	}

	public override void _Ready()
	{
		RegisterDefaultParameterVisualHandlers();
	}

	public void RegisterParameterVisualHandler(DataType dataType, ParameterVisualHandler handler)
	{
		_parameterVisualHandlers[dataType] = handler;
	}

	private void RegisterDefaultParameterVisualHandlers()
	{
		RegisterParameterVisualHandler(DataType.TRANSFORM, ApplyTransformVisual);
		RegisterParameterVisualHandler(DataType.VECTOR, ApplyVectorVisual);
		RegisterParameterVisualHandler(DataType.SPLINE, ApplySplineVisual);
		RegisterParameterVisualHandler(DataType.BOOL, ApplyBoolVisual);
	}

	public override void _ExitTree()
	{
		if (_parentNode != null && GodotObject.IsInstanceValid(_parentNode))
			_parentNode.QueueFree();

		_parentNode = null;
		base._ExitTree();
	}

	private void ResetLevel()
	{
		PreviewVisualUtility.CleanupAllFunctionEntityPreviews(this);
		LevelViewerSelection.Clear();

		if (_parentNode != null && GodotObject.IsInstanceValid(_parentNode))
			_parentNode.QueueFree();

		_parentNode = null;

		_materials.Clear();
		_wireframeMaterials.Clear();
		_modelReferenceMeshes.Clear();
		_materialSupport.Clear();

		foreach (KeyValuePair<Textures.TEX4, TexOrCube> kvp in _texturesLevel)
		{
			if (kvp.Value.Texture != null && GodotObject.IsInstanceValid(kvp.Value.Texture))
				kvp.Value.Texture.Dispose();
			if (kvp.Value.Cubemap != null && GodotObject.IsInstanceValid(kvp.Value.Cubemap))
				kvp.Value.Cubemap.Dispose();
		}
		_texturesLevel.Clear();

		foreach (KeyValuePair<Models.CS2.Component.LOD.Submesh, MeshHolder> kvp in _modelMeshes)
			kvp.Value.MainMesh?.Dispose();
		_modelMeshes.Clear();

		_compositeNodes.Clear();
		_nodeEntities.Clear();
		ClearEntityNodeCache();

		_contentOrigin = Vector3.Zero;
		_content.Reset();
	}

	public void LoadLevel(string level, string pathToAI)
	{
		if (level == null || level == "" || pathToAI == "")
			return;

		GD.Print("Loading level " + level + "...");

		ResetLevel();

		_levelName = level;
		_content.Load(pathToAI, level);
	}

	public void PopulateComposite(ShortGuid guid)
	{
		if (!_content.Loaded) return;

		_compositeNodes.Clear();
		_nodeEntities.Clear();
		ClearEntityNodeCache();
		PreviewVisualUtility.CleanupAllFunctionEntityPreviews(this);
		LevelViewerSelection.Clear();

		if (_parentNode != null && GodotObject.IsInstanceValid(_parentNode))
			_parentNode.QueueFree();

		_contentOrigin = Vector3.Zero;
		_parentNode = new Node3D { Name = _levelName };
		_parentNode.AddToGroup(LevelViewerView.ContentGroup);
		AddChild(_parentNode);

		Composite comp = _content.Level.Commands.GetComposite(guid);
		GD.Print("Loading composite " + comp?.name + "...");
		_loadedComposite = comp;
		AddCompositeInstance(comp, _parentNode, null);

		InvalidateFunctionEntityPreviewCache();
		RefreshRenderFilters();
		OnLoaded?.Invoke();
		Callable.From(() =>
		{
			RecenterContentOrigin();
			RequestFrameView(ParentNode, false);
		}).CallDeferred();
	}

	/// <summary>Moves level root so loaded content is centered near the origin for stable rendering.</summary>
	public void RecenterContentOrigin()
	{
		if (_parentNode == null || !GodotObject.IsInstanceValid(_parentNode))
			return;

		if (!LevelViewerView.TryComputeGlobalAabb(_parentNode, out Aabb bounds) || !bounds.HasVolume())
			return;

		Vector3 center = bounds.GetCenter();
		if (_contentOrigin.DistanceSquaredTo(center) < 0.25f)
			return;

		_contentOrigin = center;
		_parentNode.Position = -_contentOrigin;
	}

	private void AddCompositeInstance(Composite composite, Node3D compositeNode, Entity parentEntity)
	{
		if (composite == null) return;

		if (_compositeNodes.ContainsKey(composite.shortGUID))
		{
			_compositeNodes[composite.shortGUID].Add(compositeNode);
		}
		else
		{
			List<Node3D> compositeNodes = new List<Node3D>();
			compositeNodes.Add(compositeNode);
			_compositeNodes.Add(composite.shortGUID, compositeNodes);
		}

		foreach (Entity entity in composite.functions)
			AddEntity(composite, entity, compositeNode);
		foreach (Entity entity in composite.variables)
			AddEntity(composite, entity, compositeNode);
		foreach (Entity entity in composite.aliases)
			AddEntity(composite, entity, compositeNode);
		foreach (Entity entity in composite.proxies)
			AddEntity(composite, entity, compositeNode);

		InvalidateFunctionEntityPreviewCache();
	}

	public void RemoveComposite(ShortGuid composite)
	{
		if (_compositeNodes.ContainsKey(composite))
		{
			foreach (Node3D compositeInstance in _compositeNodes[composite])
			{
				if (compositeInstance != null && GodotObject.IsInstanceValid(compositeInstance))
				{
					compositeInstance.QueueFree();
					_nodeEntities.Remove(compositeInstance);
				}
			}
			_compositeNodes.Remove(composite);
			InvalidateFunctionEntityPreviewCache();
		}
	}

	public void AddEntity(ShortGuid composite, ShortGuid entity)
	{
		if (_compositeNodes.ContainsKey(composite))
		{
			foreach (Node3D compositeInstance in _compositeNodes[composite])
			{
				if (compositeInstance != null && GodotObject.IsInstanceValid(compositeInstance))
				{
					Composite c = _content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID == composite);
					Entity e = c?.GetEntityByID(entity);
					if (c != null && e != null)
						AddEntity(c, e, compositeInstance);
				}
			}
		}
	}

	private void AddEntity(Composite composite, Entity entity, Node3D parentNode)
	{
		GetEntityTransform(entity, out Vector3 position, out Vector3 rotation);

		Node3D entityNode;
		switch (entity.variant)
		{
			case EntityVariant.ALIAS:
			case EntityVariant.PROXY:
				entityNode = new EntityOverride { Name = entity.shortGUID.AsUInt32.ToString() };
				break;
			default:
				entityNode = new Node3D { Name = entity.shortGUID.AsUInt32.ToString() };
				break;
		}

		parentNode.AddChild(entityNode);
		entityNode.Position = position;
		entityNode.RotationDegrees = rotation;
		_nodeEntities.Add(entityNode, entity);
		TrackEntityNode(composite.shortGUID, entity.shortGUID, entityNode);

		switch (entity.variant)
		{
			case EntityVariant.ALIAS:
			{
				AliasEntity alias = (AliasEntity)entity;
				Node3D aliasedNode = GetEntityNode(EntityPathToGUIDList(alias.alias), parentNode);
				if (aliasedNode != null)
				{
					EntityNodeUtil.SetPointed(aliasedNode, true);
					((EntityOverride)entityNode).PointedEntity = aliasedNode;
					if (alias.GetParameter("position") != null)
					{
						aliasedNode.Position = position;
						aliasedNode.RotationDegrees = rotation;
					}
				}
				break;
			}
			case EntityVariant.PROXY:
			{
				ProxyEntity proxy = (ProxyEntity)entity;
				Node3D proxiedNode = GetEntityNode(EntityPathToGUIDList(proxy.proxy), ParentNode);
				if (proxiedNode != null)
				{
					EntityNodeUtil.SetPointed(proxiedNode, true);
					((EntityOverride)entityNode).PointedEntity = proxiedNode;
					if (proxy.GetParameter("position") != null)
					{
						proxiedNode.Position = position;
						proxiedNode.RotationDegrees = rotation;
					}
				}
				break;
			}
			case EntityVariant.FUNCTION:
			{
				FunctionEntity function = (FunctionEntity)entity;
				if (!function.function.IsFunctionType)
				{
					Composite compositeNext = _content.Level.Commands.GetComposite(function.function);
					if (compositeNext != null)
						AddCompositeInstance(compositeNext, entityNode, function);
				}
				else
				{
					if (FunctionEntityPreviewSetup.TryAddPreview(this, function, entityNode, _content.Level.Commands.Utils, composite.shortGUID))
						_functionEntityPreviewsCacheDirty = true;
				}
				break;
			}
		}
	}

	public void SelectEntity(List<uint> path, bool focusSelected)
	{
		Node3D entityNode = GetEntityNode(path, ParentNode);
		if (entityNode != null)
		{
			EntityOverride entityOverride = entityNode as EntityOverride;
			if (entityOverride != null && entityOverride.PointedEntity != null)
				entityNode = entityOverride.PointedEntity;
		}

		LevelViewerSelection.Apply(entityNode);

		if (entityNode != null)
			GD.Print("SelectEntity: " + string.Join("/", path) + " -> " + entityNode.Name);

		if (entityNode != null)
			Callable.From(() => FocusSelectedEntity(entityNode)).CallDeferred();
	}

	private void FocusSelectedEntity(Node3D target)
	{
		if (target == null || !GodotObject.IsInstanceValid(target))
			return;

		Camera3D camera = GetTree().Root.GetNodeOrNull<Camera3D>("Connection/Camera3D");
		if (camera is LevelViewerCamera viewerCamera)
			viewerCamera.FocusOnTarget(target);
		else
			LevelViewerView.FrameRuntimeCameraClose(target, camera);
	}

	private void RequestFrameView(Node3D target, bool focusEditor)
	{
		if (target == null || !GodotObject.IsInstanceValid(target))
			return;

		RecenterContentOrigin();

		Camera3D camera = GetTree().Root.GetNodeOrNull<Camera3D>("Connection/Camera3D");
		if (focusEditor)
			LevelViewerView.FrameAll(ParentNode ?? target, camera, focusEditor: true);
		else
			LevelViewerView.FrameRuntimeCameraOnNode(target, camera);
	}

	private Node3D GetEntityNode(List<uint> path, Node3D parent)
	{
		try
		{
			Node current = parent;
			for (int i = 0; i < path.Count; i++)
				current = current.GetNodeOrNull(path[i].ToString());
			return current as Node3D;
		}
		catch
		{
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

	public void ApplyEntityParameter(ShortGuid dataCompositeID, ShortGuid dataEntityID, SyncedParameter sync, ShortGuid visualCompositeID, ShortGuid visualEntityID, bool fromPointer, bool pointedOverride)
	{
		if (sync == null)
			return;

		Composite dataComposite = _content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID == dataCompositeID);
		if (dataComposite == null)
			return;

		Entity dataEntity = dataComposite.GetEntityByID(dataEntityID);
		if (dataEntity == null)
			return;

		ParameterSync.ApplyToEntity(dataEntity, sync, _content);

		DataType syncDataType = ParameterSync.GetDataType(sync);
		if (syncDataType == DataType.RESOURCE)
		{
			FunctionEntity remapEntity = ModelReferencePreview.ResolveModelReferenceEntity(
				dataEntity, dataComposite, _content.Level.Commands);
			if (sync.removed)
			{
				if (remapEntity != null)
					_content.RemappedResources.Remove(remapEntity);
			}
			else
			{
				List<Tuple<int, int>> renderables = ParameterSync.ToRenderableIndexList(sync, _content);
				if (renderables.Count > 0 && remapEntity != null)
					_content.RemappedResources[remapEntity] = renderables;
			}
		}

		if (!ShouldSyncVisualForOwnerComposite(visualCompositeID))
			return;

		Composite visualComposite = _content.Level.Commands.Entries.FirstOrDefault(o => o.shortGUID == visualCompositeID);
		Entity visualEntity = visualComposite?.GetEntityByID(visualEntityID);
		if (visualComposite == null || visualEntity == null)
			return;

		if (!TryGetCachedEntityNodes(visualCompositeID, visualEntityID, out List<Node3D> entityNodes))
			return;

		for (int i = 0; i < entityNodes.Count; i++)
		{
			Node3D entityNode = entityNodes[i];
			if (entityNode == null || !GodotObject.IsInstanceValid(entityNode))
				continue;

			ParameterVisualContext context = new ParameterVisualContext()
			{
				Composite = visualComposite,
				Entity = visualEntity,
				EntityNode = entityNode,
				Sync = sync,
				FromPointer = fromPointer,
				PointedOverride = pointedOverride,
			};

			if (_parameterVisualHandlers.TryGetValue(syncDataType, out ParameterVisualHandler handler))
				handler(context);
			else if (syncDataType != DataType.VECTOR && syncDataType != DataType.SPLINE && syncDataType != DataType.BOOL)
				RefreshFunctionEntityPreviews(entityNode);
		}
	}

	private void RefreshFunctionEntityPreviews(Node3D entityNode)
	{
		if (entityNode == null)
			return;

		BoxPreview[] boxPreviews = EntityNodeUtil.FindPreviews<BoxPreview>(entityNode);
		for (int i = 0; i < boxPreviews.Length; i++)
			boxPreviews[i].Refresh();

		SplinePathPreview[] splinePreviews = EntityNodeUtil.FindPreviews<SplinePathPreview>(entityNode);
		for (int i = 0; i < splinePreviews.Length; i++)
			splinePreviews[i].Refresh();

		ModelReferencePreview[] modelPreviews = EntityNodeUtil.FindPreviews<ModelReferencePreview>(entityNode);
		for (int i = 0; i < modelPreviews.Length; i++)
			modelPreviews[i].Refresh();

		FunctionEntityPreview[] others = EntityNodeUtil.FindAllPreviews(entityNode);
		for (int i = 0; i < others.Length; i++)
		{
			if (others[i] is BoxPreview || others[i] is SplinePathPreview || others[i] is ModelReferencePreview)
				continue;
			others[i].Refresh();
		}
	}

	public void InvalidateFunctionEntityPreviewCache()
	{
		_functionEntityPreviewsCacheDirty = true;
		_previewsByOwnerComposite.Clear();
	}

	private void EnsureFunctionEntityPreviewCache()
	{
		if (!_functionEntityPreviewsCacheDirty)
			return;

		_cachedFunctionEntityPreviews = _parentNode == null
			? Array.Empty<FunctionEntityPreview>()
			: EntityNodeUtil.FindAllPreviews(_parentNode);
		_previewsByOwnerComposite.Clear();

		for (int i = 0; i < _cachedFunctionEntityPreviews.Length; i++)
		{
			FunctionEntityPreview preview = _cachedFunctionEntityPreviews[i];
			if (preview == null)
				continue;

			uint ownerCompositeId = preview.OwnerCompositeId;
			if (!_previewsByOwnerComposite.TryGetValue(ownerCompositeId, out List<FunctionEntityPreview> previews))
			{
				previews = new List<FunctionEntityPreview>();
				_previewsByOwnerComposite.Add(ownerCompositeId, previews);
			}

			previews.Add(preview);
		}

		_functionEntityPreviewsCacheDirty = false;
	}

	public void RefreshNestedCompositeVisibility(uint previousActiveCompositeId, uint newActiveCompositeId)
	{
		if (_parentNode == null)
			return;

		EnsureFunctionEntityPreviewCache();

		if (previousActiveCompositeId != 0)
			RefreshVisibilityForComposite(previousActiveCompositeId);

		if (newActiveCompositeId != 0 && newActiveCompositeId != previousActiveCompositeId)
			RefreshVisibilityForComposite(newActiveCompositeId);
	}

	private void RefreshVisibilityForComposite(uint ownerCompositeId)
	{
		if (!_previewsByOwnerComposite.TryGetValue(ownerCompositeId, out List<FunctionEntityPreview> previews))
			return;

		for (int i = 0; i < previews.Count; i++)
		{
			FunctionEntityPreview preview = previews[i];
			if (preview != null)
				preview.RefreshVisibility();
		}
	}

	public void RefreshRenderFilters(HashSet<uint> changedFunctionTypes = null)
	{
		if (_parentNode == null)
			return;

		EnsureFunctionEntityPreviewCache();

		for (int i = 0; i < _cachedFunctionEntityPreviews.Length; i++)
		{
			FunctionEntityPreview preview = _cachedFunctionEntityPreviews[i];
			if (preview == null)
			{
				_functionEntityPreviewsCacheDirty = true;
				continue;
			}

			if (preview is ModelReferencePreview)
				continue;

			if (changedFunctionTypes != null && preview.Entity != null && preview.Entity.function.IsFunctionType)
			{
				uint functionType = (uint)preview.Entity.function.AsFunctionType;
				if (!changedFunctionTypes.Contains(functionType))
					continue;
			}

			preview.Refresh();
		}
	}

	private void ApplyVectorVisual(ParameterVisualContext context)
	{
		RefreshBoxPreviews(context.EntityNode);
	}

	private static void RefreshBoxPreviews(Node3D entityNode)
	{
		if (entityNode == null)
			return;

		BoxPreview[] boxPreviews = EntityNodeUtil.FindPreviews<BoxPreview>(entityNode);
		for (int i = 0; i < boxPreviews.Length; i++)
			boxPreviews[i].RefreshDimensions();
	}

	private void ApplySplineVisual(ParameterVisualContext context)
	{
		RefreshSplinePathPreviews(context.EntityNode);
	}

	private void ApplyBoolVisual(ParameterVisualContext context)
	{
		if (context.Sync != null && new ShortGuid(context.Sync.name) != ShortGuidUtils.Generate("loop"))
			return;

		RefreshSplinePathPreviews(context.EntityNode);
	}

	private static void RefreshSplinePathPreviews(Node3D entityNode)
	{
		if (entityNode == null)
			return;

		SplinePathPreview[] previews = EntityNodeUtil.FindPreviews<SplinePathPreview>(entityNode);
		for (int i = 0; i < previews.Length; i++)
			previews[i].Refresh();
	}

	private static ulong MakeEntityCacheKey(ShortGuid compositeId, ShortGuid entityId)
	{
		return ((ulong)compositeId.AsUInt32 << 32) | entityId.AsUInt32;
	}

	private void TrackEntityNode(ShortGuid compositeId, ShortGuid entityId, Node3D entityNode)
	{
		ulong key = MakeEntityCacheKey(compositeId, entityId);
		if (!_entityNodesByKey.TryGetValue(key, out List<Node3D> entityNodes))
		{
			entityNodes = new List<Node3D>();
			_entityNodesByKey.Add(key, entityNodes);
		}

		entityNodes.Add(entityNode);
	}

	private void UntrackEntityNode(ShortGuid compositeId, ShortGuid entityId, Node3D entityNode)
	{
		ulong key = MakeEntityCacheKey(compositeId, entityId);
		if (_entityNodesByKey.TryGetValue(key, out List<Node3D> entityNodes))
			entityNodes.Remove(entityNode);
	}

	private void ClearEntityNodeCache()
	{
		_entityNodesByKey.Clear();
	}

	private bool TryGetCachedEntityNodes(ShortGuid compositeId, ShortGuid entityId, out List<Node3D> entityNodes)
	{
		return _entityNodesByKey.TryGetValue(MakeEntityCacheKey(compositeId, entityId), out entityNodes)
			&& entityNodes != null
			&& entityNodes.Count > 0;
	}

	private static bool ShouldSyncVisualForOwnerComposite(ShortGuid ownerCompositeId)
	{
		if (!PreviewVisibilitySettings.HideNestedScriptEntities)
			return true;

		return ownerCompositeId.AsUInt32 == PreviewVisibilitySettings.ActiveCompositeId;
	}

	private void ApplyTransformVisual(ParameterVisualContext context)
	{
		Node3D target = context.EntityNode;
		EntityOverride entityOverride = target as EntityOverride;
		if (entityOverride != null && entityOverride.PointedEntity != null)
			target = entityOverride.PointedEntity;

		if (context.Sync.removed)
		{
			GetEntityTransform(context.Entity, out Vector3 position, out Vector3 rotation);
			target.Position = position;
			target.RotationDegrees = rotation;
			EntityNodeUtil.SetPointed(target, false);
			return;
		}

		Vector3 pos = CathodeCoordinates.PositionToGodot(ParameterSync.ToVector3(context.Sync.vector3_a));
		Vector3 rot = CathodeCoordinates.EulerDegreesToGodot(ParameterSync.ToVector3(context.Sync.vector3_b));
		bool applyToPointed = entityOverride != null;
		bool pointed = applyToPointed || context.PointedOverride;
		EntityNodeUtil.SetPointed(target, pointed);
		if (!(EntityNodeUtil.IsPointed(target) && !context.FromPointer && !applyToPointed))
		{
			target.Position = pos;
			target.RotationDegrees = rot;
		}
	}

	public void ClearRenderableChildren(Node3D parent)
	{
		if (parent == null)
			return;

		foreach (Node child in parent.GetChildren().ToArray())
		{
			if (child is MeshInstance3D)
				child.QueueFree();
		}
	}

	public void SpawnRenderable(Node3D parent, Models.CS2.Component.LOD.Submesh submesh, Materials.Material material)
	{
		CreateRenderable(parent, submesh, material);
	}

	public void SetModelReferenceWireframe(bool enabled)
	{
		ModelReferenceRenderSettings.SetWireframe(enabled);
		ApplyModelReferenceWireframeToMeshes();
	}

	public void RepositionEntity(ShortGuid composite, ShortGuid entity, Vector3 position, Vector3 rotationDegrees, bool fromPointer, bool pointedPos)
	{
		string entityNodeName = entity.AsUInt32.ToString();
		if (_compositeNodes.ContainsKey(composite))
		{
			foreach (Node3D compositeInstance in _compositeNodes[composite])
			{
				if (compositeInstance == null || !GodotObject.IsInstanceValid(compositeInstance))
					continue;

				foreach (Node child in compositeInstance.GetChildren())
				{
					if (child.Name == entityNodeName && child is Node3D entityNode)
					{
						EntityNodeUtil.SetPointed(entityNode, pointedPos);
						if (!(EntityNodeUtil.IsPointed(entityNode) && !fromPointer))
						{
							entityNode.Position = CathodeCoordinates.PositionToGodot(position);
							entityNode.RotationDegrees = CathodeCoordinates.EulerDegreesToGodot(rotationDegrees);
						}
					}
				}
			}
		}
	}

	public void RemoveEntity(ShortGuid composite, ShortGuid entity)
	{
		string entityNodeName = entity.AsUInt32.ToString();
		if (_compositeNodes.ContainsKey(composite))
		{
			foreach (Node3D compositeInstance in _compositeNodes[composite])
			{
				if (compositeInstance == null || !GodotObject.IsInstanceValid(compositeInstance))
					continue;

				foreach (Node child in compositeInstance.GetChildren().ToArray())
				{
					if (child.Name == entityNodeName && child is Node3D entityNode)
					{
						EntityOverride entityOverride = entityNode as EntityOverride;
						if (entityOverride?.PointedEntity != null
							&& _nodeEntities.TryGetValue(entityOverride.PointedEntity, out Entity pointedEntity))
						{
							GetEntityTransform(pointedEntity, out Vector3 position, out Vector3 rotation);
							entityOverride.PointedEntity.Position = position;
							entityOverride.PointedEntity.RotationDegrees = rotation;
							EntityNodeUtil.SetPointed(entityOverride.PointedEntity, false);
						}

						UntrackEntityNode(composite, entity, entityNode);
						entityNode.QueueFree();
						_nodeEntities.Remove(entityNode);
						_functionEntityPreviewsCacheDirty = true;
					}
				}
			}
		}
	}

	public void UpdateRenderable(ShortGuid composite, ShortGuid entity, List<Tuple<int, int>> renderables)
	{
		FunctionEntity function = _content.Level.Commands.Entries
			.FirstOrDefault(o => o.shortGUID == composite)?
			.GetEntityByID(entity) as FunctionEntity;

		if (function != null)
			_content.RemappedResources[function] = renderables;

		if (TryGetCachedEntityNodes(composite, entity, out List<Node3D> entityNodes))
		{
			for (int i = 0; i < entityNodes.Count; i++)
				RefreshFunctionEntityPreviews(entityNodes[i]);
		}
	}

	private void CreateRenderable(Node3D parent, Models.CS2.Component.LOD.Submesh submesh, Materials.Material material)
	{
		MeshHolder holder = GetModel(submesh);
		if (holder == null || holder.MainMesh == null || holder.MainMesh.GetSurfaceCount() == 0)
		{
			GD.Print("Attempted to load non-parsed model. Skipping!");
			return;
		}

		if (!IsMaterialSupported(material))
			return;

		MeshInstance3D meshInstance = new MeshInstance3D
		{
			Name = holder.MainMesh.ResourceName + " (" + material.Name + ")",
			Mesh = holder.MainMesh,
			Visible = true,
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(meshInstance);
		meshInstance.AddToGroup("model_reference_renderable");
		meshInstance.TreeExited += () => _modelReferenceMeshes.Remove(meshInstance);
		_modelReferenceMeshes[meshInstance] = material;
		meshInstance.MaterialOverride = GetSolidMaterial(material);
		parent.AddChild(meshInstance);
		UpdateWireframeOverlay(meshInstance, material);
	}

	private bool GetEntityTransform(Entity entity, out Vector3 position, out Vector3 rotation)
	{
		position = Vector3.Zero;
		rotation = Vector3.Zero;
		if (entity == null) return false;

		Parameter positionParam = entity.GetParameter("position");
		if (positionParam != null && positionParam.content != null)
		{
			switch (positionParam.content.dataType)
			{
				case DataType.TRANSFORM:
					cTransform transform = (cTransform)positionParam.content;
					position = CathodeCoordinates.PositionToGodot(transform.position);
					rotation = CathodeCoordinates.EulerDegreesToGodot(transform.rotation);
					return true;
			}
		}
		return false;
	}

	private MeshHolder GetModel(Models.CS2.Component.LOD.Submesh submesh)
	{
		if (submesh == null)
			return null;

		if (!_modelMeshes.ContainsKey(submesh))
		{
			Models.CS2.Component.LOD lod = _content.Level.Models.FindModelLOD(submesh);
			Models.CS2 mesh = _content.Level.Models.FindModel(submesh);
			ArrayMesh arrayMesh = submesh.ToArrayMesh();
			arrayMesh.ResourceName = ((mesh == null) ? "" : mesh.Name) + ": " + ((lod == null) ? "" : lod.Name);

			submesh.Data = null;

			MeshHolder holder = new MeshHolder
			{
				MainMesh = arrayMesh,
				DefaultMaterial = submesh.Material,
			};
			_modelMeshes.Add(submesh, holder);
		}
		return _modelMeshes[submesh];
	}

	private bool IsMaterialSupported(Materials.Material material)
	{
		EnsureSolidMaterial(material);
		return _materialSupport[_materials[material]];
	}

	private StandardMaterial3D GetSolidMaterial(Materials.Material material)
	{
		EnsureSolidMaterial(material);
		return _materials[material];
	}

	private ShaderMaterial GetWireframeMaterial(Materials.Material material)
	{
		if (!_wireframeMaterials.ContainsKey(material))
		{
			int diffuseSampler = AlienSceneMaterials.GetDiffuseSamplerIndex(material.Shader);
			if (diffuseSampler < 0)
				return null;

			ShaderMaterial wireframe = AlienSceneMaterials.CreateWireframeMaterial(
				material,
				material.Shader,
				this,
				material.Name + " " + material.Shader.Ubershader,
				diffuseSampler);
			_wireframeMaterials.Add(material, wireframe);
		}

		return _wireframeMaterials[material];
	}

	private void EnsureSolidMaterial(Materials.Material material)
	{
		if (_materials.ContainsKey(material))
			return;

		AlienSceneMaterials.MaterialResult result = AlienSceneMaterials.GetMaterial(material, this);
		_materialSupport.Add(result.Material, result.Supported);
		_materials.Add(material, result.Material);
	}

	private const string WireframeOverlaySuffix = " WireframeOverlay";

	private void ApplyModelReferenceMaterial(MeshInstance3D solidMesh, Materials.Material material)
	{
		solidMesh.MaterialOverride = GetSolidMaterial(material);
		UpdateWireframeOverlay(solidMesh, material);
	}

	private void UpdateWireframeOverlay(MeshInstance3D solidMesh, Materials.Material material)
	{
		MeshInstance3D overlay = FindWireframeOverlay(solidMesh);
		if (!ModelReferenceRenderSettings.WireframeEnabled)
		{
			if (overlay != null)
				overlay.Visible = false;
			return;
		}

		if (overlay == null)
			overlay = CreateWireframeOverlay(solidMesh, material);

		if (overlay != null)
		{
			ShaderMaterial wireframe = GetWireframeMaterial(material);
			if (wireframe != null)
				overlay.MaterialOverride = wireframe;
			overlay.Visible = true;
		}
	}

	private MeshInstance3D FindWireframeOverlay(MeshInstance3D solidMesh)
	{
		if (solidMesh == null || !GodotObject.IsInstanceValid(solidMesh))
			return null;

		Node parent = solidMesh.GetParent();
		if (parent == null)
			return null;

		string overlayName = solidMesh.Name + WireframeOverlaySuffix;
		return parent.GetNodeOrNull<MeshInstance3D>(overlayName);
	}

	private MeshInstance3D CreateWireframeOverlay(MeshInstance3D solidMesh, Materials.Material material)
	{
		if (solidMesh?.Mesh == null)
			return null;

		Node parent = solidMesh.GetParent();
		if (parent == null)
			return null;

		MeshInstance3D overlay = new MeshInstance3D
		{
			Name = solidMesh.Name + WireframeOverlaySuffix,
			Mesh = solidMesh.Mesh,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = true,
		};
		LevelViewerMeshUtil.ConfigureMeshInstance(overlay);
		overlay.AddToGroup("model_reference_wireframe_overlay");
		parent.AddChild(overlay);
		parent.MoveChild(overlay, solidMesh.GetIndex() + 1);
		return overlay;
	}

	private void ApplyModelReferenceWireframeToMeshes()
	{
		foreach (KeyValuePair<MeshInstance3D, Materials.Material> entry in _modelReferenceMeshes.ToArray())
		{
			if (entry.Key == null || !GodotObject.IsInstanceValid(entry.Key))
			{
				_modelReferenceMeshes.Remove(entry.Key);
				continue;
			}

			UpdateWireframeOverlay(entry.Key, entry.Value);
		}
	}

	public Texture2D GetSamplerTexture(Materials.Material material, Shaders.Shader shader, int samplerIndex)
	{
		return GetDiffuseTexture(material, shader, samplerIndex);
	}

	public Texture2D GetDiffuseTexture(Materials.Material material, Shaders.Shader shader, int samplerIndex)
	{
		if (shader.SamplerRemaps.Count <= samplerIndex)
			return null;

		int diffuseMapIndex = shader.SamplerRemaps[samplerIndex];
		if (diffuseMapIndex == 255)
			return null;

		TexOrCube texture = GetTexOrCube(material.TextureReferences[diffuseMapIndex]);
		return texture?.Texture;
	}

	private TexOrCube GetTexOrCube(TexturePtr ptr)
	{
		if (!((ptr.Location == TexturePtr.Source.GLOBAL && !_texturesGlobal.ContainsKey(ptr.Texture)) ||
			  (ptr.Location == TexturePtr.Source.LEVEL && !_texturesLevel.ContainsKey(ptr.Texture))))
		{
			if (ptr.Location == TexturePtr.Source.GLOBAL)
				return _texturesGlobal[ptr.Texture];
			return _texturesLevel[ptr.Texture];
		}

		if (ptr.Texture == null) return null;
		Textures.TEX4.Texture texPart = ptr.Texture.TextureStreamed == null ? ptr.Texture.TexturePersistent : ptr.Texture.TextureStreamed;

		if (texPart.Content == null || texPart.Content.Length == 0)
			return null;

		TexOrCube tex = new TexOrCube();
		if (ptr.Texture.StateFlags.HasFlag(Textures.TextureStateFlag.CUBE))
		{
			Image.Format format = AlienSceneTextures.MapImageFormat(ptr.Texture.Format);
			if (format == Image.Format.Max)
			{
				GD.PrintErr("Unsupported cubemap texture format: " + ptr.Texture.Format);
				return null;
			}

			int faceByteCount = texPart.Content.Length / 6;
			byte[] faceData = new byte[faceByteCount];
			Array.Copy(texPart.Content, 0, faceData, 0, faceByteCount);
			Image image = AlienSceneTextures.CreateImageFromRaw(
				faceData,
				(int)texPart.Width,
				(int)texPart.Height,
				format,
				ptr.Texture.Format,
				ptr.Texture.Name);
			if (image != null && !image.IsEmpty())
				tex.Texture = ImageTexture.CreateFromImage(image);
		}
		else
		{
			tex.Texture = AlienSceneTextures.CreateTextureFromTexPart(texPart, ptr.Texture.Format, ptr.Texture.Name);
		}

		if (ptr.Texture.TextureStreamed != null)
			ptr.Texture.TextureStreamed.Content = null;
		if (ptr.Texture.TexturePersistent != null)
			ptr.Texture.TexturePersistent.Content = null;

		if (ptr.Location == TexturePtr.Source.GLOBAL)
			_texturesGlobal.Add(ptr.Texture, tex);
		else
			_texturesLevel.Add(ptr.Texture, tex);

		return tex;
	}

}

public class MeshHolder
{
	public ArrayMesh MainMesh;
	public Materials.Material DefaultMaterial;
}

public class LevelContent
{
	public void Load(string aiPath, string levelName)
	{
		Reset();

		if (Global == null)
			Global = new Global(aiPath + "\\DATA\\ENV\\GLOBAL", new PAK2(aiPath + "\\DATA\\GLOBAL\\ANIMATION.PAK"));
		Level = new Level(aiPath + "\\DATA\\ENV\\" + levelName, Global);

		foreach (Shaders.Shader shader in Level.Shaders.Entries)
		{
			shader.VertexShader = null;
			shader.PixelShader = null;
			shader.HullShader = null;
			shader.DomainShader = null;
			shader.GeometryShader = null;
			shader.ComputeShader = null;
		}
	}

	public void Reset()
	{
		Level = null;
	}

	public bool Loaded => Level?.Commands != null && Level.Commands.Loaded;

	public Level Level = null;
	public static Global Global = null;

	public Dictionary<Entity, List<Tuple<int, int>>> RemappedResources = new Dictionary<Entity, List<Tuple<int, int>>>();
}
