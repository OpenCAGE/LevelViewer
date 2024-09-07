using System;
using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityGLTF.Plugins;
using WebSocketSharp;
using static UnityEngine.Rendering.DebugUI;

namespace UnityGLTF.Plugins
{
    public class OpenCAGEGltfPlugin : GLTFExportPlugin
    {
        public override string DisplayName => "OpenCAGE_Gltf_Export_Plugin";
        public override string Description => "Allows exporting multiple material and object variants in one glTF file. Viewers implementing KHR_materials_variants typically allow choosing which variants to display. Disabled objects are emulated with an \"invisible\" material.";
        public override GLTFExportPluginContext CreateInstance(ExportContext context)
        {
            return new OpenCAGE_gltf_export_context();
        }
    }

    public class OpenCAGE_gltf_export_context : GLTFExportPluginContext
    {

        public override void AfterMaterialExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Material material, GLTFMaterial materialNode)
        {
            OpenCAGEShaderMaterialWrapper cathodeShaderContainer = null;

            if (exporter.RootTransforms == null) return;

            /**
            var cathodeShaderContainer = exporter.RootTransforms
                .FirstOrDefault(x => x.GetComponentInChildren<MaterialVariants>())?
                .GetComponent<MaterialVariants>();
            **/

            foreach (Transform transform in exporter.RootTransforms)
            {
                OpenCAGEShaderMaterialWrapper[] shaderMats = transform.GetComponentsInChildren<OpenCAGEShaderMaterialWrapper>();

                foreach (OpenCAGEShaderMaterialWrapper shaderMat in shaderMats)
                {
                    if (material.name.Equals(shaderMat.materialName))
                    {
                        cathodeShaderContainer = shaderMat;
                    }
                }
            }

            if (!cathodeShaderContainer) return;

            exporter.DeclareExtensionUsage(OpenCAGE_gltf_export.EXTENSION_NAME);

            OpenCAGE_gltf_export rootNode = new OpenCAGE_gltf_export();
            materialNode.AddExtension(OpenCAGE_gltf_export.EXTENSION_NAME, rootNode);

            OpenCAGEShaderMaterial shaderMaterial = cathodeShaderContainer.openCAGEShaderMaterial;

            rootNode.exportTree.shaderCategory = new KeyValuePair<string, string>("shaderCategory", shaderMaterial.shaderCategory);

            // Process shader parameters
            if(shaderMaterial.shaderParams != null)
            {
                foreach (string shaderParamKey in shaderMaterial?.shaderParams?.Keys)
                {
                    object currentShaderParam = shaderMaterial.shaderParams[shaderParamKey];

                    if (currentShaderParam != null)
                    {
                        if (currentShaderParam is float && (float)currentShaderParam < 0.1)
                            continue;

                        rootNode.exportTree.shaderParams.Add("shader_param_" + shaderParamKey, Convert.ToString(shaderMaterial.shaderParams[shaderParamKey]));
                    }
                }
            }


            // Process input textures
            if (shaderMaterial.shaderTextures != null)
            {
                foreach (string shaderTexKey in shaderMaterial?.shaderTextures?.Keys)
                {
                    Texture2D currentTex = shaderMaterial.shaderTextures[shaderTexKey];

                    if (currentTex != null)
                    {
                        exporter.ExportTexture(currentTex, shaderTexKey);
                        rootNode.exportTree.shaderParams.Add("shader_tex_" + shaderTexKey, Convert.ToString(currentTex.name));
                    }
                }
            }
        }
    }

    [Serializable]
    public class OpenCAGE_gltf_export : IExtension
    {
        public const string EXTENSION_NAME = "CathodeShaderParam";

        public OpenCAGE_gltf_export_tree exportTree = new OpenCAGE_gltf_export_tree();

        public JProperty Serialize()
        {
            return new JProperty(OpenCAGE_gltf_export.EXTENSION_NAME, JToken.FromObject(exportTree));
        }

        public IExtension Clone(GLTFRoot root)
        {
            return new OpenCAGE_gltf_export() { exportTree = exportTree };
        }
    }

    [Serializable]
    public class OpenCAGE_gltf_export_tree
    {
        public KeyValuePair<string, string> shaderCategory;
        public Dictionary<string, string> shaderTextures = new Dictionary<string, string>();
        public Dictionary<string, string> shaderParams = new Dictionary<string, string>();
    }
}