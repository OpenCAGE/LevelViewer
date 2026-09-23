using CATHODE.Scripting;
using System;
using System.Collections.Generic;

namespace OpenCAGE.UnityConnection
{
    public enum PacketEvent
    {
        LEVEL_LOADED,

        COMPOSITE_SELECTED,
        COMPOSITE_RELOADED,
        COMPOSITE_DELETED,
        COMPOSITE_ADDED,

        ENTITY_SELECTED,
        ENTITY_MOVED,
        ENTITY_DELETED,
        ENTITY_ADDED,
        ENTITY_RESOURCE_MODIFIED,
        ENTITY_PARAMETER_MODIFIED,

        RENDER_FILTERS_CHANGED,
        MATERIAL_MAPPING_MODIFIED,
        SETTINGS_CHANGED,
        VIEWER_LOG,
        VIEWER_POPULATE_STARTED,
        VIEWER_POPULATE_FINISHED,
        VIEWPORT_MODE_CHANGED,
        GENERIC_DATA_SYNC,

        // Level Viewer -> OpenCAGE: create an entity of create_function_type at `position` (creation mode).
        ENTITY_CREATE_REQUEST,

        // Level Viewer -> OpenCAGE: delete what is selected there (Delete pressed in the viewport).
        // The level data lives on the OpenCAGE side, so it does the deleting and answers with
        // ENTITY_DELETED as it would for a deletion made in the editor.
        ENTITY_DELETE_REQUEST,

        // Level Viewer -> OpenCAGE: copy the given entity to the shared entity clipboard (Ctrl+C).
        ENTITY_CLIPBOARD_COPY,
        // Level Viewer -> OpenCAGE: paste the shared entity clipboard into the given composite (Ctrl+V).
        ENTITY_CLIPBOARD_PASTE,

        // OpenCAGE -> Level Viewer: something was dropped on the viewport at drop_viewport_x/y. Only
        // this side has the geometry, so it raycasts the position and answers with ENTITY_CREATE_REQUEST.
        VIEWPORT_DROP_REQUEST,

        // OpenCAGE -> Level Viewer: models, materials, textures or shaders were edited. The tables that
        // changed were written to a scratch folder (resource_sync_* paths, null = unchanged) for this side
        // to load and patch into the level it already has, so nothing has to be reloaded.
        LEVEL_RESOURCES_MODIFIED,

        // Level Viewer -> OpenCAGE: the alias its deep-select created was deselected before anything
        // was done with it on that side. OpenCAGE deletes it (answering with ENTITY_DELETED, as for any
        // deletion) unless it has since been given a reason to stay there: an edit, or a flowgraph node.
        ENTITY_ALIAS_RELEASED,

        // OpenCAGE -> Level Viewer: the level was saved, so the generated navigation data on disk
        // (STATE_x/NAV_MESH, COVER, SPOTTING_POSITIONS, ...) has been rewritten - an instanced save
        // regenerates all of it. This side reads those files itself, so it re-reads them rather than
        // being sent them, and redraws whichever state overlay is showing.
        LEVEL_STATE_RESOURCES_MODIFIED,

        // OpenCAGE -> Level Viewer: the zone table, recalculated from the live level. Zone membership
        // is decided entirely by links (a Zone's 'composites' pin -> TriggerSequences -> their entries),
        // and links are not part of the entity sync, so this side works it out and sends the answer
        // rather than the viewer trying to keep up with it.
        // Appended, not inserted: these travel as numbers, so an existing event's value must not move.
        ZONES_CHANGED,

        // Level Viewer -> OpenCAGE: Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) pressed with the viewport focused.
        // The undo stack lives on the OpenCAGE side and the viewport is a separate process, so its
        // window never sees those keys through the WinForms chords - it has to ask.
        UNDO_REQUEST,
        REDO_REQUEST,

        // OpenCAGE -> Level Viewer: the CAGEAnimation editor is in Animation Mode, and these are the
        // parameter values its tracks hold at the time on the playhead. The animation is being edited
        // in OpenCAGE, so only that side can evaluate it - this carries the answers, not the curves.
        // Applied as a transient override on top of the scene: the level data is never written, and
        // clearing it (animation_preview_active = false) puts every node it touched back.
        // Appended, not inserted: these travel as numbers, so an existing event's value must not move.
        ANIMATION_PREVIEW,
        
        SAVE_REQUEST,
        SAVE_AND_BUILD_REQUEST,
        // Level Viewer -> OpenCAGE: Shift was held when a gizmo drag began, so the drag is meant for a
        // COPY of what is selected (3ds Max's shift-clone). The level data lives on the OpenCAGE side,
        // so it makes the copies - in place, undoable as one step - and selects them, which reaches
        // the viewer as the usual ENTITY_ADDED + ENTITY_SELECTED and is what hands the drag over.
        ENTITY_DUPLICATE_REQUEST,

        // Level Viewer -> OpenCAGE: files were dropped on the viewport window. Godot owns that window's
        // drop target, so a package (.ocp / .omp) dropped there is handed to OpenCAGE to open, the
        // same as a drop anywhere else on the editor. Carries `dropped_files`.
        FILES_DROPPED,

        // OpenCAGE -> Level Viewer: every entity of one composite (`composite`), in `composite_entities`.
        // Sent for a composite that arrives already populated (an import), instead of one ENTITY_ADDED
        // per entity - a mission script is a thousand of those.
        COMPOSITE_CONTENTS,

        // OpenCAGE -> Level Viewer: a run of composite/entity changes is starting (an import) that ends
        // with the scene being rebuilt, so until SCENE_BATCH_END the viewer keeps its script copy up to
        // date and leaves the scene alone - tearing the old nodes down one composite at a time is work
        // the rebuild throws away, and it keeps the viewer's thread busy for seconds.
        SCENE_BATCH_BEGIN,
        SCENE_BATCH_END,

        // OpenCAGE -> Level Viewer: the shared entity clipboard was set or emptied (a copy made anywhere in
        // the editor, or a level load), with `entity_clipboard_has_content` saying which. The viewport's
        // context menu greys Paste out when there is nothing to paste. Built like OpenCAGE's other packets,
        // with the selection on it, so a viewer from before this takes it as a re-sync of what it has.
        ENTITY_CLIPBOARD_CHANGED,

        // Level Viewer -> OpenCAGE: a right click in the viewport (pressed and let go without a drag) wants
        // the context menu. OpenCAGE draws it - a WinForms menu, themed with the rest of the editor - so this
        // carries only what the viewer alone knows: where the click landed (context_menu_viewport_x/y, a 0-1
        // fraction like a drop's) and which entries have anything to act on (the context_menu_* flags). What
        // the click landed on has been selected first, through the ordinary ENTITY_SELECTED before this.
        VIEWPORT_CONTEXT_MENU,

        // Level Viewer -> OpenCAGE: input arrived in the viewport while OpenCAGE's context menu was up - a
        // click of any button, or a key. A menu over another process's window never sees that input, so
        // the viewer swallows it (the click selects nothing, looks nowhere) and asks for the menu to close.
        VIEWPORT_CONTEXT_MENU_DISMISS,

        // OpenCAGE -> Level Viewer: run one of the viewport's own actions, named by `viewport_action`: a
        // context menu entry whose implementation lives in the viewer (what its shortcut does), or the menu
        // opening and closing, so the viewer knows to hand its next input to the menu. Built like OpenCAGE's
        // other packets, with the selection on it, so a viewer from before this takes it as a re-sync.
        VIEWPORT_ACTION,
    }

    /// <summary>
    /// What a VIEWPORT_ACTION packet asks the viewer to do. Travels as a number in `viewport_action`, so
    /// values are only ever appended.
    /// </summary>
    public enum ViewportAction
    {
        None = 0,

        //Context menu entries the viewer implements: each is exactly what its shortcut does
        FocusOnSelection,      //Z
        SnapToFloor,           //Shift+End
        Hide,                  //H
        UnhideAll,             //Shift+H
        StepIntoComposite,     //Ctrl+middle click, where the right click was
        SelectParentComposite, //'-' out of a stepped-into composite
        DeselectAll,           //Escape

        //OpenCAGE's context menu opened for the viewer's last VIEWPORT_CONTEXT_MENU, or has closed again
        ContextMenuOpened,
        ContextMenuClosed,
    }

    /// <summary>One entity of a composite, as ENTITY_ADDED would carry it, for COMPOSITE_CONTENTS.</summary>
    public class EntityRecord
    {
        public uint entity;
        public EntityVariant entity_variant;
        public uint entity_function; //For function entities
        public List<uint> entity_pointed; //For alias/proxy entities
        public List<SyncedParameter> parameters = new List<SyncedParameter>();
    }

    /// <summary>
    /// One entity instance a CAGEAnimation drives, and the value its tracks hold for one parameter at
    /// the previewed time. Addressed by instance path rather than by id because the same entity in two
    /// placements of a composite is two different things to animate, and only one of them is meant.
    /// </summary>
    public class SyncedAnimationTarget
    {
        public List<uint> path = new List<uint>(); //instance path from the composite the viewer populated

        public uint parameter;  //the parameter's ShortGuid
        public uint data_type;  //DataType of the value below

        //TRANSFORM: position and rotation, in the same Cathode space a position parameter is stored in
        public float[] vector3_a;
        public float[] vector3_b;
    }

    public class Packet
    {
        public Packet(PacketEvent packet_event = PacketEvent.GENERIC_DATA_SYNC)
        {
            this.packet_event = packet_event;
        }

        //Packet metadata
        public PacketEvent packet_event;
        public int version = 21;

        //Setup metadata
        public string level_name = "";
        public string system_folder = "";

        // Matched on VIEWER_POPULATE_* packets to ignore reordered websocket messages.
        public uint populate_token = 0;

        //Selection metadata
        public List<uint> path_entities = new List<uint>();
        public List<uint> path_composites = new List<uint>();
        public uint entity;
        public uint composite;
        //With COMPOSITE_ADDED, COMPOSITE_SELECTED and COMPOSITE_RELOADED: what the composite is called, for a
        //viewer that has only ever been told its id (a composite added this session)
        public string composite_name = "";
        //With COMPOSITE_CONTENTS: every entity of `composite`
        public List<EntityRecord> composite_entities = new List<EntityRecord>();

        // Everything selected in `composite`, `entity` first, when more than one thing is selected.
        // Empty means the selection is just `entity` - the usual case - so a one-entity selection is
        // untouched by any of this. `entity` stays the one the inspector, the camera and the gizmo's
        // own orientation follow; the rest come along for highlighting and for being transformed with it.
        public List<uint> selection_entities = new List<uint>();

        //Transform
        public bool has_transform = false;
        public System.Numerics.Vector3 position = new System.Numerics.Vector3();
        public System.Numerics.Vector3 rotation = new System.Numerics.Vector3();

        //Renderable resource
        public List<Tuple<int, int>> renderable = new List<Tuple<int, int>>(); //Model Index, Material Index

        //Generic parameter sync
        public List<SyncedParameter> parameters = new List<SyncedParameter>();

        //Modified entity info
        public EntityVariant entity_variant;
        public uint entity_function; //For function entities
        //Composite to instance (its ShortGuid) instead of creating a function entity, 0 = function entity
        public uint create_composite_instance = 0;
        public List<uint> entity_pointed; //For alias/proxy entities

        //Viewport drag & drop (VIEWPORT_DROP_REQUEST): where the drop landed, as a 0-1 fraction of the
        //viewport's size. A fraction rather than pixels because the host panel and the viewer window
        //need not agree on DPI.
        public float drop_viewport_x = 0f;
        public float drop_viewport_y = 0f;

        //Resource sync (LEVEL_RESOURCES_MODIFIED): scratch copies of the tables that changed, null where
        //one didn't. Textures and models are matched by name on the receiving side; the lists say which
        //existing ones had their binary replaced, since only the sender can tell that from the metadata.
        public string resource_sync_textures = null;
        public string resource_sync_shaders = null;
        public string resource_sync_materials = null;
        public string resource_sync_models = null;
        public List<string> resource_changed_textures = new List<string>();
        public List<string> resource_changed_models = new List<string>();

        //Files dropped on the viewport window (FILES_DROPPED): absolute paths, as the OS gave them
        public List<string> dropped_files = new List<string>();

        //Track if things have changed
        public bool dirty = false;

        //Settings
        public bool focus_object = false;
        public bool fix_camera_to_selected = false;
        public bool show_camera_position = true;
        public bool hide_nested_script_entities = false;
        public bool model_reference_wireframe = false;
        public bool highlight_aliases = true;
        public bool highlight_proxies = true;
        public float transform_grid_snap = 0f;
        public float rotation_snap_degrees = 0f;
        // Vertex snapping kept on from the Transform Snap menu ('Vertex'); holding V during a drag is
        // the momentary form and never travels - the viewer reads the key itself.
        public bool transform_vertex_snap = false;
        public int deep_select_mode = 0;
        public int gizmo_mode = 0;
        // Entity creation mode: FunctionType (uint) to place on viewport click, 0 = creation mode off.
        public uint create_function_type = 0;
        public Dictionary<uint, bool> box_render_filters = new Dictionary<uint, bool>();
        // Scene geometry filters that arent tied to a FunctionType, keyed by SceneFilterKind name.
        public Dictionary<string, bool> scene_render_filters = new Dictionary<string, bool>();
        // State info overlays: index into Level.StateResources, or -1 for off.
        public int show_navmesh_state = -1;
        public int show_cover_state = -1;
        // Tint the level's geometry by the zone each part of it belongs to ('Show Zones' on the toolbar).
        public bool show_zones = false;

        // How the viewer marks the selected entity (LevelViewerHighlightMode).
        public int selection_highlight_mode = 0;

        // Level Viewer log line forwarded to OpenCAGE (VIEWER_LOG).
        public string log_message = "";
        public bool log_is_error = false;

        public SyncedMaterialMappingSet material_mapping = null;

        // The level's zones and what each one covers (ZONES_CHANGED). Null on every other packet - it
        // is only ever sent when it has been recalculated, never as part of the generic metadata.
        public List<SyncedZone> zones = null;

        // Instance paths, from the level root, of entities that ride along with the selection for
        // highlighting only: a TriggerSequence's members. Paths rather than ids because a member can
        // live any number of composites down from the one the sequence is in, so an id on its own
        // would not say which instance of it is meant.
        public List<List<uint>> selection_entity_paths = null;

        // Animation Mode in the CAGEAnimation editor (ANIMATION_PREVIEW). `animation_preview_active`
        // false clears everything the preview is holding; the list is what it holds at
        // `animation_preview_time`, and is the WHOLE set each time rather than a delta, so a target
        // that has stopped being animated is restored by simply no longer being in it.
        public bool animation_preview_active = false;
        public float animation_preview_time = 0f;
        public List<SyncedAnimationTarget> animation_preview = null;

        // Level Viewer -> OpenCAGE: every packet one viewport gesture sends carries the same non-zero id.
        // A gizmo drag of several entities is an ENTITY_PARAMETER_MODIFIED per entity, a shift-clone is an
        // ENTITY_DUPLICATE_REQUEST and then the drag that carries the copies, and Shift+End is a packet per
        // entity it lands - OpenCAGE records all of one gesture as a single undo step. 0 = not part of one,
        // which is also what a viewer from before this sends, so the two still talk.
        public uint gesture = 0;

        // Level Viewer -> OpenCAGE, on ENTITY_ADDED: more entities of the selection this packet carries are
        // still to be added after it (a box in advanced deep select makes an alias for every nested entity it
        // takes), so OpenCAGE adds this one without selecting anything yet. The last packet of the run has it
        // false and selects the lot in one go. False is also what a viewer from before this sends.
        public bool selection_follows = false;

        // OpenCAGE -> Level Viewer: whether the shared entity clipboard has anything on it. Every packet
        // OpenCAGE builds with its metadata says, and ENTITY_CLIPBOARD_CHANGED says when it changes. Null on
        // a packet that doesn't say - anything the viewer sends, anything from an OpenCAGE from before this -
        // so the viewer only takes it from one that does, and until one has Paste stays available.
        public bool? entity_clipboard_has_content = null;

        // OpenCAGE -> Level Viewer, with the rest of the settings: draw the level's galaxy (its starfield) as the
        // sky, as the game does, rather than the plain sky (Options > Viewport > Render Galaxy). True is also what
        // an OpenCAGE from before this means by never saying, so a viewer from after it shows the stars.
        public bool render_galaxy = true;

        // LEVEL_RESOURCES_MODIFIED: a scratch copy of GALAXY.ITEMS_BIN when the galaxy was regenerated (the Galaxy
        // Editor), null when it wasn't. Loaded and shown like the other resource_sync_* tables.
        public string resource_sync_galaxy = null;

        // OpenCAGE -> Level Viewer, on LEVEL_LOADED: the level was read from disk again although it is the one the
        // viewer already holds (loaded again by hand, often to throw away unsaved changes), so the viewer reads it
        // again too rather than keeping what it has. False is what the LEVEL_LOADED a save sends means, and what an
        // OpenCAGE from before this sends - both keep the level that is loaded.
        public bool level_reload = false;

        // Level Viewer -> OpenCAGE, on VIEWPORT_CONTEXT_MENU: where the right click landed, as a 0-1 fraction
        // of the viewport (like drop_viewport_x/y - the two sides need not share DPI). OpenCAGE puts it through
        // the panel the viewer is embedded in, whose coordinates are the only ones its menu can be placed in.
        public float context_menu_viewport_x = 0f;
        public float context_menu_viewport_y = 0f;

        // With it: which entries have anything to act on, as the viewer sees it - the rest are greyed out.
        // `has_selection` is Copy, Duplicate, Delete and Deselect All; `can_paste` says there is a composite
        // to paste into (OpenCAGE knows for itself whether its clipboard holds anything).
        public bool context_menu_has_selection = false;
        public bool context_menu_can_paste = false;
        public bool context_menu_can_focus = false;
        public bool context_menu_can_snap_to_floor = false;
        public bool context_menu_can_hide = false;
        public bool context_menu_can_unhide_all = false;
        public bool context_menu_can_step_into = false;
        public bool context_menu_can_select_parent = false;

        // OpenCAGE -> Level Viewer, on VIEWPORT_ACTION: which action (a ViewportAction). 0 = none, which is
        // also what a packet from before this carries.
        public int viewport_action = 0;

        // OpenCAGE -> Level Viewer, on VIEWPORT_DROP_REQUEST: a function type (a FunctionType, as a number) was
        // dropped on the viewport out of the entity palette, rather than a composite out of the browser. Placed
        // exactly as a composite drop is, and answered with the same ENTITY_CREATE_REQUEST, carrying it as
        // `entity_function` - and echoed here, since a palette drop admits any function that has a position
        // where a creation-mode click admits only the types the viewer previews, and OpenCAGE has to know which
        // it is answering. 0 = not a palette drop, which is also what a packet from before this carries; a viewer
        // from before this finds no composite to instance and lets the drop do nothing.
        public uint drop_function_type = 0;

        // Every entity the packet is about when it is about several, `entity` among them. Level Viewer ->
        // OpenCAGE, on ENTITY_ALIAS_RELEASED: the deep-select aliases let go of at once - a box's worth, abandoned
        // together by the click that replaced them - which OpenCAGE judges and deletes as one set, where a packet
        // per alias cost it a pass over the level's saved flowgraph layouts each. OpenCAGE -> Level Viewer, on
        // ENTITY_DELETED: the set that deletion took out, which the viewer removes as one batch, where a packet
        // per alias was 6 ms to build and send each. Empty means just `entity`, which is also what either side
        // from before this sends.
        public List<uint> batch_entities = new List<uint>();
    }
}
