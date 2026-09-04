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

        // Level Viewer -> OpenCAGE: copy the given entity to the shared entity clipboard (Ctrl+C).
        ENTITY_CLIPBOARD_COPY,
        // Level Viewer -> OpenCAGE: paste the shared entity clipboard into the given composite (Ctrl+V).
        ENTITY_CLIPBOARD_PASTE,

        // OpenCAGE -> Level Viewer: something was dropped on the viewport at drop_viewport_x/y. Only
        // this side has the geometry, so it raycasts the position and answers with ENTITY_CREATE_REQUEST.
        VIEWPORT_DROP_REQUEST,
    }

    public class Packet
    {
        public Packet(PacketEvent packet_event = PacketEvent.GENERIC_DATA_SYNC)
        {
            this.packet_event = packet_event;
        }

        //Packet metadata
        public PacketEvent packet_event;
        public int version = 10;

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

        // Level Viewer log line forwarded to OpenCAGE (VIEWER_LOG).
        public string log_message = "";
        public bool log_is_error = false;

        public SyncedMaterialMappingSet material_mapping = null;
    }
}
