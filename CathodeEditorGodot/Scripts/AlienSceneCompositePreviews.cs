using CATHODE;
using CATHODE.Scripting;
using CathodeLib;
using Godot;
using OpenCAGE.UnityConnection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// Composite previews: a square preview of each composite of the loaded level, for the browser in
/// OpenCAGE (COMPOSITE_PREVIEW_CAPTURE_REQUEST). The previews themselves are CompositePreviewCapture's;
/// this builds each composite as the thing on screen, previews it, and puts the view back at the end.
/// </summary>
/// <remarks>
/// Each composite is captured as it looks opened on its own in OpenCAGE - populated as the root, the
/// way PopulateCompositeInternal builds the composite on screen - and not as an instance inside the
/// level. An instance carries what the level applied to it: the material remaps an alias up the chain
/// resolves (ModelReferenceMaterialMapping walks the ancestors), parameter overrides, a proxy that
/// moved it. Opened on its own it has none of that, and that is the preview the browser wants.
/// </remarks>
public partial class AlienScene
{
	private CompositePreviewCapture _previewCapture;
	private bool _previewBatchRunning;
	/* PopulateCompositeInternal runs lean while this is set: no loading overlay, no word to OpenCAGE,
	   no camera framing, the material cache and the mapping index kept from the last populate, the
	   pak binaries kept for the whole batch, no distance culling, no memory line - a populate in tens
	   of milliseconds rather than the one-a-level it was written as. What makes the scene right
	   (registries, alias and proxy wiring, the render filters) still runs. */
	private bool _previewBatchPopulate;
	/* The overlays are stood down for the batch: the grey-out of a stepped-into composite, the zone
	   tint and the alias and proxy colours all replace or overlay a mesh's material and would be in
	   the previews. Their refreshes are no-ops while this is set, whoever asks for them. */
	private bool _previewOverlaysStoodDown;
	//The composite the batch last built as the one open, to tell its own setting of the active composite from anyone else's
	private uint _previewBatchActiveCompositeId;
	//The active composite OpenCAGE's editor moved to while the batch ran (0: it did not): what the batch puts back
	private uint _editorActiveCompositeDuringPreviews;

	/// <summary>
	/// The editor's packets set the active composite while a batch runs (a step out during a save's capture), and the next
	/// preview's populate writes over it: noted here, so the batch puts that back and not the one it started from - which
	/// the grey-out would otherwise judge the rebuilt scene by until the next packet.
	/// </summary>
	public void NoteEditorActiveComposite(uint activeCompositeId)
	{
		if (_previewBatchRunning)
			_editorActiveCompositeDuringPreviews = activeCompositeId;
	}

	//The scene the batch puts back when it is done, and a root switch OpenCAGE asked for while it ran (built instead)
	private Composite _previewBatchViewComposite;
	private ShortGuid _previewBatchRequestedRoot = ShortGuid.Invalid;

	/// <summary>
	/// A root switch (a composite opened from the browser) while a batch has the scene: a populate now would free what the
	/// batch is capturing, or - asked for the composite being captured - be skipped as the one on screen, and the switch
	/// was lost: the batch put its old scene back with the editor somewhere else. Noted, and built in place of the old
	/// scene once the batch is done. Back to the scene the batch restores is noted too: a batch that is abandoned puts
	/// nothing back, and the request is then made again.
	/// </summary>
	private bool TryDeferRootSwitchDuringPreviews(ShortGuid composite)
	{
		if (!_previewBatchRunning)
			return false;
		_previewBatchRequestedRoot = composite;
		if (_previewBatchViewComposite != null && _previewBatchViewComposite.shortGUID == composite)
			ViewerPopulateBridge.NotifySkipped();
		return true;
	}

	/* The scene on screen is still the batch's own (nothing has loaded or populated over it since its last preview), so whatever
	   ends the batch builds the scene again from the script: the restore, the root switch it deferred, or the load that takes
	   the scene from it. Edits that arrive meanwhile are already in the script, and are not built into a preview about to go. */
	private int _previewBatchGeneration = int.MinValue;
	private bool PreviewBatchHasScene => _previewBatchRunning && _loadStep == LoadPipelineStep.None && _contentGeneration == _previewBatchGeneration;

	/* The view the last batch took down: its composite, content origin and viewport hides. Good for as long as that batch's
	   last preview is the scene (BatchPreviewOnScreen), during the batch or after one that was abandoned or left it there. */
	private ShortGuid _previewBatchViewId = ShortGuid.Invalid;
	private Vector3 _previewBatchViewOrigin;
	private List<NodePath> _previewBatchViewHides;
	private bool BatchPreviewOnScreen => _contentGeneration == _previewBatchGeneration;

	/// <summary>
	/// A populate while a batch's lean preview is the scene (never framed, origin 0): the user's own composite built again gets
	/// the view the batch took down, as the batch's restore would put it back; any other composite is framed afresh. Taken for a
	/// rebuild of the scene on screen instead, the user's composite came back at the preview's origin under their camera.
	/// </summary>
	private bool TryKeepViewTakenByBatch(Composite comp, out Vector3 origin, out List<NodePath> hides)
	{
		origin = _previewBatchViewOrigin;
		hides = _previewBatchViewHides;
		return comp != null && _previewBatchViewId != ShortGuid.Invalid && _previewBatchViewId == comp.shortGUID
			&& _parentNode != null && GodotObject.IsInstanceValid(_parentNode);
	}

	private void DropDeferredRootSwitch()
	{
		if (_previewBatchRunning)
			_previewBatchRequestedRoot = ShortGuid.Invalid;
	}

	private const int PreviewProgressEvery = 50;
	/// <summary>
	/// Left beside where a composite's PNG would be when it draws nothing, zero bytes long, so a later
	/// run over another level (with preview_skip_existing) need not build it again to learn that: a
	/// composite has the same content in every level.
	/// </summary>
	public const string EmptyMarkerExtension = ".empty";
	//The selected light's radius sphere lives under its entity node, and would be in that entity's preview
	private static readonly StringName LightRadiusVisualNodeName = new StringName("LightRadiusVisual");

	public bool IsPreviewBatchRunning => _previewBatchRunning;

	private struct PreviewJob
	{
		public uint Id;
		public Composite Composite;
		public string File;
		public CompositePreviewStatus Status;
		public bool Pending;
		//Skipped on the strength of an .empty marker rather than a PNG: no file to report
		public bool SkippedAsEmpty;
	}

	/// <summary>The composites a request names: the list it carries, or every composite of the level when the list is empty.</summary>
	private List<uint> ResolvePreviewCompositeIds(List<uint> composites)
	{
		if (composites != null && composites.Count > 0)
			return new List<uint>(composites);

		List<uint> ids = new List<uint>();
		if (!_content.Loaded)
			return ids;

		foreach (Composite composite in _content.Level.Commands.Entries)
		{
			if (composite != null && composite.shortGUID != ShortGuid.Invalid)
				ids.Add(composite.shortGUID.AsUInt32);
		}

		return ids;
	}

	/// <summary>One Failed result per composite a request names, for a request that cannot be served at all.</summary>
	public List<CompositePreviewResult> BuildFailedPreviewResults(List<uint> composites)
	{
		List<uint> ids = ResolvePreviewCompositeIds(composites);
		List<CompositePreviewResult> results = new List<CompositePreviewResult>(ids.Count);
		for (int i = 0; i < ids.Count; i++)
			results.Add(MakePreviewResult(ids[i], "", CompositePreviewStatus.Failed));
		return results;
	}

	private static CompositePreviewResult MakePreviewResult(uint id, string file, CompositePreviewStatus status)
	{
		return new CompositePreviewResult { composite = id, file = file ?? "", status = (int)status };
	}

	private static string EmptyMarkerPath(string pngPath)
	{
		return Path.ChangeExtension(pngPath, EmptyMarkerExtension);
	}

	/// <summary>
	/// Preview each composite in <paramref name="composites"/> (every composite of the level when empty)
	/// as <c>&lt;id&gt;.png</c> in <paramref name="outputDir"/>, <paramref name="size"/> pixels square
	/// (0 = the default). With <paramref name="skipExisting"/>, a composite whose PNG or .empty marker is
	/// already there is left alone. With <paramref name="restoreView"/>, the composite that was on screen
	/// is built again at the end and the camera put back where it stood; without it, the last composite
	/// captured stays on screen. One result per composite, in the order asked for.
	/// </summary>
	/// <remarks>
	/// Refused outright - every composite Failed - while nothing is loaded or populated, while a load or
	/// populate is in flight, or while another batch is running; abandoned, with the rest Failed and
	/// nothing put back, if a load or populate arrives part way through, since whoever asked for that
	/// owns the scene now.
	///
	/// Every composite is built as the scene, in turn, by a lean populate (see the flags above) and
	/// captured whole. What the lot of them need converting is worked out first and restored in one
	/// pass, so the model and texture paks are read again at most once rather than once per composite
	/// (ReleaseCathodeBinarySourceData frees the binaries after every populate, and a populate that
	/// finds one missing re-reads a whole pak).
	/// </remarks>
	public async Task<List<CompositePreviewResult>> CapturePreviewsAsync(List<uint> composites, string outputDir, int size, bool skipExisting, bool restoreView)
	{
		List<uint> ids = ResolvePreviewCompositeIds(composites);

		string refusal = null;
		if (_previewBatchRunning)
			refusal = "a batch is already running";
		else if (!_content.Loaded)
			refusal = "no level is loaded";
		else if (_loadStep != LoadPipelineStep.None)
			refusal = "a load is in flight";
		else if (_parentNode == null || !GodotObject.IsInstanceValid(_parentNode) || _loadedComposite == null)
			refusal = "nothing is populated";
		else if (string.IsNullOrWhiteSpace(outputDir))
			refusal = "no output folder was given";
		//Relative to the viewer's own working folder, which is the exported build itself: never that
		else if (!Path.IsPathRooted(outputDir))
			refusal = "the output folder is not an absolute path";

		if (refusal != null)
		{
			ViewerLog.PrintErr("[Preview] Refusing to capture " + ids.Count + " composite(s): " + refusal + ".");
			return BuildFailedPreviewResults(ids);
		}

		if (size <= 0)
			size = CompositePreviewCapture.DefaultOutputSize;

		Stopwatch stopwatch = Stopwatch.StartNew();
		Commands commands = _content.Level.Commands;
		List<CompositePreviewResult> results = new List<CompositePreviewResult>(ids.Count);
		int[] counts = new int[5];
		int blankBefore = 0;
		int rescuedBefore = 0;
		double populateSeconds = 0;
		double captureSeconds = 0;
		int populated = 0;

		//What the batch takes off the screen, to put back afterwards
		Composite viewComposite = _loadedComposite;
		Vector3 viewOrigin = _contentOrigin;
		uint viewActiveComposite = PreviewVisibilitySettings.ActiveCompositeId;
		LevelViewerCamera camera = GetViewport()?.GetCamera3D() as LevelViewerCamera;
		Transform3D viewCamera = camera != null ? camera.GlobalTransform : Transform3D.Identity;
		List<NodePath> viewHides = LevelViewerEntityHide.CapturePaths(_parentNode);
		bool sceneRebuilt = false;
		bool abandoned = false;
		bool stoppedForSync = false;

		_previewBatchRunning = true;
		_editorActiveCompositeDuringPreviews = 0;
		_previewBatchViewComposite = viewComposite;
		_previewBatchViewId = viewComposite?.shortGUID ?? ShortGuid.Invalid;
		_previewBatchViewOrigin = viewOrigin;
		_previewBatchViewHides = viewHides;
		_previewBatchRequestedRoot = ShortGuid.Invalid;
		_previewBatchGeneration = int.MinValue; //the user's scene, until the first preview is built over it
		LevelViewerRenderIdleThrottle.SetLoadActive(true);
		try
		{
			Directory.CreateDirectory(outputDir);
			if (_previewCapture == null || !GodotObject.IsInstanceValid(_previewCapture))
				_previewCapture = CompositePreviewCapture.Ensure(this);
			blankBefore = _previewCapture.BlankCaptures;
			rescuedBefore = _previewCapture.RescuedCaptures;

			LevelViewerZoneHighlight.Clear();
			LevelViewerCompositeFocus.Clear();
			LevelViewerAliasHighlight.Clear();
			LevelViewerProxyHighlight.Clear();
			LevelViewerSelection.Suspend();
			//A rebuild queued before the batch would colour a preview; the restore at the end colours the level again
			_zoneRebuildPending = false;
			_previewOverlaysStoodDown = true;

			PreviewJob[] jobs = new PreviewJob[ids.Count];
			List<Composite> pending = new List<Composite>();
			for (int i = 0; i < ids.Count; i++)
			{
				uint id = ids[i];
				PreviewJob job = new PreviewJob { Id = id, File = Path.Combine(outputDir, id + ".png") };
				job.Composite = commands.GetComposite(new ShortGuid(id));
				if (job.Composite == null)
					job.Status = CompositePreviewStatus.Unknown;
				else if (skipExisting && File.Exists(job.File))
					job.Status = CompositePreviewStatus.SkippedExisting;
				else if (skipExisting && File.Exists(EmptyMarkerPath(job.File)))
				{
					job.Status = CompositePreviewStatus.SkippedExisting;
					job.SkippedAsEmpty = true;
				}
				else
				{
					job.Pending = true;
					pending.Add(job.Composite);
				}

				jobs[i] = job;
			}

			if (pending.Count > 0)
			{
				ShowLoading("Preparing to capture " + pending.Count + " composites...");
				Stopwatch prewarm = Stopwatch.StartNew();
				PrewarmForCompositeSet(pending);
				/* Built here once for the batch rather than once per populate (see the flag): the index
				   is over the whole script and does not depend on which composite is the root. */
				ModelReferenceMaterialMapping.PrepareForLevelPopulate(commands);
				LogPreview("Prewarmed for " + pending.Count + " composite(s) in " + prewarm.Elapsed.TotalSeconds.ToString("0.0") + " s.");
			}

			int generation = _contentGeneration;
			for (int i = 0; i < jobs.Length; i++)
			{
				PreviewJob job = jobs[i];
				CompositePreviewStatus status = job.Status;
				if (job.Pending)
				{
					if (!abandoned && (_contentGeneration != generation || !_content.Loaded || _loadStep != LoadPipelineStep.None))
					{
						abandoned = true;
						ViewerLog.PrintErr("[Preview] The level changed under the batch after " + i + " of " + jobs.Length
							+ " composite(s); the rest are marked failed and nothing is put back.");
					}
					/* A resource sync waits for the batch (see AdvanceResourceSync), and every packet from OpenCAGE waits
					   behind the sync: the batch stops here rather than hold the editor off for the rest of it. The scene
					   is still the batch's own, so it is put back as usual. */
					if (!abandoned && !stoppedForSync && _pendingResourceSyncs.Count > 0)
					{
						stoppedForSync = true;
						ViewerLog.PrintErr("[Preview] A resource sync is waiting: stopping the batch after " + i + " of " + jobs.Length
							+ " composite(s); the rest are marked failed.");
					}

					if (abandoned || stoppedForSync)
					{
						status = CompositePreviewStatus.Failed;
					}
					else
					{
						sceneRebuilt = true;
						Stopwatch populate = Stopwatch.StartNew();
						bool built = TryPopulateForPreview(job.Composite);
						populateSeconds += populate.Elapsed.TotalSeconds;
						populated++;
						//The populate is this batch's own; a generation change from anywhere else is what the check above is for
						generation = _previewBatchGeneration = _contentGeneration;

						if (!built)
						{
							status = CompositePreviewStatus.Failed;
						}
						else
						{
							Stopwatch capture = Stopwatch.StartNew();
							status = await _previewCapture.CaptureSubtreeAsync(_parentNode, job.File, size, IsExcludedFromPreview, GetPopulateDisplayLabel(job.Composite));
							captureSeconds += capture.Elapsed.TotalSeconds;
							/* A load or populate that arrived while the preview was being taken freed what was
							   tagged for it: whatever came back is of nothing, not of this composite, and a
							   marker for it would say the composite draws nothing. */
							if (_contentGeneration != generation)
							{
								status = CompositePreviewStatus.Failed;
								//...and that scene is on screen now, not the batch's: as when the next job finds it (above), nothing is put back
								if (!abandoned)
								{
									abandoned = true;
									ViewerLog.PrintErr("[Preview] The level changed under the batch while composite " + (i + 1) + " of " + jobs.Length
										+ " was being captured; the rest are marked failed and nothing is put back.");
								}
							}
							else
								RecordEmptyMarker(job.File, status);
						}
					}
				}

				counts[(int)status]++;
				bool hasFile = status == CompositePreviewStatus.Captured
					|| (status == CompositePreviewStatus.SkippedExisting && !job.SkippedAsEmpty);
				results.Add(MakePreviewResult(job.Id, hasFile ? job.File : "", status));

				/* One line per composite, so the log says which composite each file is (the file is named
				   by id alone). Tagged apart from the batch's own progress lines, which a driver picks out
				   of the log by "[Preview]". */
				if (job.Pending)
				{
					ViewerLog.Print("[Preview detail] " + job.Id + " " + status.ToString().ToLowerInvariant() + " "
						+ GetPopulateDisplayLabel(job.Composite));
				}

				if ((i + 1) % PreviewProgressEvery == 0 && i + 1 < jobs.Length)
				{
					ShowLoading("Capturing composites: " + (i + 1) + " of " + jobs.Length + "...");
					LogPreview("Captured " + (i + 1) + " of " + jobs.Length + " composites (" + DescribePreviewCounts(counts) + ") in "
						+ stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " s (populate " + populateSeconds.ToString("0.0")
						+ " s, capture " + captureSeconds.ToString("0.0") + " s).");
				}
			}
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] The batch failed: " + e);
			for (int i = results.Count; i < ids.Count; i++)
			{
				counts[(int)CompositePreviewStatus.Failed]++;
				results.Add(MakePreviewResult(ids[i], "", CompositePreviewStatus.Failed));
			}
		}
		finally
		{
			_previewBatchPopulate = false;
			/* Put back only while it is still the batch's own: a load or populate that arrived part way
			   through set it for its scene, and that scene is what is on screen now. */
			if (populated > 0 && PreviewVisibilitySettings.ActiveCompositeId == _previewBatchActiveCompositeId)
				PreviewVisibilitySettings.ActiveCompositeId = _editorActiveCompositeDuringPreviews != 0 ? _editorActiveCompositeDuringPreviews : viewActiveComposite;
			_editorActiveCompositeDuringPreviews = 0;
			_previewOverlaysStoodDown = false;
		}

		double restoreSeconds = 0;
		ShortGuid requestedRoot = _previewBatchRequestedRoot;
		//Deleted while the batch ran (an undo of its creation; the editor closed its view): nothing to switch to, the old scene goes back
		if (requestedRoot != ShortGuid.Invalid && _content.Level?.Commands?.GetComposite(requestedRoot) == null)
			requestedRoot = ShortGuid.Invalid;
		bool rootSwitched = requestedRoot != ShortGuid.Invalid && (viewComposite == null || viewComposite.shortGUID != requestedRoot);
		try
		{
			if (sceneRebuilt && !abandoned)
			{
				//The editor went to another root while the batch ran: that is built next, not the old scene put back
				if (rootSwitched)
					FinishLastPreviewPopulate();
				else if (restoreView && viewComposite != null && _content.Loaded && _loadStep == LoadPipelineStep.None)
				{
					Stopwatch restore = Stopwatch.StartNew();
					//The composite as the script holds it now: deleted and put back (an undo) while the batch ran, it is another object
					Composite restoreTo = _content.Level?.Commands?.GetComposite(viewComposite.shortGUID) ?? viewComposite;
					RestoreViewAfterPreviews(restoreTo, viewOrigin, camera, viewCamera, viewHides);
					restoreSeconds = restore.Elapsed.TotalSeconds;
				}
				else
				{
					FinishLastPreviewPopulate();
				}
			}
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Putting the scene back after the batch failed: " + e);
		}
		finally
		{
			_previewBatchRunning = false;
			_previewBatchViewComposite = null;
			_previewBatchRequestedRoot = ShortGuid.Invalid;
			RestoreOverlaysAfterPreviews();
			//A load or populate that arrived part way through showed the overlay for itself and hides it when it is done;
			//a load that failed shows its message there, which stays until the next load
			if (_loadStep == LoadPipelineStep.None && !_levelLoadFailedShown)
				HideLoading();
			LevelViewerRenderIdleThrottle.SetLoadActive(false);
		}

		/* The batch's last preview is still the scene, with a composite populate on its way (it abandoned the batch, or kept the
		   restore from running). That preview is not the user's scene: left as the loaded composite, a forced rebuild of it would
		   keep the preview's origin under the user's camera, and a switch to it would be merged away as already on screen. */
		if (populated > 0 && _contentGeneration == _previewBatchGeneration && _loadStep == LoadPipelineStep.WaitUiBeforeCompositePopulate)
			_loadedComposite = null;

		/* The root the editor switched to while the batch ran, built as a root switch builds it (framed, reported to OpenCAGE).
		   The scene on screen may be that composite already, as the batch's last preview: forgotten as the loaded one, or the
		   populate would take itself for a rebuild of it and keep the preview's origin and camera. */
		if (rootSwitched && !abandoned && _content.Loaded && _loadStep == LoadPipelineStep.None)
		{
			if (_loadedComposite != null && _loadedComposite.shortGUID == requestedRoot)
				_loadedComposite = null;
			QueuePopulateComposite(requestedRoot, true);
		}
		/* A load or populate took the scene from the batch (abandoned, nothing put back), or is on its way now: the switch
		   asked for after it is made again as an ordinary switch - merged into the pending one, or from what it built. One
		   that came before it was dropped when it arrived (see QueuePopulateComposite, QueueLoadLevel). */
		else if (requestedRoot != ShortGuid.Invalid && (abandoned || _loadStep != LoadPipelineStep.None))
		{
			QueuePopulateComposite(requestedRoot, false);
		}

		int blank = 0;
		int rescued = 0;
		if (_previewCapture != null && GodotObject.IsInstanceValid(_previewCapture))
		{
			blank = _previewCapture.BlankCaptures - blankBefore;
			rescued = _previewCapture.RescuedCaptures - rescuedBefore;
		}

		LogPreview("Captured " + ids.Count + " composite(s) (" + DescribePreviewCounts(counts) + ") in "
			+ stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " s: "
			+ populated + " populate(s) took " + populateSeconds.ToString("0.0") + " s ("
			+ (populated > 0 ? (populateSeconds * 1000.0 / populated).ToString("0") : "0") + " ms each), the previews "
			+ captureSeconds.ToString("0.0") + " s"
			+ (restoreSeconds > 0 ? ", putting the view back " + restoreSeconds.ToString("0.0") + " s" : "")
			+ (rescued > 0 ? "; " + rescued + " captured from another side" : "")
			+ (blank > 0 ? "; " + blank + " blank from every side, reported empty" : "") + ".");
		return results;
	}

	/// <summary>
	/// Build the composite as the scene, the way it is built when opened on its own, less what a
	/// one-a-level populate does around it (the flag). False when the populate threw; the scene is
	/// then half built, and the next populate - the next composite's, or the restore - clears it.
	/// </summary>
	private bool TryPopulateForPreview(Composite composite)
	{
		//The composite is the one open, as it would be in OpenCAGE: what the nested-entity hiding keys off
		_previewBatchActiveCompositeId = composite.shortGUID.AsUInt32;
		PreviewVisibilitySettings.ActiveCompositeId = _previewBatchActiveCompositeId;

		_previewBatchPopulate = true;
		_isBulkPopulating = true;
		_deferMeshTreeActivation = true;
		FunctionEntityPreview.DeferVisualRefresh = true;
		try
		{
			PopulateCompositeInternal(composite);
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Building " + GetPopulateDisplayLabel(composite) + " for its preview failed: " + e);
			return false;
		}
		finally
		{
			FunctionEntityPreview.DeferVisualRefresh = false;
			_deferMeshTreeActivation = false;
			_isBulkPopulating = false;
			_previewBatchPopulate = false;
		}

		/* What CompletePopulate does to make the scene right - the filters bring the meshes spawned
		   hidden above into view and give every preview its visuals. The overlays it would refresh are
		   stood down, and the camera is left alone. */
		RefreshRenderFilters(null);
		RefreshSceneGeometryFilters();
		return true;
	}

	/// <summary>
	/// The composite the user had on screen, built again as a populate of it would build it, with
	/// the content origin and the camera exactly where they were rather than framed afresh, and
	/// what was hidden in the viewport hidden again.
	/// </summary>
	private void RestoreViewAfterPreviews(Composite composite, Vector3 origin, LevelViewerCamera camera, Transform3D cameraTransform, List<NodePath> hides)
	{
		string label = GetPopulateDisplayLabel(composite);
		ShowLoading("Loading " + label + "...");

		bool cameraValid = camera != null && GodotObject.IsInstanceValid(camera);
		if (cameraValid)
			camera.SkipNextCompositeFraming = true;

		/* As ExecutePopulateComposite runs one, less the word to OpenCAGE: it did not ask for this
		   populate and its populate bookkeeping must not see it (whatever it re-sends after the reply,
		   the selection and the focus, it re-sends anyway). CompletePopulate's NotifyFinished is a
		   no-op with no populate token active. */
		_isBulkPopulating = true;
		_deferMeshTreeActivation = true;
		FunctionEntityPreview.DeferVisualRefresh = true;
		try
		{
			PopulateCompositeInternal(composite);
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Putting " + label + " back on screen failed: " + e);
		}
		CompletePopulate();
		if (cameraValid)
			camera.SkipNextCompositeFraming = false;

		/* Where the framing had moved the content to (RecenterContentOrigin) and where the camera stood.
		   The origin is put back as it was rather than resolved again: a resolve only moves the origin
		   when the focus point is half a metre or more from the last one, so what it would give now is
		   not always what the view had. */
		if (_parentNode != null && GodotObject.IsInstanceValid(_parentNode))
		{
			_contentOrigin = origin;
			_parentNode.Position = -origin;
			LevelViewerPick.InvalidateAllPickBounds();
		}

		//Only if something moved it: the same transform set again comes back with its basis a rounding off
		if (cameraValid && camera.GlobalTransform != cameraTransform)
		{
			camera.GlobalTransform = cameraTransform;
			camera.SyncAnglesFromTransform();
		}

		RestoreHidesAfterRebuild(hides);
	}

	/* The last composite captured stays on screen (the one-off run over every level, whose viewer is
	   closed straight after): finish its populate the way a one-a-level populate finishes, so the
	   scene is in the state everything after a populate expects. The material cache is left alone -
	   the meshes on screen are drawn with it. */
	private void FinishLastPreviewPopulate()
	{
		ReleaseCathodeBinarySourceData();
		CollectReleasedSourceData();
		LogMemoryBreakdown("after the preview batch");
		QueueLargeSceneRenderPolicyApply();
	}

	//An .empty marker for a composite that drew nothing; a composite captured after all loses a stale one
	private static void RecordEmptyMarker(string pngPath, CompositePreviewStatus status)
	{
		try
		{
			string marker = EmptyMarkerPath(pngPath);
			if (status == CompositePreviewStatus.Empty)
				File.WriteAllBytes(marker, Array.Empty<byte>());
			else if (status == CompositePreviewStatus.Captured && File.Exists(marker))
				File.Delete(marker);
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Could not write the empty marker beside " + pngPath + ": " + e.Message);
		}
	}

	//Overlays that live inside the level's tree and are never part of a composite's preview
	private bool IsExcludedFromPreview(Node node)
	{
		return node == _collisionOverlay || node == _stateInfoOverlay || node.Name == LightRadiusVisualNodeName;
	}

	/// <summary>
	/// One restore / convert / cache pass for everything these composites will need when built, so the
	/// batch never re-reads a pak per composite. Only what the caches do not hold goes into the pass.
	/// </summary>
	private void PrewarmForCompositeSet(List<Composite> composites)
	{
		LevelViewerPopulatePrewarm.Plan union = new LevelViewerPopulatePrewarm.Plan();
		for (int i = 0; i < composites.Count; i++)
		{
			LevelViewerPopulatePrewarm.Plan plan = BuildCompositePrewarmPlan(composites[i]);
			if (plan == null)
				continue;

			foreach (int writeIndex in plan.MeshWriteIndices)
			{
				if (!IsPlanMeshCached(writeIndex))
					union.MeshWriteIndices.Add(writeIndex);
			}

			foreach (Textures.TEX4 texture in plan.Textures)
			{
				if (texture == null || IsPlanTextureCached(texture, plan))
					continue;

				union.Textures.Add(texture);
				if (plan.TextureLocations.TryGetValue(texture, out TexturePtr.Source location))
					union.TextureLocations[texture] = location;
			}

			foreach (Materials.Material material in plan.Materials)
				union.Materials.Add(material);
		}

		if (union.MeshWriteIndices.Count == 0 && union.Textures.Count == 0)
			return;

		PrewarmPlanIfMissing(union, composites.Count + " composite(s) to be captured");
	}

	private void RestoreOverlaysAfterPreviews()
	{
		try
		{
			RefreshCompositeFocus();
			RefreshZoneOverlay();
			RefreshEntityHighlights(forceRebuild: true);
			LevelViewerSelection.ReapplyIfSelectionActive();
		}
		catch (Exception e)
		{
			ViewerLog.PrintErr("[Preview] Putting the overlays back failed: " + e);
		}
	}

	private static void LogPreview(string line)
	{
		ViewerLog.Print("[Preview] " + line);
		//ViewerLog is off in a viewer OpenCAGE did not start; the batch driver reads stdout
		if (!ViewerLog.Enabled)
			GD.Print("[Preview] " + line);
	}

	private static string DescribePreviewCounts(int[] counts)
	{
		return counts[(int)CompositePreviewStatus.Captured] + " captured, "
			+ counts[(int)CompositePreviewStatus.Empty] + " empty, "
			+ counts[(int)CompositePreviewStatus.Failed] + " failed, "
			+ counts[(int)CompositePreviewStatus.SkippedExisting] + " skipped, "
			+ counts[(int)CompositePreviewStatus.Unknown] + " unknown";
	}
}
