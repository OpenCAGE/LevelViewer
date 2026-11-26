using Godot;
using System;
using System.Collections.Generic;
using CATHODE;

/// <summary>CPU-baked texture upload payload; Godot Image/Texture2D created on the main thread.</summary>
public sealed class BakedTextureCpu
{
	public byte[] Upload;
	public int Width;
	public int Height;
	public Image.Format Format;
	public Textures.TextureFormat SourceFormat;
	public bool HasTransparency;
	public bool GenerateMipmaps;
	public bool SwizzleBgra;
	public bool DecompressBlockCompressed;
}

/// <summary>
/// Loads Cathode TEX4 data into Godot Image/Texture2D (GPU-compressed where possible).
/// </summary>
public static class AlienSceneTextures
{
	private static readonly Dictionary<Texture2D, bool> _transparencyCache = new Dictionary<Texture2D, bool>();

	public static void ClearTransparencyCache()
	{
		_transparencyCache.Clear();
	}

	public static void ForgetTransparency(Texture2D texture)
	{
		if (texture != null)
			_transparencyCache.Remove(texture);
	}

	public static void RegisterTransparency(Texture2D texture, bool hasTransparency)
	{
		if (texture != null)
			_transparencyCache[texture] = hasTransparency;
	}

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

		byte[] baseOnly = new byte[baseSize];
		Array.Copy(content, 0, baseOnly, 0, baseSize);
		return baseOnly;
	}

	public static Texture2D CreateTextureFromTexPart(Textures.TEX4.Texture texPart, Textures.TextureFormat sourceFormat, string name)
	{
		if (!TryBakeCpu(texPart, sourceFormat, out BakedTextureCpu baked))
			return null;

		return CreateTextureFromBaked(baked, name);
	}

	public static bool TryBakeCpu(Textures.TEX4 tex, out BakedTextureCpu baked)
	{
		baked = null;
		if (tex == null)
			return false;

		Textures.TEX4.Texture texPart = SelectTexPart(tex);
		if (texPart == null)
			return false;

		return TryBakeCpu(texPart, tex.Format, out baked);
	}

	public static bool TryBakeCpu(Textures.TEX4.Texture texPart, Textures.TextureFormat sourceFormat, out BakedTextureCpu baked)
	{
		baked = null;
		if (texPart?.Content == null || texPart.Content.Length == 0)
			return false;

		Image.Format format = MapImageFormat(sourceFormat);
		if (format == Image.Format.Max && !AstcTextureDecode.IsAstcFormat(sourceFormat))
			return false;

		int width = (int)texPart.Width;
		int height = (int)texPart.Height;
		if (width <= 0 || height <= 0)
			return false;

		if (AstcTextureDecode.IsAstcFormat(sourceFormat))
			return TryBakeAstcCpu(texPart.Content, width, height, sourceFormat, out baked);

		byte[] upload = ExtractBaseMipData(texPart.Content, width, height, format, sourceFormat);
		if (upload == null)
			return false;

		bool blockCompressed = IsBlockCompressed(format);
		baked = new BakedTextureCpu
		{
			Upload = upload,
			Width = width,
			Height = height,
			Format = format,
			SourceFormat = sourceFormat,
			HasTransparency = DetectTransparencyFromContent(texPart.Content, width, height, sourceFormat),
			GenerateMipmaps = !blockCompressed,
			SwizzleBgra = sourceFormat == Textures.TextureFormat.A8R8G8B8,
			DecompressBlockCompressed = blockCompressed && sourceFormat == Textures.TextureFormat.A4R4G4B4,
		};
		return true;
	}

	public static Texture2D CreateTextureFromBaked(BakedTextureCpu baked, string name)
	{
		if (baked?.Upload == null || baked.Upload.Length == 0)
			return null;

		//Uploaded by CreateFromImage below and not kept: released here rather than by a finalizer, for every texture a load converts
		using Image image = Image.CreateFromData(baked.Width, baked.Height, false, baked.Format, baked.Upload);
		if (image == null || image.IsEmpty())
			return null;

		if (baked.DecompressBlockCompressed)
			image.Decompress();
		else if (baked.SwizzleBgra)
			SwizzleBgraToRgba(image);

		if (baked.GenerateMipmaps)
			image.GenerateMipmaps();

		Texture2D texture = ImageTexture.CreateFromImage(image);
		texture.ResourceName = name;
		texture.ResourceLocalToScene = false;
		_transparencyCache[texture] = baked.HasTransparency;
		return texture;
	}

	private static Textures.TEX4.Texture SelectTexPart(Textures.TEX4 tex)
	{
		if (tex?.TextureStreamed?.Content != null && tex.TextureStreamed.Content.Length > 0)
			return tex.TextureStreamed;
		if (tex?.TexturePersistent?.Content != null && tex.TexturePersistent.Content.Length > 0)
			return tex.TexturePersistent;
		return null;
	}

	private static bool TryBakeAstcCpu(
		byte[] content,
		int width,
		int height,
		Textures.TextureFormat sourceFormat,
		out BakedTextureCpu baked)
	{
		baked = null;
		byte[] baseMip = ExtractBaseMipData(content, width, height, Image.Format.Max, sourceFormat);
		if (baseMip == null)
			return false;

		byte[] upload = baseMip;
		Image.Format format = MapImageFormat(sourceFormat);
		bool generateMipmaps = false;

		if (sourceFormat == Textures.TextureFormat.ASTC12X12)
		{
			byte[] rgba = AstcTextureDecode.DecodeToRgba8(baseMip, width, height, sourceFormat);
			if (rgba == null)
				return false;

			upload = rgba;
			format = Image.Format.Rgba8;
			generateMipmaps = true;
		}

		baked = new BakedTextureCpu
		{
			Upload = upload,
			Width = width,
			Height = height,
			Format = format,
			SourceFormat = sourceFormat,
			HasTransparency = DetectTransparencyFromContent(content, width, height, sourceFormat),
			GenerateMipmaps = generateMipmaps,
		};
		return true;
	}

	public static Image CreateImageFromRaw(byte[] content, int width, int height, Image.Format format, Textures.TextureFormat sourceFormat, string name)
	{
		if (AstcTextureDecode.IsAstcFormat(sourceFormat))
			return CreateImageFromAstc(content, width, height, sourceFormat, name);

		byte[] upload = ExtractBaseMipData(content, width, height, format, sourceFormat);
		int expectedBase = GetBaseMipByteSize(width, height, format, sourceFormat);
		if (upload == null)
		{
			ViewerLog.PrintErr($"Texture data too small for '{name}' ({sourceFormat}, {width}x{height}, bytes={content?.Length ?? 0}, expected base mip {expectedBase})");
			return null;
		}

		Image image = Image.CreateFromData(width, height, false, format, upload);
		if (image == null || image.IsEmpty())
		{
			ViewerLog.PrintErr($"Image.CreateFromData failed for '{name}' ({sourceFormat}, {width}x{height}, upload={upload.Length}, total={content.Length})");
			return null;
		}

		// Keep block-compressed data on the GPU; decompressing every environment map to RGBA8+mips dominates RAM.
		if (IsBlockCompressed(format))
		{
			if (sourceFormat == Textures.TextureFormat.A4R4G4B4)
				image.Decompress();
		}
		else
		{
			if (sourceFormat == Textures.TextureFormat.A8R8G8B8)
				SwizzleBgraToRgba(image);

			image.GenerateMipmaps();
		}

		return image;
	}

	private static Image CreateImageFromAstc(byte[] content, int width, int height, Textures.TextureFormat sourceFormat, string name)
	{
		byte[] baseMip = ExtractBaseMipData(content, width, height, Image.Format.Max, sourceFormat);
		int expectedBase = AstcTextureDecode.GetCompressedByteSize(width, height, sourceFormat);
		if (baseMip == null)
		{
			ViewerLog.PrintErr($"ASTC data too small for '{name}' ({sourceFormat}, {width}x{height}, bytes={content?.Length ?? 0}, expected {expectedBase})");
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
			ViewerLog.PrintErr($"ASTC CPU decode failed for '{name}' ({sourceFormat}, {width}x{height})");
			return null;
		}

		Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
		if (image == null || image.IsEmpty())
		{
			ViewerLog.PrintErr($"Image.CreateFromData failed after ASTC decode for '{name}' ({sourceFormat}, {width}x{height})");
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

	/// <summary>
	/// Sparse scan for non-opaque pixels (matches OpenCAGE MaterialApplier.ImageSourceHasTransparency).
	/// Uses the one-time cache from conversion; avoids GetImage() decompressing GPU textures again.
	/// </summary>
	public static bool HasTransparency(Texture2D texture)
	{
		if (texture == null)
			return false;

		if (_transparencyCache.TryGetValue(texture, out bool cached))
			return cached;

		using Image image = texture.GetImage();
		if (image == null || image.IsEmpty())
			return false;

		return ScanImageForTransparency(image);
	}

	/// <summary>One-time alpha scan from Cathode source bytes before Content is released.</summary>
	/// <remarks>
	/// Reads the base mip's alpha straight from the bytes (DXT5 and BC7 decoded a block at a time) with the same sampling
	/// and thresholds as ScanImageForTransparency. It runs on the prewarm's worker threads: decoding each texture to an RGBA8
	/// Godot Image there ran the process out of memory on big levels.
	/// </remarks>
	public static bool DetectTransparencyFromContent(byte[] content, int width, int height, Textures.TextureFormat sourceFormat)
	{
		if (content == null || content.Length == 0 || width <= 0 || height <= 0)
			return false;

		if (AstcTextureDecode.IsAstcFormat(sourceFormat))
		{
			byte[] baseMip = ExtractBaseMipData(content, width, height, Image.Format.Max, sourceFormat);
			byte[] rgba = baseMip == null ? null : AstcTextureDecode.DecodeToRgba8(baseMip, width, height, sourceFormat);
			return rgba != null && rgba.Length >= (long)width * height * 4 && ScanRgba8Alpha(rgba, width, height);
		}

		Image.Format format = MapImageFormat(sourceFormat);
		int baseSize = GetBaseMipByteSize(width, height, format, sourceFormat);
		if (baseSize <= 0 || content.Length < baseSize)
			return false;

		switch (format)
		{
			case Image.Format.Rgba8:
				return ScanRgba8Alpha(content, width, height);
			case Image.Format.Rgbah:
				return ScanFloatAlpha(content, width, height, false);
			case Image.Format.Rgbaf:
				return ScanFloatAlpha(content, width, height, true);
			case Image.Format.Dxt5:
				return ScanBlockAlpha(content, width, height, false);
			case Image.Format.BptcRgba:
				return ScanBlockAlpha(content, width, height, true);
			default:
				//DXT1, X8R8G8B8, L8 and R16F were never scanned; A8, DXN and BC6H all decode with alpha 255
				return false;
		}
	}

	/// <summary>The every-fourth-texel tally ScanImageForTransparency keeps.</summary>
	private struct AlphaTally
	{
		private readonly long _samples;
		private long _tested;
		private long _low;
		private int _min;

		public AlphaTally(long texels)
		{
			_samples = (texels + 3) / 4;
			_tested = 0;
			_low = 0;
			_min = byte.MaxValue;
		}

		public void Add(int alpha)
		{
			_tested++;
			if (alpha < _min)
				_min = alpha;
			if (alpha < 250)
				_low++;
		}

		/// <summary>The answer can only be yes from here.</summary>
		public bool Settled => _min <= 16 || _low * 200 >= _samples;

		public bool Result => _tested > 0 && (_min <= 16 || _low * 200 >= _tested);
	}

	private static bool ScanRgba8Alpha(byte[] rgba, int width, int height)
	{
		long texels = (long)width * height;
		AlphaTally tally = new AlphaTally(texels);
		for (long texel = 0; texel < texels; texel += 4)
		{
			tally.Add(rgba[texel * 4 + 3]);
			if (tally.Settled)
				return true;
		}
		return tally.Result;
	}

	private static bool ScanFloatAlpha(byte[] content, int width, int height, bool fullFloat)
	{
		long texels = (long)width * height;
		int stride = fullFloat ? 16 : 8;
		AlphaTally tally = new AlphaTally(texels);
		for (long texel = 0; texel < texels; texel += 4)
		{
			int offset = (int)(texel * stride) + (fullFloat ? 12 : 6);
			float alpha = fullFloat ? BitConverter.ToSingle(content, offset) : (float)BitConverter.ToHalf(content, offset);
			tally.Add(float.IsNaN(alpha) ? 0 : (int)Math.Clamp(alpha * 255.0f, 0.0f, 255.0f));
			if (tally.Settled)
				return true;
		}
		return tally.Result;
	}

	private static bool ScanBlockAlpha(byte[] content, int width, int height, bool bc7)
	{
		int blocksWide = (width + 3) / 4;
		int blocksHigh = (height + 3) / 4;
		AlphaTally tally = new AlphaTally((long)width * height);
		Span<byte> alpha = stackalloc byte[16];
		for (int blockY = 0; blockY < blocksHigh; blockY++)
		{
			for (int blockX = 0; blockX < blocksWide; blockX++)
			{
				int offset = (blockY * blocksWide + blockX) * 16;
				if (bc7)
					DecodeBc7Alpha(content, offset, alpha);
				else
					DecodeDxt5Alpha(content, offset, alpha);

				for (int row = 0; row < 4; row++)
				{
					int y = blockY * 4 + row;
					if (y >= height)
						break;
					for (int column = 0; column < 4; column++)
					{
						int x = blockX * 4 + column;
						if (x >= width)
							break;
						if ((((long)y * width + x) & 3) == 0)
							tally.Add(alpha[row * 4 + column]);
					}
				}

				if (tally.Settled)
					return true;
			}
		}
		return tally.Result;
	}

	private static void DecodeDxt5Alpha(byte[] block, int offset, Span<byte> alpha)
	{
		int a0 = block[offset];
		int a1 = block[offset + 1];
		Span<byte> palette = stackalloc byte[8];
		palette[0] = (byte)a0;
		palette[1] = (byte)a1;
		if (a0 > a1)
		{
			for (int i = 1; i <= 6; i++)
				palette[i + 1] = (byte)(((7 - i) * a0 + i * a1 + 1) / 7);
		}
		else
		{
			for (int i = 1; i <= 4; i++)
				palette[i + 1] = (byte)(((5 - i) * a0 + i * a1 + 1) / 5);
			palette[6] = 0;
			palette[7] = 255;
		}

		ulong indices = 0;
		for (int i = 0; i < 6; i++)
			indices |= (ulong)block[offset + 2 + i] << (8 * i);
		for (int i = 0; i < 16; i++)
		{
			alpha[i] = palette[(int)(indices & 7)];
			indices >>= 3;
		}
	}

	//BC7 two-subset partitions (bit n = texel n's subset) and subset 1's anchor texel
	private static readonly ushort[] Bc7Partition2 =
	{
		0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80,
		0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
		0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE,
		0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
		0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A,
		0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
		0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C,
		0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
	};

	private static readonly byte[] Bc7Anchor2 =
	{
		15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
		15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
		15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
		6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
	};

	private static readonly byte[] Bc7Weights2 = { 0, 21, 43, 64 };
	private static readonly byte[] Bc7Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
	private static readonly byte[] Bc7Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

	/// <summary>The final alpha of a BC7 block's 16 texels (after any mode 4/5 channel rotation).</summary>
	private static void DecodeBc7Alpha(byte[] block, int offset, Span<byte> alpha)
	{
		Bc7Bits bits = new Bc7Bits(BitConverter.ToUInt64(block, offset), BitConverter.ToUInt64(block, offset + 8));
		int mode = 0;
		while (mode < 8 && bits.Read(1) == 0)
			mode++;

		if (mode == 8)
		{
			alpha.Fill(0); //reserved mode decodes to zero
			return;
		}
		if (mode < 4)
		{
			alpha.Fill(255); //modes 0-3 carry no alpha
			return;
		}

		if (mode == 4 || mode == 5)
		{
			int rotation = bits.Read(2);
			int indexSelection = mode == 4 ? bits.Read(1) : 0;
			int colourBits = mode == 4 ? 5 : 7;
			int alphaBits = mode == 4 ? 6 : 8;

			//R0 R1 G0 G1 B0 B1 A0 A1
			Span<int> endpoints = stackalloc int[8];
			for (int i = 0; i < 6; i++)
				endpoints[i] = Bc7Unquantise(bits.Read(colourBits), colourBits);
			endpoints[6] = Bc7Unquantise(bits.Read(alphaBits), alphaBits);
			endpoints[7] = Bc7Unquantise(bits.Read(alphaBits), alphaBits);

			int secondaryIndexBits = mode == 4 ? 3 : 2;
			Span<byte> primary = stackalloc byte[16];
			Span<byte> secondary = stackalloc byte[16];
			for (int i = 0; i < 16; i++)
				primary[i] = (byte)bits.Read(i == 0 ? 1 : 2);
			for (int i = 0; i < 16; i++)
				secondary[i] = (byte)bits.Read(i == 0 ? secondaryIndexBits - 1 : secondaryIndexBits);

			//Rotation 1-3 swaps alpha with R, G or B, so the final alpha is that colour channel
			int channel = rotation == 0 ? 3 : rotation - 1;
			bool alphaUsesSecondary = indexSelection == 0;
			bool useSecondary = channel == 3 ? alphaUsesSecondary : !alphaUsesSecondary;
			Span<byte> indices = useSecondary ? secondary : primary;
			byte[] weights = useSecondary && secondaryIndexBits == 3 ? Bc7Weights3 : Bc7Weights2;
			int e0 = endpoints[channel * 2];
			int e1 = endpoints[channel * 2 + 1];
			for (int i = 0; i < 16; i++)
				alpha[i] = Bc7Interpolate(e0, e1, weights[indices[i]]);
			return;
		}

		if (mode == 6)
		{
			bits.Skip(42); //RGB endpoints, 6 x 7 bits
			int a0 = bits.Read(7);
			int a1 = bits.Read(7);
			a0 = (a0 << 1) | bits.Read(1);
			a1 = (a1 << 1) | bits.Read(1);
			for (int i = 0; i < 16; i++)
				alpha[i] = Bc7Interpolate(a0, a1, Bc7Weights4[bits.Read(i == 0 ? 3 : 4)]);
			return;
		}

		//Mode 7: two subsets, 5-bit endpoints plus a p-bit each, 2-bit indices
		int partition = bits.Read(6);
		bits.Skip(60); //RGB endpoints, 12 x 5 bits
		Span<int> alphaEndpoints = stackalloc int[4];
		for (int i = 0; i < 4; i++)
			alphaEndpoints[i] = bits.Read(5);
		for (int i = 0; i < 4; i++)
			alphaEndpoints[i] = Bc7Unquantise((alphaEndpoints[i] << 1) | bits.Read(1), 6);

		int mask = Bc7Partition2[partition];
		int anchor = Bc7Anchor2[partition];
		for (int i = 0; i < 16; i++)
		{
			int subset = (mask >> i) & 1;
			int index = bits.Read(i == 0 || i == anchor ? 1 : 2);
			alpha[i] = Bc7Interpolate(alphaEndpoints[subset * 2], alphaEndpoints[subset * 2 + 1], Bc7Weights2[index]);
		}
	}

	private static int Bc7Unquantise(int value, int bits)
	{
		return bits >= 8 ? value : (value << (8 - bits)) | (value >> (2 * bits - 8));
	}

	private static byte Bc7Interpolate(int e0, int e1, int weight)
	{
		return (byte)(((64 - weight) * e0 + weight * e1 + 32) >> 6);
	}

	/// <summary>LSB-first reader over one 128-bit BC7 block.</summary>
	private struct Bc7Bits
	{
		private readonly ulong _low;
		private readonly ulong _high;
		private int _position;

		public Bc7Bits(ulong low, ulong high)
		{
			_low = low;
			_high = high;
			_position = 0;
		}

		public int Read(int count)
		{
			ulong value;
			if (_position >= 64)
				value = _high >> (_position - 64);
			else if (_position + count <= 64)
				value = _low >> _position;
			else
				value = (_low >> _position) | (_high << (64 - _position));
			_position += count;
			return (int)(value & ((1UL << count) - 1));
		}

		public void Skip(int count)
		{
			_position += count;
		}
	}

	private static bool ScanImageForTransparency(Image image)
	{
		if (image == null || image.IsEmpty())
			return false;

		Image.Format format = image.GetFormat();
		if (format == Image.Format.Rgb8 || format == Image.Format.L8 || format == Image.Format.Rf)
			return false;

		if (IsBlockCompressed(format))
			image.Decompress();

		if (image.GetFormat() != Image.Format.Rgba8)
			image.Convert(Image.Format.Rgba8);

		byte[] pixels = image.GetData();
		if (pixels == null || pixels.Length < 4)
			return false;

		int tested = 0;
		int alphaPixels = 0;
		byte minAlpha = byte.MaxValue;

		for (int i = 3; i < pixels.Length; i += 16)
		{
			tested++;
			byte a = pixels[i];
			if (a < minAlpha)
				minAlpha = a;
			if (a < 250)
				alphaPixels++;
		}

		if (tested == 0)
			return false;

		if (minAlpha <= 16)
			return true;

		return alphaPixels * 200 >= tested;
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
