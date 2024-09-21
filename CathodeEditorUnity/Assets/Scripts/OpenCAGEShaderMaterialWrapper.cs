using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UnityGLTF.Plugins
{
    public class OpenCAGEShaderMaterialWrapper : MonoBehaviour
    {
        public string materialName;
        public OpenCAGEShaderMaterial openCAGEShaderMaterial;
    }
}