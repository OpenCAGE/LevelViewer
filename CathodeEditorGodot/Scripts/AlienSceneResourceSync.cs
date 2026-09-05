using CATHODE;
using CATHODE.Scripting;
using CATHODE.Scripting.Internal;
using CathodeLib;
using Godot;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static CATHODE.Models;

/// <summary>
/// LEVEL_RESOURCES_MODIFIED: models, materials, textures or shaders were edited in OpenCAGE, and the
/// tables that changed were written to a scratch folder for this side to pick up (see
/// ViewerResourceSync over there). Until this existed the only way to see an import was to reload the
/// level.
///
/// The snapshot is loaded off the main thread into tables of its own, then reconciled into the ones
/// the level already has rather than swapped for them: entity resources, REDS rows and every cache
/// here point at the existing objects, so an entry that survives keeps its object and takes the
/// snapshot's content, additions join the table, and what the snapshot no longer has leaves it.
/// Textures and models are matched by name, materials and shaders by position (they are only ever
/// appended to). Write indexes are then rebuilt from the entries on both sides alike, and the caches
/// keyed by them moved along. Only what actually changed is rebuilt in Godot: the meshes of a
/// replaced model, the materials that changed or sample a replaced texture, and the previews showing
/// any of those are respawned.
/// </summary>
public partial class AlienScene
{
	private readonly Queue<Packet> _pendingResourceSyncs = new Queue<Packet>();
	private bool _resourceSyncInFlight;

	//Where released model and texture binaries are re-read from once a snapshot has replaced the table:
	//the level's own pak no longer holds what is being shown
	private string _modelsRestorePath;
	private string _levelTexturesRestorePath;

	/// <summary>
	/// A snapshot is queued or being applied. The connection holds further packets until it is done,
	/// so anything sent after it (an entity resource naming a model it brought) sees the tables it
	/// describes.
	/// </summary>
	public bool IsResourceSyncBusy => _resourceSyncInFlight || _pendingResourceSyncs.Count > 0;

	public void QueueResourceSync(Packet packet)
	{
		if (packet == null)
			return;

		//Held until the level is in and populated; refused only when no level is loaded or on its way
		bool loadPending = _loadStep != LoadPipelineStep.None
			|| string.Equals(packet.level_name, _queuedLevelName, StringComparison.OrdinalIgnoreCase);
		if (!_content.Loaded && !loadPending)
		{
			ViewerLog.Print("Resource sync ignored: no level is loaded.");
			return;
		}

		_pendingResourceSyncs.Enqueue(packet);
		SetProcess(true);
	}

	/* A level reset drops what was queued for the old level. A snapshot for the level about to load
	   stays: it was sent for a level OpenCAGE already had, so it applies to the disk copy on its way in. */
	private void ResetResourceSyncState()
	{
		Packet[] pending = _pendingResourceSyncs.ToArray();
		_pendingResourceSyncs.Clear();
		foreach (Packet packet in pending)
		{
			if (!string.IsNullOrEmpty(_queuedLevelName)
				&& string.Equals(packet.level_name, _queuedLevelName, StringComparison.OrdinalIgnoreCase))
				_pendingResourceSyncs.Enqueue(packet);
		}
		_modelsRestorePath = null;
		_levelTexturesRestorePath = null;
	}

	private void AdvanceResourceSync()
	{
		if (_resourceSyncInFlight || _pendingResourceSyncs.Count == 0 || _loadStep != LoadPipelineStep.None || !_content.Loaded)
			return;

		BeginResourceSync(_pendingResourceSyncs.Dequeue());
	}

	private sealed class ResourceSnapshot
	{
		public Textures Textures;
		public Shaders Shaders;
		public Materials Materials;
		public Models Models;
		public string Error;
		public double LoadMs;
	}

	private void BeginResourceSync(Packet packet)
	{
		Level level = _content.Level;
		if (level == null || !string.Equals(level.Name, packet.level_name, StringComparison.OrdinalIgnoreCase))
		{
			ViewerLog.Print("Resource sync ignored: it describes " + packet.level_name + ", this is " + level?.Name + ".");
			return;
		}

		_resourceSyncInFlight = true;
		int generation = _contentGeneration;

		//The snapshot's loaders resolve cross-table references by write index against whichever tables
		//it did not replace, so those must index their entries the way the sender's do. A load leaves
		//entries imported afterwards (the global textures) unindexed; both sides rebuild instead.
		RebuildResourceWriteLists(null, null, new List<MeshHolder>(), new List<TexOrCube>());

		Task.Run(() =>
		{
			ResourceSnapshot snapshot = LoadResourceSnapshot(packet, level);
			Callable.From(() => FinishResourceSync(packet, snapshot, generation)).CallDeferred();
		});
	}

	/* Off the main thread: the paks are read whole, and LEVEL_TEXTURES alone can be half a gigabyte. */
	private static ResourceSnapshot LoadResourceSnapshot(Packet packet, Level level)
	{
		ResourceSnapshot snapshot = new ResourceSnapshot();
		Stopwatch stopwatch = Stopwatch.StartNew();
		try
		{
			if (!string.IsNullOrEmpty(packet.resource_sync_textures))
			{
				snapshot.Textures = new Textures(packet.resource_sync_textures);
				if (!snapshot.Textures.Loaded)
					throw new Exception("textures did not load from " + packet.resource_sync_textures);
			}
			if (!string.IsNullOrEmpty(packet.resource_sync_shaders))
			{
				snapshot.Shaders = new Shaders(packet.resource_sync_shaders);
				if (!snapshot.Shaders.Loaded)
					throw new Exception("shaders did not load from " + packet.resource_sync_shaders);
			}
			if (!string.IsNullOrEmpty(packet.resource_sync_materials))
			{
				snapshot.Materials = new Materials(
					packet.resource_sync_materials,
					LevelContent.Global?.Textures,
					snapshot.Textures ?? level.Textures,
					snapshot.Shaders ?? level.Shaders);
				if (!snapshot.Materials.Loaded)
					throw new Exception("materials did not load from " + packet.resource_sync_materials);
			}
			if (!string.IsNullOrEmpty(packet.resource_sync_models))
			{
				snapshot.Models = new Models(
					packet.resource_sync_models,
					snapshot.Materials ?? level.Materials,
					level.WeightedCollisions,
					level.MorphTargetDB);
				if (!snapshot.Models.Loaded)
					throw new Exception("models did not load from " + packet.resource_sync_models);
			}
		}
		catch (Exception ex)
		{
			snapshot.Error = ex.Message;
		}

		snapshot.LoadMs = stopwatch.Elapsed.TotalMilliseconds;
		return snapshot;
	}

	private void FinishResourceSync(Packet packet, ResourceSnapshot snapshot, int generation)
	{
		bool readWholePaks = snapshot.Textures != null || snapshot.Models != null;
		try
		{
			if (generation != _contentGeneration || !_content.Loaded)
				ViewerLog.Print("Resource sync dropped: the level changed while its snapshot loaded.");
			else if (snapshot.Error != null)
				ViewerLog.PrintErr("Resource sync failed to load: " + snapshot.Error);
			else
				ApplyResourceSnapshot(packet, snapshot);
		}
		catch (Exception ex)
		{
			ViewerLog.PrintErr("[Viewer] Resource sync failed: " + ex);
		}
		finally
		{
			_resourceSyncInFlight = false;
			snapshot.Textures = null;
			snapshot.Shaders = null;
			snapshot.Materials = null;
			snapshot.Models = null;
			if (readWholePaks)
				CollectReleasedSourceData(); //the whole-pak buffers are garbage now, and they are big
			PruneResourceSnapshotFolders(packet);
		}
	}

	private void ApplyResourceSnapshot(Packet packet, ResourceSnapshot snapshot)
	{
		Level level = _content.Level;
		Stopwatch stopwatch = Stopwatch.StartNew();

		//Snapshot object -> the live object standing for it, for rewriting references between tables
		Dictionary<Textures.TEX4, Textures.TEX4> textureMap = new Dictionary<Textures.TEX4, Textures.TEX4>(ReferenceEqualityComparer.Instance);
		Dictionary<Shaders.Shader, Shaders.Shader> shaderMap = new Dictionary<Shaders.Shader, Shaders.Shader>(ReferenceEqualityComparer.Instance);
		Dictionary<Materials.Material, Materials.Material> materialMap = new Dictionary<Materials.Material, Materials.Material>(ReferenceEqualityComparer.Instance);

		HashSet<Textures.TEX4> changedTextures = new HashSet<Textures.TEX4>(ReferenceEqualityComparer.Instance);
		HashSet<Shaders.Shader> changedShaders = new HashSet<Shaders.Shader>(ReferenceEqualityComparer.Instance);
		HashSet<Materials.Material> changedMaterials = new HashSet<Materials.Material>(ReferenceEqualityComparer.Instance);
		HashSet<CS2.Component.LOD.Submesh> changedSubmeshes = new HashSet<CS2.Component.LOD.Submesh>(ReferenceEqualityComparer.Instance);

		//Material keys its dictionaries by value, and that value takes in its shader, so every cache
		//lookup for what is about to change has to happen before anything is written into it: the
		//reconcile passes only decide, and queue the writes for after the caches have let go.
		List<Action> pendingWrites = new List<Action>();
		ReconcileCounts counts = new ReconcileCounts();

		if (snapshot.Textures != null)
			ReconcileTextures(level.Textures, snapshot.Textures, packet.resource_changed_textures, textureMap, changedTextures, pendingWrites, counts);
		if (snapshot.Shaders != null)
			ReconcileShaders(level.Shaders, snapshot.Shaders, shaderMap, changedShaders, pendingWrites, counts);
		if (snapshot.Materials != null)
			ReconcileMaterials(level.Materials, snapshot.Materials, textureMap, shaderMap, materialMap, changedMaterials, pendingWrites, counts);

		//A material draws with its shader's sampler layout and its textures' pixels: a change to either is a change to it
		if (changedTextures.Count > 0 || changedShaders.Count > 0)
		{
			foreach (Materials.Material material in level.Materials.Entries)
			{
				if (material == null || changedMaterials.Contains(material))
					continue;
				if (changedShaders.Contains(material.Shader) || material.TextureReferences.Any(r => r?.Texture != null && changedTextures.Contains(r.Texture)))
					changedMaterials.Add(material);
			}
		}

		if (snapshot.Models != null)
			ReconcileModels(level.Models, snapshot.Models, packet.resource_changed_models, materialMap, changedSubmeshes, pendingWrites, counts);

		//A material this side could not shade gave its previews nothing to show; if it can be now, they are revisited
		bool anyPreviouslyUnsupported = changedMaterials.Any(m =>
			_materials.TryGetValue(m, out ShaderMaterial shaded) && _materialSupport.TryGetValue(shaded, out bool supported) && !supported);
		List<ShaderMaterial> retiredMaterials = EvictGodotMaterials(changedMaterials);

		for (int i = 0; i < pendingWrites.Count; i++)
			pendingWrites[i]();

		List<MeshHolder> retiredMeshes = new List<MeshHolder>();
		List<TexOrCube> retiredTextures = new List<TexOrCube>();
		RebuildResourceWriteLists(changedSubmeshes, changedTextures, retiredMeshes, retiredTextures);

		HashSet<Mesh> staleMeshes = new HashSet<Mesh>(ReferenceEqualityComparer.Instance);
		foreach (MeshHolder holder in retiredMeshes)
		{
			if (holder?.MainMesh != null)
				staleMeshes.Add(holder.MainMesh);
		}

		HashSet<ModelReferencePreview> affected = new HashSet<ModelReferencePreview>();
		CollectPreviewsShowing(changedMaterials, staleMeshes, affected);
		if (changedSubmeshes.Count > 0 || retiredMeshes.Count > 0 || anyPreviouslyUnsupported)
			CollectPreviewsReferencing(changedSubmeshes, changedMaterials, affected);

		int refreshed = 0;
		foreach (ModelReferencePreview preview in affected)
		{
			if (preview == null || !GodotObject.IsInstanceValid(preview) || !preview.IsInsideTree())
				continue;

			ForgetRenderableChildren(preview.GetPopulateRenderTarget());
			preview.Refresh();
			preview.SyncPickablesWithVisibility();
			refreshed++;
		}

		//Nothing shows the old resources any more (the freed mesh instances keep their own reference until the frame ends)
		foreach (MeshHolder holder in retiredMeshes)
		{
			if (holder?.MainMesh != null && GodotObject.IsInstanceValid(holder.MainMesh))
				holder.MainMesh.Dispose();
		}
		foreach (TexOrCube texture in retiredTextures)
			DisposeCachedTexture(texture);
		foreach (ShaderMaterial material in retiredMaterials)
		{
			if (material != null && GodotObject.IsInstanceValid(material))
				material.Dispose();
		}

		if (snapshot.Models != null)
			_modelsRestorePath = packet.resource_sync_models;
		if (snapshot.Textures != null)
			_levelTexturesRestorePath = packet.resource_sync_textures;

		if (refreshed > 0)
			RefreshEntityHighlights(forceRebuild: true);

		ViewerLog.Print("Resource sync applied (" + snapshot.LoadMs.ToString("0") + " ms load, " + stopwatch.Elapsed.TotalMilliseconds.ToString("0") + " ms apply):"
			+ (snapshot.Textures != null ? " textures +" + counts.TexturesAdded + " -" + counts.TexturesRemoved + " ~" + changedTextures.Count : "")
			+ (snapshot.Shaders != null ? " shaders +" + counts.ShadersAdded + " -" + counts.ShadersRemoved + " ~" + changedShaders.Count : "")
			+ (snapshot.Materials != null ? " materials +" + counts.MaterialsAdded + " -" + counts.MaterialsRemoved : "")
			+ " (" + changedMaterials.Count + " rebuilt)"
			+ (snapshot.Models != null ? " models +" + counts.ModelsAdded + " -" + counts.ModelsRemoved + " ~" + changedSubmeshes.Count + " submeshes" : "")
			+ "; " + refreshed + " model references respawned.");
	}

	private sealed class ReconcileCounts
	{
		public int TexturesAdded, TexturesRemoved, ShadersAdded, ShadersRemoved, MaterialsAdded, MaterialsRemoved, ModelsAdded, ModelsRemoved;
	}

	#region RECONCILE
	/* Textures are matched by name. An existing one keeps its object (materials point at it) and takes
	   the snapshot's content when the sender says its binary was replaced or its header differs. */
	private static void ReconcileTextures(
		Textures live,
		Textures snapshot,
		List<string> replacedNames,
		Dictionary<Textures.TEX4, Textures.TEX4> map,
		HashSet<Textures.TEX4> changed,
		List<Action> pendingWrites,
		ReconcileCounts counts)
	{
		Dictionary<string, Textures.TEX4> liveByName = new Dictionary<string, Textures.TEX4>(StringComparer.OrdinalIgnoreCase);
		foreach (Textures.TEX4 texture in live.Entries)
		{
			if (texture != null)
				liveByName[NormaliseTextureName(texture.Name)] = texture;
		}

		HashSet<string> replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (replacedNames != null)
		{
			foreach (string name in replacedNames)
				replaced.Add(NormaliseTextureName(name));
		}

		List<Textures.TEX4> entries = new List<Textures.TEX4>(snapshot.Entries.Count);
		foreach (Textures.TEX4 incoming in snapshot.Entries)
		{
			if (incoming == null)
				continue;

			string key = NormaliseTextureName(incoming.Name);
			if (liveByName.TryGetValue(key, out Textures.TEX4 existing))
			{
				liveByName.Remove(key);
				if (replaced.Contains(key) || !TextureHeaderEqual(existing, incoming))
				{
					changed.Add(existing);
					pendingWrites.Add(() => CopyTextureInto(incoming, existing));
				}
				map[incoming] = existing;
				entries.Add(existing);
			}
			else
			{
				map[incoming] = incoming;
				entries.Add(incoming);
				counts.TexturesAdded++;
			}
		}

		counts.TexturesRemoved = liveByName.Count;
		pendingWrites.Add(() =>
		{
			live.Entries.Clear();
			live.Entries.AddRange(entries);
		});
	}

	private static string NormaliseTextureName(string name)
	{
		return (name ?? "").Replace('\\', '/');
	}

	private static bool TextureHeaderEqual(Textures.TEX4 a, Textures.TEX4 b)
	{
		return a.Format == b.Format
			&& a.StateFlags == b.StateFlags
			&& a.UsageFlags == b.UsageFlags
			&& TexturePartHeaderEqual(a.TexturePersistent, b.TexturePersistent)
			&& TexturePartHeaderEqual(a.TextureStreamed, b.TextureStreamed);
	}

	private static bool TexturePartHeaderEqual(Textures.TEX4.Texture a, Textures.TEX4.Texture b)
	{
		if (a == null || b == null)
			return a == null && b == null;
		return a.Width == b.Width && a.Height == b.Height && a.Depth == b.Depth && a.MipLevels == b.MipLevels;
	}

	private static void CopyTextureInto(Textures.TEX4 source, Textures.TEX4 target)
	{
		target.Name = source.Name;
		target.Format = source.Format;
		target.StateFlags = source.StateFlags;
		target.UsageFlags = source.UsageFlags;
		target.TexturePersistent = CopyTexturePart(source.TexturePersistent, target.TexturePersistent);
		target.TextureStreamed = CopyTexturePart(source.TextureStreamed, target.TextureStreamed);
	}

	private static Textures.TEX4.Texture CopyTexturePart(Textures.TEX4.Texture source, Textures.TEX4.Texture target)
	{
		if (source == null)
			return null;

		target ??= new Textures.TEX4.Texture();
		target.Width = source.Width;
		target.Height = source.Height;
		target.Depth = source.Depth;
		target.MipLevels = source.MipLevels;
		target.Content = source.Content; //the snapshot is discarded, so its buffer is ours to keep
		return target;
	}

	/* Shaders are matched by position; an entry is compared on the metadata this side renders from
	   (the bytecode was dropped after the level loaded, and is dropped from the snapshot here). */
	private static void ReconcileShaders(
		Shaders live,
		Shaders snapshot,
		Dictionary<Shaders.Shader, Shaders.Shader> map,
		HashSet<Shaders.Shader> changed,
		List<Action> pendingWrites,
		ReconcileCounts counts)
	{
		for (int i = 0; i < snapshot.Entries.Count; i++)
		{
			Shaders.Shader incoming = snapshot.Entries[i];
			if (incoming == null)
				continue;

			ClearShaderBytecode(incoming);
			if (i < live.Entries.Count)
			{
				Shaders.Shader existing = live.Entries[i];
				if (!ShaderMetadataEqual(existing, incoming))
				{
					changed.Add(existing);
					pendingWrites.Add(() => CopyShaderInto(incoming, existing));
				}
				map[incoming] = existing;
			}
			else
			{
				map[incoming] = incoming;
				pendingWrites.Add(() => live.Entries.Add(incoming));
				counts.ShadersAdded++;
			}
		}

		for (int i = snapshot.Entries.Count; i < live.Entries.Count; i++)
		{
			changed.Add(live.Entries[i]);
			counts.ShadersRemoved++;
		}
		if (snapshot.Entries.Count < live.Entries.Count)
			pendingWrites.Add(() => live.Entries.RemoveRange(snapshot.Entries.Count, live.Entries.Count - snapshot.Entries.Count));
	}

	private static void ClearShaderBytecode(Shaders.Shader shader)
	{
		shader.VertexShader = null;
		shader.PixelShader = null;
		shader.HullShader = null;
		shader.DomainShader = null;
		shader.GeometryShader = null;
		shader.ComputeShader = null;
	}

	private static bool ShaderMetadataEqual(Shaders.Shader a, Shaders.Shader b)
	{
		if (a.Ubershader != b.Ubershader
			|| a.RequiredShaderModel != b.RequiredShaderModel
			|| a.UbershaderFeatureFlags != b.UbershaderFeatureFlags
			|| a.UbershaderRequirementFlags != b.UbershaderRequirementFlags
			|| a.CycleCount != b.CycleCount
			|| a.RegisterCount != b.RegisterCount
			|| a.PermutationHash != b.PermutationHash
			|| a.Technique != b.Technique)
			return false;

		if (!ListsEqual(a.SamplerStageBindings, b.SamplerStageBindings)
			|| !ListsEqual(a.SamplerRemaps, b.SamplerRemaps)
			|| !ListsEqual(a.EngineParameterRemaps, b.EngineParameterRemaps)
			|| !ListsEqual(a.VertexShaderParameterRemaps, b.VertexShaderParameterRemaps)
			|| !ListsEqual(a.PixelShaderParameterRemaps, b.PixelShaderParameterRemaps)
			|| !ListsEqual(a.HullShaderParameterRemaps, b.HullShaderParameterRemaps)
			|| !ListsEqual(a.DomainShaderParameterRemaps, b.DomainShaderParameterRemaps))
			return false;

		int samplerCount = a.Samplers?.Count ?? 0;
		if (samplerCount != (b.Samplers?.Count ?? 0))
			return false;
		for (int i = 0; i < samplerCount; i++)
		{
			if (!Equals(a.Samplers[i], b.Samplers[i]))
				return false;
		}

		return Equals(a.RenderStates, b.RenderStates);
	}

	private static bool ListsEqual<T>(List<T> a, List<T> b)
	{
		if (a == null || b == null)
			return a == null && b == null;
		return a.SequenceEqual(b);
	}

	private static void CopyShaderInto(Shaders.Shader source, Shaders.Shader target)
	{
		target.Technique = source.Technique;
		target.Ubershader = source.Ubershader;
		target.RequiredShaderModel = source.RequiredShaderModel;
		target.UbershaderFeatureFlags = source.UbershaderFeatureFlags;
		target.UbershaderRequirementFlags = source.UbershaderRequirementFlags;
		target.CycleCount = source.CycleCount;
		target.RegisterCount = source.RegisterCount;
		target.PermutationHash = source.PermutationHash;
		target.Samplers = source.Samplers;
		target.SamplerStageBindings = source.SamplerStageBindings;
		target.SamplerRemaps = source.SamplerRemaps;
		target.EngineParameterRemaps = source.EngineParameterRemaps;
		target.VertexShaderParameterRemaps = source.VertexShaderParameterRemaps;
		target.PixelShaderParameterRemaps = source.PixelShaderParameterRemaps;
		target.HullShaderParameterRemaps = source.HullShaderParameterRemaps;
		target.DomainShaderParameterRemaps = source.DomainShaderParameterRemaps;
		target.RenderStates = source.RenderStates;
	}

	/* Materials are matched by position. The snapshot's references are first pointed at the live
	   textures and shaders they stand for, after which Material's own equality is the right test. */
	private static void ReconcileMaterials(
		Materials live,
		Materials snapshot,
		Dictionary<Textures.TEX4, Textures.TEX4> textureMap,
		Dictionary<Shaders.Shader, Shaders.Shader> shaderMap,
		Dictionary<Materials.Material, Materials.Material> map,
		HashSet<Materials.Material> changed,
		List<Action> pendingWrites,
		ReconcileCounts counts)
	{
		for (int i = 0; i < snapshot.Entries.Count; i++)
		{
			Materials.Material incoming = snapshot.Entries[i];
			if (incoming == null)
				continue;

			incoming.Shader = MapReference(shaderMap, incoming.Shader);
			foreach (TexturePtr reference in incoming.TextureReferences)
			{
				if (reference != null)
					reference.Texture = MapReference(textureMap, reference.Texture);
			}

			if (i < live.Entries.Count)
			{
				Materials.Material existing = live.Entries[i];
				if (!(existing == incoming))
				{
					changed.Add(existing);
					pendingWrites.Add(() => CopyMaterialInto(incoming, existing));
				}
				map[incoming] = existing;
			}
			else
			{
				map[incoming] = incoming;
				pendingWrites.Add(() => live.Entries.Add(incoming));
				counts.MaterialsAdded++;
			}
		}

		for (int i = snapshot.Entries.Count; i < live.Entries.Count; i++)
		{
			changed.Add(live.Entries[i]);
			counts.MaterialsRemoved++;
		}
		if (snapshot.Entries.Count < live.Entries.Count)
			pendingWrites.Add(() => live.Entries.RemoveRange(snapshot.Entries.Count, live.Entries.Count - snapshot.Entries.Count));
	}

	private static T MapReference<T>(Dictionary<T, T> map, T reference) where T : class
	{
		return reference != null && map.TryGetValue(reference, out T mapped) ? mapped : reference;
	}

	private static void CopyMaterialInto(Materials.Material source, Materials.Material target)
	{
		target.Name = source.Name;
		target.TextureReferences = source.TextureReferences;
		target.EngineConstants = source.EngineConstants;
		target.VertexShaderConstants = source.VertexShaderConstants;
		target.PixelShaderConstants = source.PixelShaderConstants;
		target.HullShaderConstants = source.HullShaderConstants;
		target.DomainShaderConstants = source.DomainShaderConstants;
		target.OfflineLightFeatures = source.OfflineLightFeatures;
		target.Shader = source.Shader;
		target.PhysicalMaterialIndex = source.PhysicalMaterialIndex;
		target.EnvironmentMapIndex = source.EnvironmentMapIndex;
		target.Priority = source.Priority;
	}

	/* Models are matched by name and, within one, by position. A model the sender says was replaced
	   has every submesh rebuilt; otherwise a submesh is rebuilt when its header differs. A submesh's
	   object is what REDS rows and entity resources hold, so it is kept and given the new content. */
	private static void ReconcileModels(
		Models live,
		Models snapshot,
		List<string> replacedNames,
		Dictionary<Materials.Material, Materials.Material> materialMap,
		HashSet<CS2.Component.LOD.Submesh> changed,
		List<Action> pendingWrites,
		ReconcileCounts counts)
	{
		Dictionary<string, CS2> liveByName = new Dictionary<string, CS2>(StringComparer.Ordinal);
		foreach (CS2 model in live.Entries)
		{
			if (model?.Name != null)
				liveByName[model.Name] = model;
		}

		HashSet<string> replaced = new HashSet<string>(replacedNames ?? new List<string>(), StringComparer.Ordinal);
		List<CS2> entries = new List<CS2>(snapshot.Entries.Count);
		foreach (CS2 incoming in snapshot.Entries)
		{
			if (incoming == null)
				continue;

			if (incoming.Name != null && liveByName.TryGetValue(incoming.Name, out CS2 existing))
			{
				liveByName.Remove(incoming.Name);
				ReconcileModel(existing, incoming, replaced.Contains(incoming.Name), materialMap, changed, pendingWrites);
				entries.Add(existing);
			}
			else
			{
				foreach (CS2.Component component in incoming.Components)
					RemapComponentMaterials(component, materialMap);
				entries.Add(incoming);
				counts.ModelsAdded++;
			}
		}

		counts.ModelsRemoved = liveByName.Count;
		pendingWrites.Add(() =>
		{
			live.Entries.Clear();
			live.Entries.AddRange(entries);
		});
	}

	private static void ReconcileModel(
		CS2 existing,
		CS2 incoming,
		bool replaced,
		Dictionary<Materials.Material, Materials.Material> materialMap,
		HashSet<CS2.Component.LOD.Submesh> changed,
		List<Action> pendingWrites)
	{
		for (int c = 0; c < incoming.Components.Count; c++)
		{
			CS2.Component incomingComponent = incoming.Components[c];
			if (c >= existing.Components.Count)
			{
				RemapComponentMaterials(incomingComponent, materialMap);
				pendingWrites.Add(() => existing.Components.Add(incomingComponent));
				continue;
			}

			CS2.Component existingComponent = existing.Components[c];
			for (int l = 0; l < incomingComponent.LODs.Count; l++)
			{
				CS2.Component.LOD incomingLod = incomingComponent.LODs[l];
				if (l >= existingComponent.LODs.Count)
				{
					foreach (CS2.Component.LOD.Submesh submesh in incomingLod.Submeshes)
						submesh.Material = MapReference(materialMap, submesh.Material);
					pendingWrites.Add(() => existingComponent.LODs.Add(incomingLod));
					continue;
				}

				CS2.Component.LOD existingLod = existingComponent.LODs[l];
				pendingWrites.Add(() => existingLod.Name = incomingLod.Name);
				for (int s = 0; s < incomingLod.Submeshes.Count; s++)
				{
					CS2.Component.LOD.Submesh incomingSubmesh = incomingLod.Submeshes[s];
					incomingSubmesh.Material = MapReference(materialMap, incomingSubmesh.Material);
					if (s >= existingLod.Submeshes.Count)
					{
						pendingWrites.Add(() => existingLod.Submeshes.Add(incomingSubmesh));
						continue;
					}

					CS2.Component.LOD.Submesh existingSubmesh = existingLod.Submeshes[s];
					if (replaced || !SubmeshHeaderEqual(existingSubmesh, incomingSubmesh))
					{
						changed.Add(existingSubmesh);
						pendingWrites.Add(() => CopySubmeshInto(incomingSubmesh, existingSubmesh));
					}
				}
				if (existingLod.Submeshes.Count > incomingLod.Submeshes.Count)
					pendingWrites.Add(() => existingLod.Submeshes.RemoveRange(incomingLod.Submeshes.Count, existingLod.Submeshes.Count - incomingLod.Submeshes.Count));
			}
			if (existingComponent.LODs.Count > incomingComponent.LODs.Count)
				pendingWrites.Add(() => existingComponent.LODs.RemoveRange(incomingComponent.LODs.Count, existingComponent.LODs.Count - incomingComponent.LODs.Count));
		}
		if (existing.Components.Count > incoming.Components.Count)
			pendingWrites.Add(() => existing.Components.RemoveRange(incoming.Components.Count, existing.Components.Count - incoming.Components.Count));
	}

	private static void RemapComponentMaterials(CS2.Component component, Dictionary<Materials.Material, Materials.Material> materialMap)
	{
		foreach (CS2.Component.LOD lod in component.LODs)
		{
			foreach (CS2.Component.LOD.Submesh submesh in lod.Submeshes)
				submesh.Material = MapReference(materialMap, submesh.Material);
		}
	}

	/* Everything but the vertex buffer, which this side released after building the mesh. */
	private static bool SubmeshHeaderEqual(CS2.Component.LOD.Submesh a, CS2.Component.LOD.Submesh b)
	{
		return a.MinBounds == b.MinBounds
			&& a.MaxBounds == b.MaxBounds
			&& a.MinLODRange == b.MinLODRange
			&& a.MaxLODRange == b.MaxLODRange
			&& a.RenderFlags == b.RenderFlags
			&& ReferenceEquals(a.Material, b.Material)
			&& a.CollisionProxyIndex == b.CollisionProxyIndex
			&& ReferenceEquals(a.WeightedCollision, b.WeightedCollision)
			&& ReferenceEquals(a.MorphAnimSet, b.MorphAnimSet)
			&& VertexFormatEqual(a.VertexFormatFull, b.VertexFormatFull)
			&& VertexFormatEqual(a.VertexFormatPartial, b.VertexFormatPartial)
			&& a.VertexScale == b.VertexScale
			&& a.VertexCount == b.VertexCount
			&& a.IndexCount == b.IndexCount
			&& ListsEqual(a.Bones, b.Bones);
	}

	//VertexFormat's own == dereferences its left operand
	private static bool VertexFormatEqual(VertexFormat a, VertexFormat b)
	{
		if (ReferenceEquals(a, null) || ReferenceEquals(b, null))
			return ReferenceEquals(a, null) && ReferenceEquals(b, null);
		return a.Equals(b);
	}

	private static void CopySubmeshInto(CS2.Component.LOD.Submesh source, CS2.Component.LOD.Submesh target)
	{
		target.MinBounds = source.MinBounds;
		target.MaxBounds = source.MaxBounds;
		target.MinLODRange = source.MinLODRange;
		target.MaxLODRange = source.MaxLODRange;
		target.RenderFlags = source.RenderFlags;
		target.Material = source.Material;
		target.CollisionProxyIndex = source.CollisionProxyIndex;
		target.WeightedCollision = source.WeightedCollision;
		target.MorphAnimSet = source.MorphAnimSet;
		target.VertexFormatFull = source.VertexFormatFull;
		target.VertexFormatPartial = source.VertexFormatPartial;
		target.VertexScale = source.VertexScale;
		target.VertexCount = source.VertexCount;
		target.IndexCount = source.IndexCount;
		target.Bones = source.Bones;
		target.Data = source.Data; //kept until the mesh is built from it, like any other unconverted submesh
	}
	#endregion

	#region GODOT CACHES
	/// <summary>
	/// Bring every table's write index up to date with its entries and move the caches keyed by
	/// those indexes along with them. Entries in the evict sets, or no longer indexed at all, come
	/// out; their Godot resources are handed back to be disposed once nothing is showing them.
	/// </summary>
	private void RebuildResourceWriteLists(
		HashSet<CS2.Component.LOD.Submesh> evictSubmeshes,
		HashSet<Textures.TEX4> evictTextures,
		List<MeshHolder> retiredMeshes,
		List<TexOrCube> retiredTextures)
	{
		Level level = _content.Level;

		Dictionary<int, CS2.Component.LOD.Submesh> submeshByOldIndex = new Dictionary<int, CS2.Component.LOD.Submesh>();
		foreach (KeyValuePair<CS2.Component.LOD.Submesh, int> entry in _submeshWriteIndexByReference)
			submeshByOldIndex[entry.Value] = entry.Key;

		Dictionary<int, Textures.TEX4> textureByOldIndex = new Dictionary<int, Textures.TEX4>();
		for (int i = 0; ; i++)
		{
			Textures.TEX4 texture = level.Textures.GetAtWriteIndex(i);
			if (texture == null)
				break;
			textureByOldIndex[i] = texture;
		}

		Dictionary<int, Materials.Material> materialByOldIndex = new Dictionary<int, Materials.Material>();
		for (int i = 0; ; i++)
		{
			Materials.Material material = level.Materials.GetAtWriteIndex(i);
			if (material == null)
				break;
			materialByOldIndex[i] = material;
		}

		level.Textures.RebuildWriteList();
		level.Shaders.RebuildWriteList();
		level.Materials.RebuildWriteList();
		level.Models.RebuildWriteList();
		BuildSubmeshWriteIndexCache();

		Dictionary<int, MeshHolder> meshes = new Dictionary<int, MeshHolder>();
		foreach (KeyValuePair<int, MeshHolder> entry in _modelMeshesByWriteIndex)
		{
			if (submeshByOldIndex.TryGetValue(entry.Key, out CS2.Component.LOD.Submesh submesh)
				&& !(evictSubmeshes?.Contains(submesh) ?? false)
				&& _submeshWriteIndexByReference.TryGetValue(submesh, out int newIndex))
			{
				meshes[newIndex] = entry.Value;
			}
			else
			{
				retiredMeshes.Add(entry.Value);
			}
		}
		_modelMeshesByWriteIndex.Clear();
		foreach (KeyValuePair<int, MeshHolder> entry in meshes)
			_modelMeshesByWriteIndex[entry.Key] = entry.Value;

		Dictionary<int, TexOrCube> textures = new Dictionary<int, TexOrCube>();
		foreach (KeyValuePair<int, TexOrCube> entry in _texturesLevelByIndex)
		{
			int newIndex;
			if (textureByOldIndex.TryGetValue(entry.Key, out Textures.TEX4 texture)
				&& !(evictTextures?.Contains(texture) ?? false)
				&& (newIndex = level.Textures.GetWriteIndex(texture)) >= 0)
			{
				textures[newIndex] = entry.Value;
			}
			else
			{
				retiredTextures.Add(entry.Value);
			}
		}
		_texturesLevelByIndex.Clear();
		foreach (KeyValuePair<int, TexOrCube> entry in textures)
			_texturesLevelByIndex[entry.Key] = entry.Value;

		//Resources synced from OpenCAGE were resolved to indexes when they arrived
		foreach (KeyValuePair<Entity, List<Tuple<int, int>>> entry in _content.RemappedResources.ToArray())
		{
			List<Tuple<int, int>> remapped = new List<Tuple<int, int>>(entry.Value.Count);
			foreach (Tuple<int, int> renderable in entry.Value)
			{
				int modelIndex = submeshByOldIndex.TryGetValue(renderable.Item1, out CS2.Component.LOD.Submesh submesh)
					&& _submeshWriteIndexByReference.TryGetValue(submesh, out int newModelIndex)
					? newModelIndex
					: -1;
				int materialIndex = materialByOldIndex.TryGetValue(renderable.Item2, out Materials.Material material)
					? level.Materials.GetWriteIndex(material)
					: -1;
				if (modelIndex >= 0 && materialIndex >= 0)
					remapped.Add(new Tuple<int, int>(modelIndex, materialIndex));
			}
			_content.RemappedResources[entry.Key] = remapped;
		}

		InvalidateModelRefRenderablesCache();
	}

	private List<ShaderMaterial> EvictGodotMaterials(HashSet<Materials.Material> materials)
	{
		List<ShaderMaterial> retired = new List<ShaderMaterial>();
		foreach (Materials.Material material in materials)
		{
			if (_materials.TryGetValue(material, out ShaderMaterial shaded))
			{
				_materials.Remove(material);
				_materialSupport.Remove(shaded);
				retired.Add(shaded);
			}
			if (_wireframeMaterials.TryGetValue(material, out ShaderMaterial wireframe))
			{
				_wireframeMaterials.Remove(material);
				retired.Add(wireframe);
			}
		}

		foreach (KeyValuePair<ulong, Materials.Material> entry in _modelReferenceOverrideMaterialSources.ToArray())
		{
			if (!materials.Contains(entry.Value))
				continue;

			if (_modelReferenceOverrideMaterials.TryGetValue(entry.Key, out ShaderMaterial overridden))
			{
				_modelReferenceOverrideMaterials.Remove(entry.Key);
				retired.Add(overridden);
			}
			_modelReferenceOverrideMaterialSources.Remove(entry.Key);
		}

		return retired;
	}

	private static void DisposeCachedTexture(TexOrCube texture)
	{
		if (texture == null)
			return;

		if (texture.Texture != null && GodotObject.IsInstanceValid(texture.Texture))
		{
			AlienSceneTextures.ForgetTransparency(texture.Texture);
			texture.Texture.Dispose();
		}
		if (texture.Cubemap != null && GodotObject.IsInstanceValid(texture.Cubemap))
			texture.Cubemap.Dispose();
	}
	#endregion

	#region PREVIEWS
	/* The previews whose meshes on screen use a material that changed or a mesh that was retired. */
	private void CollectPreviewsShowing(HashSet<Materials.Material> changedMaterials, HashSet<Mesh> staleMeshes, HashSet<ModelReferencePreview> affected)
	{
		foreach (KeyValuePair<MeshInstance3D, Materials.Material> entry in _modelReferenceMeshes.ToArray())
		{
			MeshInstance3D mesh = entry.Key;
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			{
				_modelReferenceMeshes.Remove(mesh);
				_meshBindings.Remove(mesh);
				continue;
			}

			if (changedMaterials.Contains(entry.Value) || (mesh.Mesh != null && staleMeshes.Contains(mesh.Mesh)))
				AddPreviewOf(mesh, affected);
		}

		foreach (KeyValuePair<MeshInstance3D, SceneFilterMesh> entry in _sceneFilterMeshes.ToArray())
		{
			MeshInstance3D mesh = entry.Key;
			if (mesh == null || !GodotObject.IsInstanceValid(mesh))
			{
				_sceneFilterMeshes.Remove(mesh);
				continue;
			}

			if (mesh.Mesh != null && staleMeshes.Contains(mesh.Mesh))
				AddPreviewOf(mesh, affected);
		}
	}

	private static void AddPreviewOf(MeshInstance3D mesh, HashSet<ModelReferencePreview> affected)
	{
		if (!(mesh.GetParent() is Node3D renderTarget))
			return;

		//A model reference renders onto its own entity node, or onto the node an alias points at; the
		//preview is a child of the entity node either way
		foreach (Node child in renderTarget.GetChildren())
		{
			if (child is ModelReferencePreview preview)
				affected.Add(preview);
		}
	}

	/* The previews whose resource names something that changed, whether or not anything was on screen
	   for it: a model that would not build before, or a material this side could not shade, spawned
	   nothing to find by the route above. */
	private void CollectPreviewsReferencing(
		HashSet<CS2.Component.LOD.Submesh> changedSubmeshes,
		HashSet<Materials.Material> changedMaterials,
		HashSet<ModelReferencePreview> affected)
	{
		Level level = _content.Level;
		HashSet<int> modelIndexes = new HashSet<int>();
		foreach (CS2.Component.LOD.Submesh submesh in changedSubmeshes)
		{
			if (_submeshWriteIndexByReference.TryGetValue(submesh, out int index))
				modelIndexes.Add(index);
		}
		HashSet<int> materialIndexes = new HashSet<int>();
		foreach (Materials.Material material in changedMaterials)
		{
			int index = level.Materials.GetWriteIndex(material);
			if (index >= 0)
				materialIndexes.Add(index);
		}

		EnsureFunctionEntityPreviewCache();
		foreach (FunctionEntityPreview candidate in _cachedFunctionEntityPreviews)
		{
			if (!(candidate is ModelReferencePreview preview) || affected.Contains(preview) || !GodotObject.IsInstanceValid(preview))
				continue;

			foreach (Tuple<int, int> renderable in preview.GetSourceRenderableIndexes())
			{
				if (modelIndexes.Contains(renderable.Item1) || materialIndexes.Contains(renderable.Item2))
				{
					affected.Add(preview);
					break;
				}
			}
		}
	}

	/* Refresh frees the render target's meshes, but those spawned by a populate have no exit hook to
	   take themselves out of the bookkeeping, so it is done here first. */
	private void ForgetRenderableChildren(Node3D renderTarget)
	{
		if (renderTarget == null || !GodotObject.IsInstanceValid(renderTarget))
			return;

		for (int i = renderTarget.GetChildCount() - 1; i >= 0; i--)
		{
			if (!(renderTarget.GetChild(i) is MeshInstance3D mesh))
				continue;

			_modelReferenceMeshes.Remove(mesh);
			_meshBindings.Remove(mesh);
			_sceneFilterMeshes.Remove(mesh);
			if (LevelViewerPick.TryGetPickOwner(mesh, out Node3D owner))
				LevelViewerPick.UnregisterPickableMesh(mesh, owner);
		}
	}
	#endregion

	/* Snapshots are numbered folders under one per level. Older ones are deleted once no restore path
	   points into them; the current one stays as it may be read from until the next arrives. */
	private void PruneResourceSnapshotFolders(Packet packet)
	{
		try
		{
			string current = FirstSnapshotFolder(packet);
			if (current == null)
				return;

			string parent = Path.GetDirectoryName(current);
			if (parent == null || !Directory.Exists(parent))
				return;

			HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(current) };
			if (_modelsRestorePath != null)
				keep.Add(Path.GetFullPath(Path.GetDirectoryName(_modelsRestorePath)));
			if (_levelTexturesRestorePath != null)
				keep.Add(Path.GetFullPath(Path.GetDirectoryName(_levelTexturesRestorePath)));

			foreach (string folder in Directory.GetDirectories(parent))
			{
				if (keep.Contains(Path.GetFullPath(folder)))
					continue;
				try
				{
					Directory.Delete(folder, true);
				}
				catch
				{
					//Still being written, or read by a restore; the next prune gets it
				}
			}
		}
		catch (Exception ex)
		{
			ViewerLog.Print("Snapshot folder prune skipped: " + ex.Message);
		}
	}

	private static string FirstSnapshotFolder(Packet packet)
	{
		foreach (string path in new[] { packet.resource_sync_textures, packet.resource_sync_shaders, packet.resource_sync_materials, packet.resource_sync_models })
		{
			if (!string.IsNullOrEmpty(path))
				return Path.GetDirectoryName(path);
		}
		return null;
	}
}
