using System.Collections.Generic;

namespace OpenCAGE.UnityConnection
{
    /// <summary>
    /// One Zone entity and the parts of the level it claims, as OpenCAGE works it out. Travels on a
    /// ZONES_CHANGED packet; canonical copy for Level Viewer and OpenCAGE (linked in OpenCAGE.csproj).
    /// </summary>
    /// <remarks>
    /// <see cref="roots"/> holds instance paths (entity ids from the level root) rather than the whole
    /// membership: a zone reaches an entity and then everything inside that entity's composite, which
    /// is exactly a node and its subtree in the viewer's scene. Sending the roots keeps this to the
    /// dozens of entries a zone actually names instead of the tens of thousands it ends up covering.
    ///
    /// It lives apart from Packet.cs so the calculation can be built and tested on its own, against
    /// nothing but CathodeLib.
    /// </remarks>
    public class SyncedZone
    {
        public uint zone_entity;
        public uint zone_composite;
        public string name = "";

        /// <summary>
        /// The entity ids stepped through to reach the zone itself, from wherever the walk started.
        /// A zone is per placement, not per entity, so this is the other half of which zone this is -
        /// and it is the way back to it through the hierarchy rather than by opening its composite bare.
        /// </summary>
        public List<uint> zone_path = new List<uint>();

        //Picked from the zone's id, so a zone is the same colour every time the level is opened
        public float colour_r;
        public float colour_g;
        public float colour_b;

        public List<List<uint>> roots = new List<List<uint>>();
    }
}
