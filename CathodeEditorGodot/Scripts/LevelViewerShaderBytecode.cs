using System.Collections.Generic;
using System.Threading.Tasks;
using CATHODE;

/// <summary>
/// Drops unused DX11 shader bytecode from CathodeLib after level load (viewer only uses shader metadata).
/// </summary>
public static class LevelViewerShaderBytecode
{
	public static void ClearAsync(IList<Shaders.Shader> shaders)
	{
		if (shaders == null || shaders.Count == 0)
			return;

		Task.Run(() => ClearEntries(shaders));
	}

	private static void ClearEntries(IList<Shaders.Shader> shaders)
	{
		for (int i = 0; i < shaders.Count; i++)
			ClearShader(shaders[i]);
	}

	private static void ClearShader(Shaders.Shader shader)
	{
		if (shader == null)
			return;

		shader.VertexShader = null;
		shader.PixelShader = null;
		shader.HullShader = null;
		shader.DomainShader = null;
		shader.GeometryShader = null;
		shader.ComputeShader = null;
	}
}
