using CathodeLib;
using Microsoft.SqlServer.Server;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using static CATHODE.Models;
using static UnityEngine.GraphicsBuffer;

public static class CathodeLibExtensions
{
    /* Convert a CS2 submesh to Unity Mesh */
    public static Mesh ToMesh(this CS2.Component.LOD.Submesh submesh)
    {
        Mesh mesh = new Mesh();

        List<UInt16> indices = new List<UInt16>();
        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector4> binormals = new List<Vector4>();
        List<Vector4> tangents = new List<Vector4>();
        List<Color> colours = new List<Color>();
        List<Vector2>[] uvs = new List<Vector2>[0];

        List<Vector4> boneIndexes = new List<Vector4>();
        List<Vector4> boneWeights = new List<Vector4>();

        if (submesh == null || submesh.Data.Length == 0)
            return mesh;

        using (BinaryReader reader = new BinaryReader(new MemoryStream(submesh.Data)))
        {
            for (int i = 0; i < submesh.VertexFormatFull.Elements.Count; ++i)
            {
                if (i == submesh.VertexFormatFull.Elements.Count - 1)
                {
                    for (int x = 0; x < submesh.IndexCount; x++)
                        indices.Add(reader.ReadUInt16());
                    continue;
                }

                for (int x = 0; x < submesh.VertexCount; ++x)
                {
                    for (int y = 0; y < submesh.VertexFormatFull.Elements[i].Count; ++y)
                    {
                        AlienVBF.Element f = submesh.VertexFormatFull.Elements[i][y];
                        Vector4 v = ReadVertexData(reader, f.Type);

                        switch (f.Usage)
                        {
                            case VertexFormatUsage.POSITION:
                                vertices.Add(v * submesh.VertexScale);
                                break;
                            case VertexFormatUsage.BLENDWEIGHT:
                                boneWeights.Add(v);
                                break;
                            case VertexFormatUsage.BLENDINDICES:
                                boneIndexes.Add(v);
                                break;
                            case VertexFormatUsage.NORMAL:
                                normals.Add(v);
                                break;
                            case VertexFormatUsage.TEXCOORD:
                                if (f.VariantIndex >= uvs.Length)
                                {
                                    Array.Resize(ref uvs, f.VariantIndex + 1);
                                    uvs[f.VariantIndex] ??= new List<Vector2>();
                                }
                                uvs[f.VariantIndex].Add(v);
                                break;
                            case VertexFormatUsage.TANGENT:
                                tangents.Add(v);
                                break;
                            case VertexFormatUsage.BINORMAL:
                                binormals.Add(v);
                                break;
                            case VertexFormatUsage.COLOR:
                                colours.Add(v);
                                break;
                        }
                    }
                }
                Utilities.Align(reader, 16);
            }
        }

        if (vertices.Count == 0) return mesh;

        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetIndices(indices, MeshTopology.Triangles, 0); 
        mesh.SetTangents(tangents);
        mesh.SetColors(colours);
        for (int i = 0; i < uvs.Length; ++i)
        {
            if (uvs[i] == null) continue;
            mesh.SetUVs(i, uvs[i]);
        }
        mesh.boneWeights = ConvertToBoneWeights(boneIndexes, boneWeights);
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();

        return mesh;
    }

    private static Vector4 ReadVertexData(BinaryReader reader, VertexFormatType type)
    {
        switch (type)
        {
            case VertexFormatType.FLOAT1:
                return new Vector4(reader.ReadSingle(), 0, 0, 0);
            case VertexFormatType.FLOAT2:
                return new Vector4(reader.ReadSingle(), reader.ReadSingle(), 0, 0);
            case VertexFormatType.FLOAT3:
                return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), 0);
            case VertexFormatType.FLOAT4:
                return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            case VertexFormatType.COLOUR:
                uint data = reader.ReadUInt32();
                return new Vector4((float)((data & 0xFF000000) >> 24) / 255.0f, (float)((data & 0x00FF0000) >> 16) / 255.0f, (float)((data & 0x0000FF00) >> 8) / 255.0f, (float)((data & 0x000000FF) >> 0) / 255.0f);
            case VertexFormatType.UBYTE4:
                return new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            case VertexFormatType.SHORT2:
                return new Vector4(reader.ReadInt16(), reader.ReadInt16(), 0, 0);
            case VertexFormatType.SHORT4:
                return new Vector4(reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16());
            case VertexFormatType.UBYTE4N:
                return new Vector4((float)reader.ReadByte() / 255.0f, (float)reader.ReadByte() / 255.0f, (float)reader.ReadByte() / 255.0f, (float)reader.ReadByte() / 255.0f);
            case VertexFormatType.SHORT2N:
                return new Vector4((float)reader.ReadInt16() / (float)Int16.MaxValue, (float)reader.ReadInt16() / (float)Int16.MaxValue, 0, 0);
            case VertexFormatType.SHORT4N:
                return new Vector4((float)reader.ReadInt16() / (float)Int16.MaxValue, (float)reader.ReadInt16() / (float)Int16.MaxValue, (float)reader.ReadInt16() / (float)Int16.MaxValue, (float)reader.ReadInt16() / (float)Int16.MaxValue);
            case VertexFormatType.USHORT2N:
                return new Vector4((float)reader.ReadUInt16() / (float)UInt16.MaxValue, (float)reader.ReadUInt16() / (float)UInt16.MaxValue, 0, 0);
            case VertexFormatType.USHORT4N:
                return new Vector4((float)reader.ReadUInt16() / (float)UInt16.MaxValue, (float)reader.ReadUInt16() / (float)UInt16.MaxValue, (float)reader.ReadUInt16() / (float)UInt16.MaxValue, (float)reader.ReadUInt16() / (float)UInt16.MaxValue);
            case VertexFormatType.DEC3N:
                uint val = reader.ReadUInt32();
                short sx = (short)((val >> 20) & 0x3ff);
                short sy = (short)((val >> 10) & 0x3ff);
                short sz = (short)((val) & 0x3ff);
                return new Vector4(((sx < 512) ? sx : (sx - 1024)) / 511.0f, ((sy < 512) ? sy : (sy - 1024)) / 511.0f, ((sz < 512) ? sz : (sz - 1024)) / 511.0f, 0);
        }
        throw new Exception("Unsupported VertexFormatType");
    }

    private static BoneWeight[] ConvertToBoneWeights(List<Vector4> boneIndexes, List<Vector4> boneWeights)
    {
        if (boneIndexes.Count != boneWeights.Count)
            return new BoneWeight[0];

        BoneWeight[] newBoneWeights = new BoneWeight[boneIndexes.Count];
        for (int i = 0; i < newBoneWeights.Length; i++)
        {
            BoneWeight bw = new BoneWeight();
            Vector4 indexes = boneIndexes[i];
            Vector4 weights = boneWeights[i];

            bw.boneIndex0 = (int)indexes.x;
            bw.boneIndex1 = (int)indexes.y;
            bw.boneIndex2 = (int)indexes.z;
            bw.boneIndex3 = (int)indexes.w;

            bw.weight0 = weights.x;
            bw.weight1 = weights.y;
            bw.weight2 = weights.z;
            bw.weight3 = weights.w;

            newBoneWeights[i] = bw;
        }
        return newBoneWeights;
    }
}