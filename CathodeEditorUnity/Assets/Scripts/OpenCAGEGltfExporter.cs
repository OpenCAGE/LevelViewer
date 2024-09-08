using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CATHODE;
using GLTF.Schema;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityGLTF.Plugins;
using WebSocketSharp;
using static CATHODE.Materials.Material;
using static UnityEngine.Rendering.DebugUI;

namespace UnityGLTF.Plugins
{
    public class OpenCAGEGltfPlugin : GLTFExportPlugin
    {
        public override string DisplayName => "OpenCAGE_Gltf_Export_Plugin";
        public override string Description => "Allows exporting Cathode Materials as GLTF - Appends extra shader metadata in JSon Tree";
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
            
            foreach (Transform transform in exporter.RootTransforms)
            {
                OpenCAGEShaderMaterialWrapper[] shaderMats = transform.GetComponentsInChildren<OpenCAGEShaderMaterialWrapper>();

                foreach (OpenCAGEShaderMaterialWrapper shaderMat in shaderMats)
                {
                    if (material.name.Equals(shaderMat.materialName))
                    {
                        cathodeShaderContainer = shaderMat;
                        break;
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

                        rootNode.exportTree.shaderParams.Add(shaderParamKey, Convert.ToString(shaderMaterial.shaderParams[shaderParamKey]));
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

                        rootNode.exportTree.shaderTextures.Add(shaderTexKey, Convert.ToString(currentTex.name));
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