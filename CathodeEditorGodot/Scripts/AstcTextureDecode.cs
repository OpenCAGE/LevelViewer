using System;
using AstcSharp;
using AstcSharp.Core;
using CATHODE;

/// <summary>
/// CPU ASTC LDR decode (used for 12x12 and when Godot cannot decompress ASTC on the host GPU).
/// </summary>
public static class AstcTextureDecode
{
	public static bool IsAstcFormat(Textures.TextureFormat format)
	{
		return format is Textures.TextureFormat.ASTC4X4
			or Textures.TextureFormat.ASTC8X8
			or Textures.TextureFormat.ASTC12X12;
	}

	public static int GetCompressedByteSize(int width, int height, Textures.TextureFormat format)
	{
		if (!TryGetBlockSize(format, out int blockW, out int blockH))
			return -1;

		int blocksW = (Math.Max(width, 1) + blockW - 1) / blockW;
		int blocksH = (Math.Max(height, 1) + blockH - 1) / blockH;
		return blocksW * blocksH * 16;
	}

	public static byte[] DecodeToRgba8(byte[] astcData, int width, int height, Textures.TextureFormat format)
	{
		if (astcData == null || width <= 0 || height <= 0 || !TryGetFootprint(format, out Footprint footprint))
			return null;

		int expected = GetCompressedByteSize(width, height, format);
		if (expected <= 0 || astcData.Length < expected)
			return null;

		ReadOnlySpan<byte> span = astcData.AsSpan(0, expected);
		Span<byte> rgba = AstcDecoder.DecompressImage(span, width, height, footprint);
		return rgba.ToArray();
	}

	private static bool TryGetBlockSize(Textures.TextureFormat format, out int blockW, out int blockH)
	{
		switch (format)
		{
			case Textures.TextureFormat.ASTC4X4:
				blockW = 4;
				blockH = 4;
				return true;
			case Textures.TextureFormat.ASTC8X8:
				blockW = 8;
				blockH = 8;
				return true;
			case Textures.TextureFormat.ASTC12X12:
				blockW = 12;
				blockH = 12;
				return true;
			default:
				blockW = 0;
				blockH = 0;
				return false;
		}
	}

	private static bool TryGetFootprint(Textures.TextureFormat format, out Footprint footprint)
	{
		if (!TryGetFootprintType(format, out FootprintType footprintType))
		{
			footprint = default;
			return false;
		}

		footprint = Footprint.FromFootprintType(footprintType);
		return true;
	}

	private static bool TryGetFootprintType(Textures.TextureFormat format, out FootprintType footprintType)
	{
		switch (format)
		{
			case Textures.TextureFormat.ASTC4X4:
				footprintType = FootprintType.Footprint4x4;
				return true;
			case Textures.TextureFormat.ASTC8X8:
				footprintType = FootprintType.Footprint8x8;
				return true;
			case Textures.TextureFormat.ASTC12X12:
				footprintType = FootprintType.Footprint12x12;
				return true;
			default:
				footprintType = default;
				return false;
		}
	}
}
