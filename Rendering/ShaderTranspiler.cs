using System;
using System.Collections.Concurrent;
using System.IO;
using Silk.NET.OpenGL;
using Silk.NET.Core.Native;
using SPIRVCross.NET;
using SPIRVCross.NET.GLSL;
using Veldrid;
using Veldrid.SPIRV;

namespace Avalonia3DViewer.Rendering;

internal static class ShaderTranspiler
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static bool IsOpenGLES(GL gl)
    {
        // Works for ANGLE (Windows) and native GLES contexts.
        unsafe
        {
            byte* ptr = gl.GetString(StringName.Version);
            string? version = ptr == null ? null : SilkMarshal.PtrToString((nint)ptr);
            return version != null && version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static string GetSourceForCurrentContext(GL gl, ShaderType stage, string fullPath, string source)
    {
        if (!IsOpenGLES(gl))
            return source;

        // Cache key invalidates when the shader file changes.
        long ticks = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath).Ticks : 0;
        string cacheKey = $"gles3|{stage}|{fullPath}|{ticks}";

        return Cache.GetOrAdd(cacheKey, _ => CrossCompileToEssl300(stage, fullPath, source));
    }

    private static string CrossCompileToEssl300(ShaderType stage, string fullPath, string source)
    {
        // 1) Compile GLSL source -> SPIR-V
        ShaderStages veldridStage = stage switch
        {
            ShaderType.VertexShader => ShaderStages.Vertex,
            ShaderType.FragmentShader => ShaderStages.Fragment,
            _ => throw new NotSupportedException($"Shader stage {stage} is not supported for cross-compilation.")
        };

        SpirvCompilationResult spirv;
        try
        {
            spirv = SpirvCompilation.CompileGlslToSpirv(
                sourceText: source,
                fileName: Path.GetFileName(fullPath),
                stage: veldridStage,
                options: GlslCompileOptions.Default);
        }
        catch (Exception ex)
        {
            // Fallback: return the original source so we at least get a compiler error from GL with useful line numbers.
            Console.WriteLine($"[ShaderTranspiler] Failed GLSL->SPIR-V compile for '{fullPath}': {ex.Message}");
            return source;
        }

        // 2) Cross-compile SPIR-V -> GLSL ES 3.0 (ESSL 300)
        try
        {
            using var ctx = new Context();
            ParsedIR ir = ctx.ParseSpirv(spirv.SpirvBytes);
            GLSLCrossCompiler glsl = ctx.CreateGLSLCompiler(ir);

            // Common options (keep GL-style clip-space; we are not targeting Vulkan conventions here).
            glsl.options.fixupClipSpace = false;
            glsl.options.flipVertexY = false;

            // GLSL ES output options.
            glsl.glslOptions.version = 300;
            glsl.glslOptions.ES = true;
            glsl.glslOptions.vulkanSemantics = false;
            glsl.glslOptions.defaultFloatPrecision = Precision.Highp;
            glsl.glslOptions.defaultIntPrecision = Precision.Highp;

            // Make sure we don't end up with separate image/sampler in ESSL output in edge cases.
            glsl.BuildCombinedImageSamplers();

            return glsl.Compile();
        }
        catch (Exception ex)
        {
            // Fallback: return original source to keep app functional and provide diagnostics.
            Console.WriteLine($"[ShaderTranspiler] Failed SPIR-V->ESSL cross-compile for '{fullPath}': {ex.Message}");
            return source;
        }
    }
}


