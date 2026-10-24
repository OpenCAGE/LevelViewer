using System.Collections.Generic;

namespace OpenCAGE.UnityConnection
{
    public static class TransformSnapDefinitions
    {
        public static readonly IReadOnlyList<float> GridSnapValues = new float[]
        {
            0f,
            0.25f,
            0.5f,
            1f,
            2f,
            5f,
            10f,
        };

        public static readonly IReadOnlyList<float> RotationSnapValues = new float[]
        {
            0f,
            1f,
            5f,
            15f,
            30f,
            45f,
            90f,
        };

        public static string FormatGridSnapLabel(float value)
        {
            return value <= 0f ? "Off" : value.ToString("0.#######");
        }

        public static string FormatRotationSnapLabel(float value)
        {
            return value <= 0f ? "Off" : value.ToString("0.#######") + "°";
        }
    }
}
