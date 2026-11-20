using System.Collections.Generic;

namespace OpenCAGE.UnityConnection
{
    /// <summary>
    /// The editor-only markup drawn at every instance of one composite, already built into triangles in the composite's own
    /// Cathode space. Travels on a COMPOSITE_MARKUP packet; canonical copy for Level Viewer and OpenCAGE (linked in OpenCAGE.csproj).
    /// </summary>
    public class SyncedCompositeMarkup
    {
        public uint composite;
        public List<SyncedMarkupSurface> surfaces = new List<SyncedMarkupSurface>();
    }

    /// <summary>One colour's triangles: vertices as x, y, z after one another.</summary>
    public class SyncedMarkupSurface
    {
        public float colour_r = 1f;
        public float colour_g = 1f;
        public float colour_b = 1f;
        public float[] vertices;
        public int[] indices;
    }
}
