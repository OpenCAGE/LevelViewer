using Godot;
using System;
using CATHODE;

/// <summary>
/// Loads Cathode TEX4 data into Godot Image/Texture2D (decompress + channel fixes).
/// </summary>
public static class AlienSceneTextures
{
	public static Image.Format MapImageFormat(Textures.TextureFormat format)
	{
		switch (format)
		{
			case Textures.TextureFormat.A32R32G32B32F:
				return Image.Format.Rgbaf;
			case Textures.TextureFormat.A16R16G16B16:
				return Image.Format.Rgbah;
			case Textures.TextureFormat.A8R8G8B8:
				return Image.Format.Rgba8;
			case Textures.TextureFormat.X8R8G8B8:
				return Image.Format.Rgb8;
			case Textures.TextureFormat.A8:
				return Image.Format.R8;
			case Textures.TextureFormat.L8:
				return Image.Format.L8;
			case Textures.TextureFormat.DXT1:
				return Image.Format.Dxt1;
			case Textures.TextureFormat.DXT5:
				return Image.Format.Dxt5;
			case Textures.TextureFormat.DXN:
				return Image.Format.RgtcRg;
			case Textures.TextureFormat.A4R4G4B4:
				return Image.Format.Rgba8;
			case Textures.TextureFormat.BC6H:
				return Image.Format.BptcRgbfu;
			case Textures.TextureFormat.BC7:
				return Image.Format.BptcRgba;
			case Textures.TextureFormat.R16F:
				return Image.Format.Rf;
			case Textures.TextureFormat.ASTC4X4:
				return Image.Format.Astc4X4;
			case Textures.TextureFormat.ASTC8X8:
				return Image.Format.Astc8X8;
			case Textures.TextureFormat.ASTC12X12:
				return Image.Format.Max;
			default:
				return Image.Format.Max;
		}
	}

	public static bool IsBlockCompressed(Image.Format format)
	{
		return format == Image.Format.Dxt1
			|| format == Image.Format.Dxt3
			|| format == Image.Format.Dxt5
			|| format == Image.Format.RgtcR
			|| format == Image.Format.RgtcRg
			|| format == Image.Format.BptcRgba
			|| format == Image.Format.BptcRgbf
			|| format == Image.Format.BptcRgbfu
			|| format == Image.Format.Astc4X4
			|| format == Image.Format.Astc8X8;
	}

	/// <summary>
	/// Cathode stores a full D3D-style mip chain in one blob; Godot CreateFromData (use_mipmaps=false) needs only level 0 bytes.
	/// </summary>
	public static int GetBaseMipByteSize(int width, int height, Image.Format format, Textures.TextureFormat? sourceFormat = null)
	{
		if (sourceFormat.HasValue && AstcTextureDecode.IsAstcFormat(sourceFormat.Value))
			return AstcTextureDecode.GetCompressedByteSize(width, height, sourceFormat.Value);

		int w = Mathf.Max(width, 1);
		int h = Mathf.Max(height, 1);

		switch (format)
		{
			case Image.Format.Dxt1:
				return BlockCompressedByteSize(w, h, 8);
			case Image.Format.Dxt3:
			case Image.Format.Dxt5:
			case Image.Format.RgtcR:
			case Image.Format.RgtcRg:
			case Image.Format.BptcRgba:
			case Image.Format.BptcRgbf:
			case Image.Format.BptcRgbfu:
				return BlockCompressedByteSize(w, h, 16);
			case Image.Format.Rgbaf:
				return w * h * 16;
			case Image.Format.Rgbah:
				return w * h * 8;
			case Image.Format.Rgba8:
			case Image.Format.Rgb8:
				return w * h * 4;
			case Image.Format.R8:
			case Image.Format.L8:
				return w * h;
			case Image.Format.Rf:
				return w * h * 4;
			default:
				return -1;
		}
	}

	public static byte[] ExtractBaseMipData(byte[] content, int width, int height, Image.Format format, Textures.TextureFormat? sourceFormat = null)
	{
		int baseSize = GetBaseMipByteSize(width, height, format, sourceFormat);
		if (content == null || baseSize <= 0 || content.Length < baseSize)
			return null;

		if (content.Length == baseSize)
			return content;

		byte[] baseOnly = new byte[baseSize];
		Array.Copy(content, 0, baseOnly, 0, baseSize);
		return baseOnly;
	}

	public static Texture2D CreateTextureFromTexPart(Textures.TEX4.Texture texPart, Textures.TextureFormat sourceFormat, string name)
	{
		if (texPart?.Content == null || texPart.Content.Length == 0)
			return null;

		Image.Format format = MapImageFormat(sourceFormat);
		if (format == Image.Format.Max && !AstcTextureDecode.IsAstcFormat(sourceFormat))
			return null;

		int width = (int)texPart.Width;
		int height = (int)texPart.Height;
		if (width <= 0 || height <= 0)
			return null;

		Image image = CreateImageFromRaw(texPart.Content, width, height, format, sourceFormat, name);
		if (image == null)
			return null;

		Texture2D texture = ImageTexture.CreateFromImage(image);
		texture.ResourceName = name;
		return texture;
	}

	public static Image CreateImageFromRaw(byte[] content, int width, int height, Image.Format format, Textures.TextureFormat sourceFormat, string name)
	{
		if (AstcTextureDecode.IsAstcFormat(sourceFormat))
			return CreateImageFromAstc(content, width, height, sourceFormat, name);

		byte[] upload = ExtractBaseMipData(content, width, height, format, sourceFormat);
		int expectedBase = GetBaseMipByteSize(width, height, format, sourceFormat);
		if (upload == null)
		{
			GD.PrintErr($"Texture data too small for '{name}' ({sourceFormat}, {width}x{height}, bytes={content?.Length ?? 0}, expected base mip {expectedBase})");
			return null;
		}

		Image image = Image.CreateFromData(width, height, false, format, upload);
		if (image == null || image.IsEmpty())
		{
			GD.PrintErr($"Image.CreateFromData failed for '{name}' ({sourceFormat}, {width}x{height}, upload={upload.Length}, total={content.Length})");
			return null;
		}

		if (IsBlockCompressed(format))
			image.Decompress();

		if (sourceFormat == Textures.TextureFormat.A8R8G8B8)
			SwizzleBgraToRgba(image);

		image.GenerateMipmaps();
		return image;
	}

	private static Image CreateImageFromAstc(byte[] content, int width, int height, Textures.TextureFormat sourceFormat, string name)
	{
		byte[] baseMip = ExtractBaseMipData(content, width, height, Image.Format.Max, sourceFormat);
		int expectedBase = AstcTextureDecode.GetCompressedByteSize(width, height, sourceFormat);
		if (baseMip == null)
		{
			GD.PrintErr($"ASTC data too small for '{name}' ({sourceFormat}, {width}x{height}, bytes={content?.Length ?? 0}, expected {expectedBase})");
			return null;
		}

		if (sourceFormat == Textures.TextureFormat.ASTC12X12)
			return CreateImageFromAstcCpuDecode(baseMip, width, height, sourceFormat, name);

		Image.Format godotAstc = MapImageFormat(sourceFormat);
		Image image = Image.CreateFromData(width, height, false, godotAstc, baseMip);
		if (image != null && !image.IsEmpty() && image.Decompress() == Error.Ok && image.GetFormat() == Image.Format.Rgba8)
		{
			image.GenerateMipmaps();
			return image;
		}

		return CreateImageFromAstcCpuDecode(baseMip, width, height, sourceFormat, name);
	}

	private static Image CreateImageFromAstcCpuDecode(byte[] baseMip, int width, int height, Textures.TextureFormat sourceFormat, string name)
	{
		byte[] rgba = AstcTextureDecode.DecodeToRgba8(baseMip, width, height, sourceFormat);
		if (rgba == null)
		{
			GD.PrintErr($"ASTC CPU decode failed for '{name}' ({sourceFormat}, {width}x{height})");
			return null;
		}

		Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
		if (image == null || image.IsEmpty())
		{
			GD.PrintErr($"Image.CreateFromData failed after ASTC decode for '{name}' ({sourceFormat}, {width}x{height})");
			return null;
		}

		image.GenerateMipmaps();
		return image;
	}

	private static int BlockCompressedByteSize(int width, int height, int bytesPerBlock)
	{
		int blocksW = (width + 3) / 4;
		int blocksH = (height + 3) / 4;
		return blocksW * blocksH * bytesPerBlock;
	}

	private static void SwizzleBgraToRgba(Image image)
	{
		if (image.GetFormat() != Image.Format.Rgba8)
			return;

		byte[] data = image.GetData();
		for (int i = 0; i + 3 < data.Length; i += 4)
		{
			byte b = data[i];
			data[i] = data[i + 2];
			data[i + 2] = b;
		}

		image.SetData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
	}
}
