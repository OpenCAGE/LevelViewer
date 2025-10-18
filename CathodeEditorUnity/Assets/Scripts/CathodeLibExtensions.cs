using System;
using System.Collections.Generic;
using static CATHODE.Models;
using CathodeLib;
using UnityEngine;
using System.IO;

public static class CathodeLibExtensions
{
    /* Convert a CS2 submesh to Unity Mesh */
    public static Mesh ToMesh(this CS2.Component.LOD.Submesh submesh)
    {
        Mesh mesh = new Mesh();

        List<UInt16> indices = new List<UInt16>();
        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector4> tangents = new List<Vector4>();
        List<Vector2> uv0 = new List<Vector2>();
        List<Vector2> uv1 = new List<Vector2>();
        List<Vector2> uv2 = new List<Vector2>();
        List<Vector2> uv3 = new List<Vector2>();
        List<Vector2> uv7 = new List<Vector2>();

        //TODO: implement skeleton lookup for the indexes
        List<Vector4> boneIndex = new List<Vector4>(); //The indexes of 4 bones that affect each vertex
        List<Vector4> boneWeight = new List<Vector4>(); //The weights for each bone

        if (submesh == null || submesh.Data.Length == 0)
            return mesh;

        using (BinaryReader reader = new BinaryReader(new MemoryStream(submesh.Data)))
        {
            for (int i = 0; i < submesh.VertexFormatFull.Elements.Count; ++i)
            {
                if (i == submesh.VertexFormatFull.Elements.Count - 1)
                {
                    //TODO: should probably properly verify VariableType here 
                    // if (submesh.VertexFormatFull.Elements[i].Count != 1 || submesh.VertexFormatFull.Elements[i][0].VariableType != VBFE_InputType.INDICIES_U16)
                    //     throw new Exception("unexpected format");

                    for (int x = 0; x < submesh.IndexCount; x++)
                        indices.Add(reader.ReadUInt16());

                    continue;
                }

                for (int x = 0; x < submesh.VertexCount; ++x)
                {
                    for (int y = 0; y < submesh.VertexFormatFull.Elements[i].Count; ++y)
                    {
                        AlienVBF.Element format = submesh.VertexFormatFull.Elements[i][y];
                        switch (format.VariableType)
                        {
                            case VBFE_InputType.VECTOR3:
                                {
                                    Vector3 v = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.NORMAL:
                                            normals.Add(v);
                                            break;
                                        case VBFE_InputSlot.TANGENT:
                                            tangents.Add(new Vector4((float)v.x, (float)v.y, (float)v.z, 0));
                                            break;
                                        case VBFE_InputSlot.UV:
                                            //TODO: 3D UVW
                                            break;
                                    };
                                    break;
                                }
                            case VBFE_InputType.INT32:
                                {
                                    int v = reader.ReadInt32();
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.COLOUR:
                                            //??
                                            break;
                                    }
                                    break;
                                }
                            case VBFE_InputType.VECTOR4_BYTE:
                                {
                                    Vector4 v = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.BONE_INDICES:
                                            boneIndex.Add(v);
                                            break;
                                    }
                                    break;
                                }
                            case VBFE_InputType.VECTOR4_BYTE_DIV255:
                                {
                                    Vector4 v = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                                    v /= 255.0f;
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.BONE_WEIGHTS:
                                            boneWeight.Add(v / (v.x + v.y + v.z + v.w));
                                            break;
                                        case VBFE_InputSlot.UV:
                                            uv2.Add(new Vector2(v.x, v.y));
                                            uv3.Add(new Vector2(v.z, v.w));
                                            break;
                                    }
                                    break;
                                }
                            case VBFE_InputType.VECTOR2_INT16_DIV2048:
                                {
                                    Vector2 v = new Vector2(reader.ReadInt16() / 2048.0f, reader.ReadInt16() / 2048.0f);
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.UV:
                                            if (format.VariantIndex == 0) uv0.Add(v);
                                            else if (format.VariantIndex == 1)
                                            {
                                                // TODO: We can figure this out based on AlienVBFE.
                                                //Material->Material.Flags |= Material_HasTexCoord1;
                                                uv1.Add(v);
                                            }
                                            else if (format.VariantIndex == 2) uv2.Add(v);
                                            else if (format.VariantIndex == 3) uv3.Add(v);
                                            else if (format.VariantIndex == 7) uv7.Add(v);
                                            break;
                                    }
                                    break;
                                }
                            case VBFE_InputType.VECTOR4_INT16_DIVMAX:
                                {
                                    Vector4 v = new Vector4(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
                                    v /= (float)Int16.MaxValue;
                                    if (v.w != 0 && v.w != -1 && v.w != 1) throw new Exception("Unexpected vert W");
                                    v *= submesh.VertexScale; //Account for scale
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.VERTEX:
                                            vertices.Add(new Vector3(v.x, v.y, v.z));
                                            break;
                                    }
                                    break;
                                }
                            case VBFE_InputType.VECTOR4_BYTE_NORM:
                                {
                                    Vector4 v = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                                    v /= (float)byte.MaxValue - 0.5f;
                                    v = Vector4.Normalize(v);
                                    switch (format.ShaderSlot)
                                    {
                                        case VBFE_InputSlot.NORMAL:
                                            normals.Add(new Vector3(v.x, v.y, v.z));
                                            break;
                                        case VBFE_InputSlot.TANGENT:
                                            break;
                                        case VBFE_InputSlot.BITANGENT:
                                            break;
                                    }
                                    break;
                                }
                        }
                    }
                }
                Utilities.Align(reader, 16);
            }
        }

        if (vertices.Count == 0) return mesh;

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0); //0??
        mesh.SetTangents(tangents);
        mesh.SetUVs(0, uv0);
        mesh.SetUVs(1, uv1);
        mesh.SetUVs(2, uv2);
        mesh.SetUVs(3, uv3);
        mesh.SetUVs(7, uv7);
        //mesh.SetBoneWeights(InBoneWeights.ToArray());
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        return mesh;
    }
}