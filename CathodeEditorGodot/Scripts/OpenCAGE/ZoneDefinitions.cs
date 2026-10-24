namespace OpenCAGE
{
    /// <summary>
    /// The colour a zone is drawn in. Canonical copy for Level Viewer and OpenCAGE (linked in
    /// OpenCAGE.csproj).
    /// </summary>
    /// <remarks>
    /// A level carries dozens of zones and no palette would tell them apart, so the colour is derived
    /// from the zone's own id: the same zone is the same colour every time the level is opened, and
    /// two zones side by side are almost never near-neighbours in hue. The hue walks in golden-angle
    /// steps rather than being taken straight from the hash, which is what keeps consecutive ids -
    /// which is how a level's zones tend to be numbered - far apart on the wheel.
    ///
    /// Saturation and value are kept high and near-constant: the colour is what the geometry is drawn
    /// in, with only a directional shading term over it, so a dark or washed-out one loses its shape
    /// against its neighbours instead of reading as a zone.
    /// </remarks>
    public static class ZoneDefinitions
    {
        private const float GoldenAngle = 0.61803399f;

        public static void GetColour(uint zoneEntityId, out float r, out float g, out float b)
        {
            //Mix the id first: neighbouring ids would otherwise land on neighbouring golden-angle steps
            uint hash = zoneEntityId;
            hash ^= hash >> 16;
            hash *= 0x7FEB352D;
            hash ^= hash >> 15;
            hash *= 0x846CA68B;
            hash ^= hash >> 16;

            float hue = (hash % 1024u) * GoldenAngle;
            hue -= (int)hue;

            //A little wobble in saturation and value so two zones that do collide in hue still differ
            float saturation = 0.62f + ((hash >> 10) & 3u) * 0.09f;
            float value = 0.84f + ((hash >> 12) & 3u) * 0.05f;

            HsvToRgb(hue, saturation, value, out r, out g, out b);
        }

        private static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
        {
            float sector = h * 6f;
            int index = (int)sector;
            float offset = sector - index;

            float p = v * (1f - s);
            float q = v * (1f - s * offset);
            float t = v * (1f - s * (1f - offset));

            switch (index % 6)
            {
                case 0: r = v; g = t; b = p; return;
                case 1: r = q; g = v; b = p; return;
                case 2: r = p; g = v; b = t; return;
                case 3: r = p; g = q; b = v; return;
                case 4: r = t; g = p; b = v; return;
                default: r = v; g = p; b = q; return;
            }
        }
    }
}
