using System;
using System.Text.RegularExpressions;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;

namespace Avalonia3DViewer.Rendering;

internal static class ShaderCompat
{
    private static readonly Regex VersionLineRegex = new(@"(?m)^\s*#version\s+.*\r?\n", RegexOptions.Compiled);

    public static bool IsOpenGlesContext(GL gl)
    {
        // On GLES contexts, GL_VERSION typically contains "OpenGL ES".
        // Example: "OpenGL ES 3.0 (ANGLE 2.1.0 ...)"
        try
        {
            unsafe
            {
                var ptr = gl.GetString(StringName.Version);
                var version = SilkMarshal.PtrToString((nint)ptr);
                return version != null && version.IndexOf("OpenGL ES", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
            // If querying fails, assume desktop GL (safer than trying to compile ES shaders on desktop).
            return false;
        }
    }

    public static string BuildSource(
        string rawShaderSource,
        bool isFragment,
        bool isOpenGles,
        (uint location, string name)[]? fragOutputBindings)
    {
        if (rawShaderSource == null)
            throw new ArgumentNullException(nameof(rawShaderSource));

        // Remove any existing #version lines (we own the header).
        string src = VersionLineRegex.Replace(rawShaderSource, "");

        // Avoid accidental BOM/whitespace before #version.
        src = src.TrimStart('\uFEFF');

        string header = isOpenGles
            ? "#version 300 es\n"
            : "#version 150\n";

        if (isOpenGles)
        {
            // GLES requires default float precision in fragment shaders.
            // Adding it to both stages keeps the source consistent and harmless on ES.
            header += "precision mediump float;\n";
            header += "precision mediump sampler2D;\n";
            header += "precision mediump samplerCube;\n";
        }

        string combined = header + "\n" + src;

        if (isOpenGles && isFragment)
            combined = InjectFragmentOutputLocations(combined, fragOutputBindings);

        return combined;
    }

    private static string InjectFragmentOutputLocations(string src, (uint location, string name)[]? fragOutputBindings)
    {
        // GLES 3.0 doesn't support glBindFragDataLocation; use layout qualifiers instead.
        // If the caller provided explicit bindings (e.g., GBuffer MRT), honor them.
        if (fragOutputBindings != null && fragOutputBindings.Length > 0)
        {
            foreach (var (location, name) in fragOutputBindings)
                src = InjectLayoutQualifierForOutVariable(src, name, location);
            return src;
        }

        // Otherwise, for the common single-output case, try to pin FragColor to location 0 if present.
        src = InjectLayoutQualifierForOutVariable(src, "FragColor", 0);
        return src;
    }

    private static string InjectLayoutQualifierForOutVariable(string src, string outVarName, uint location)
    {
        // Replace:
        //   out vec4 FragColor;
        // with:
        //   layout(location = 0) out vec4 FragColor;
        //
        // Works with any type (vec4, float, etc.) and preserves indentation.
        var pattern = $@"(?m)^(?<indent>\s*)out\s+(?<type>[A-Za-z_]\w*)\s+{Regex.Escape(outVarName)}\s*;";
        var regex = new Regex(pattern, RegexOptions.Compiled);

        if (!regex.IsMatch(src))
            return src;

        return regex.Replace(src, m =>
            $"{m.Groups["indent"].Value}layout(location = {location}) out {m.Groups["type"].Value} {outVarName};", 1);
    }
}


