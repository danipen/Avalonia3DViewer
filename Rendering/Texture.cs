using System;
using System.Buffers;
using System.IO;
using System.Text.RegularExpressions;
using Silk.NET.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Avalonia3DViewer.Rendering;

public class Texture : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public TextureTarget Target { get; private set; }
    private bool _disposed;

    public bool HasNonOpaqueAlpha { get; private set; }
    public float PartialAlphaFraction { get; private set; }
    public bool IsMostlyBinaryAlpha { get; private set; }
    
    private const int GlTextureMaxAnisotropyExt = 0x84FE;
    private const int GlMaxTextureMaxAnisotropyExt = 0x84FF;
    public const int MaxTextureSize = 2048;

    // GLES internal formats/constants that are commonly missing/mis-mapped across profiles.
    // - GL_SRGB8_ALPHA8 is required for GLES3 sRGB textures (GL_SRGB_ALPHA is not a valid TexImage2D internalformat in GLES3).
    private const int GlSrgb8Alpha8 = 0x8C43;
    private const int GlSrgbAlpha = 0x8C42;

    private static readonly Regex GlesMajorRegex = new(@"OpenGL\s+ES\s+(?<major>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Texture(GL gl, TextureTarget target = TextureTarget.Texture2D)
    {
        _gl = gl;
        Target = target;
        Handle = _gl.GenTexture();
    }

    public static Texture LoadFromFile(GL gl, string path, bool srgb = false)
    {
        var texture = new Texture(gl);
        
        using var img = Image.Load<Rgba32>(path);
        DownscaleIfNeeded(img, path);

        texture.Bind();
        texture.UploadRgba32Image(img, srgb);
        bool hasMipmaps = TryGenerateMipmaps(gl, img.Width, img.Height);
        ApplyHighQualityFiltering(gl, img.Width, img.Height, hasMipmaps);
        ThrowIfGlError(gl, $"Texture upload failed ({Path.GetFileName(path)})");

        return texture;
    }

    public static Texture CreateFromPixelData(GL gl, byte[] pixelData, int width, int height, bool srgb = false)
    {
        var texture = new Texture(gl);
        texture.Bind();

        AnalyzeAlpha(pixelData, out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
        texture.HasNonOpaqueAlpha = hasAlpha;
        texture.PartialAlphaFraction = partialFrac;
        texture.IsMostlyBinaryAlpha = mostlyBinary;

        int internalFormat = ChooseRgbaInternalFormat(gl, srgb);
        unsafe
        {
            fixed (byte* p = pixelData)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                    (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
        }

        bool hasMipmaps = TryGenerateMipmaps(gl, width, height);
        ApplyHighQualityFiltering(gl, width, height, hasMipmaps);
        ThrowIfGlError(gl, "Texture upload failed (pixel data)");

        return texture;
    }

    public static Texture LoadHDR(GL gl, string path)
    {
        var texture = new Texture(gl);
        
        using var img = Image.Load<RgbaVector>(path);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        DownscaleHdrIfNeeded(img, path);

        texture.Bind();
        texture.UploadHdrImage(img);

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        return texture;
    }

    public static Texture LoadFromMemory(GL gl, byte[] data, bool srgb = false)
    {
        var texture = new Texture(gl);
        
        using var stream = new MemoryStream(data);
        using var img = Image.Load<Rgba32>(stream);
        DownscaleIfNeeded(img, "memory");

        texture.Bind();
        texture.UploadRgba32ImageAsBytes(img, srgb);
        bool hasMipmaps = TryGenerateMipmaps(gl, img.Width, img.Height);
        ApplyHighQualityFiltering(gl, img.Width, img.Height, hasMipmaps);
        ThrowIfGlError(gl, "Texture upload failed (memory)");

        return texture;
    }

    private void UploadRgba32Image(Image<Rgba32> img, bool srgb)
    {
        int pixelCount = img.Width * img.Height;
        Rgba32[] pixels = ArrayPool<Rgba32>.Shared.Rent(pixelCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, pixelCount));

            AnalyzeAlpha(pixels.AsSpan(0, pixelCount), out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
            HasNonOpaqueAlpha = hasAlpha;
            PartialAlphaFraction = partialFrac;
            IsMostlyBinaryAlpha = mostlyBinary;

            unsafe
            {
                fixed (Rgba32* p = pixels)
                {
                    int internalFormat = ChooseRgbaInternalFormat(_gl, srgb);
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
        }
        finally
        {
            ArrayPool<Rgba32>.Shared.Return(pixels);
        }
    }

    private void UploadRgba32ImageAsBytes(Image<Rgba32> img, bool srgb)
    {
        int byteCount = img.Width * img.Height * 4;
        byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, byteCount));

            AnalyzeAlpha(pixels.AsSpan(0, byteCount), out bool hasAlpha, out float partialFrac, out bool mostlyBinary);
            HasNonOpaqueAlpha = hasAlpha;
            PartialAlphaFraction = partialFrac;
            IsMostlyBinaryAlpha = mostlyBinary;

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    int internalFormat = ChooseRgbaInternalFormat(_gl, srgb);
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private void UploadHdrImage(Image<RgbaVector> img)
    {
        int pixelCount = img.Width * img.Height;
        RgbaVector[] pixels = ArrayPool<RgbaVector>.Shared.Rent(pixelCount);
        try
        {
            img.CopyPixelDataTo(pixels.AsSpan(0, pixelCount));

            unsafe
            {
                fixed (RgbaVector* p = pixels)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                        (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.Float, p);
                }
            }
        }
        finally
        {
            ArrayPool<RgbaVector>.Shared.Return(pixels);
        }
    }

    private static void DownscaleIfNeeded(Image<Rgba32> img, string path)
    {
        if (img.Width <= MaxTextureSize && img.Height <= MaxTextureSize) return;

        float scale = Math.Min((float)MaxTextureSize / img.Width, (float)MaxTextureSize / img.Height);
        int newWidth = Math.Max(1, (int)(img.Width * scale));
        int newHeight = Math.Max(1, (int)(img.Height * scale));
        img.Mutate(x => x.Resize(newWidth, newHeight));
        Console.WriteLine($"[Texture] Downscaled {path} to {newWidth}x{newHeight}");
    }

    private static void DownscaleHdrIfNeeded(Image<RgbaVector> img, string path)
    {
        const int MaxHdrSize = 4096;
        if (img.Width <= MaxHdrSize && img.Height <= MaxHdrSize) return;

        float scale = Math.Min((float)MaxHdrSize / img.Width, (float)MaxHdrSize / img.Height);
        int newWidth = Math.Max(1, (int)(img.Width * scale));
        int newHeight = Math.Max(1, (int)(img.Height * scale));
        img.Mutate(x => x.Resize(newWidth, newHeight));
        Console.WriteLine($"[Texture] Downscaled HDR {path} to {newWidth}x{newHeight}");
    }

    private static void ApplyHighQualityFiltering(GL gl, int width, int height, bool hasMipmaps)
    {
        bool isGles = ShaderCompat.IsOpenGlesContext(gl);
        int glesMajor = isGles ? GetGlesMajorVersion(gl) : 0;

        bool isPot = IsPowerOfTwo(width) && IsPowerOfTwo(height);

        // GLES2 NPOT textures are commonly restricted: wrap must be CLAMP_TO_EDGE and mipmaps can't be used.
        // Even on implementations that support NPOT, CLAMP_TO_EDGE is the most compatible default.
        bool allowRepeat = !isGles || glesMajor >= 3 || isPot;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)(allowRepeat ? GLEnum.Repeat : GLEnum.ClampToEdge));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)(allowRepeat ? GLEnum.Repeat : GLEnum.ClampToEdge));

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)(hasMipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        
        // Only apply anisotropy when the extension is present; otherwise this is a common GLES InvalidEnum.
        if (HasExtension(gl, "GL_EXT_texture_filter_anisotropic") || HasExtension(gl, "GL_ARB_texture_filter_anisotropic"))
        {
            float maxAnisotropy = GetMaxAnisotropy(gl);
            gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)GlTextureMaxAnisotropyExt, maxAnisotropy);
        }
    }

    private static float GetMaxAnisotropy(GL gl)
    {
        try
        {
            gl.GetFloat((GetPName)GlMaxTextureMaxAnisotropyExt, out float maxAnisotropy);
            return Math.Clamp(maxAnisotropy, 1.0f, 16.0f);
        }
        catch
        {
            return 16.0f;
        }
    }

    private static bool TryGenerateMipmaps(GL gl, int width, int height)
    {
        bool isGles = ShaderCompat.IsOpenGlesContext(gl);
        int glesMajor = isGles ? GetGlesMajorVersion(gl) : 0;

        // GLES2: avoid mipmaps on NPOT textures to prevent "incomplete texture" (samples as black).
        if (isGles && glesMajor > 0 && glesMajor < 3)
        {
            if (!IsPowerOfTwo(width) || !IsPowerOfTwo(height))
                return false;
        }

        gl.GenerateMipmap(TextureTarget.Texture2D);
        return true;
    }

    private static int ChooseRgbaInternalFormat(GL gl, bool srgb)
    {
        bool isGles = ShaderCompat.IsOpenGlesContext(gl);
        int glesMajor = isGles ? GetGlesMajorVersion(gl) : 0;

        if (!isGles)
            return srgb ? (int)InternalFormat.SrgbAlpha : (int)InternalFormat.Rgba8;

        // GLES3: must use sized sRGB internal format for TexImage2D.
        if (glesMajor >= 3)
            return srgb ? GlSrgb8Alpha8 : (int)InternalFormat.Rgba8;

        // GLES2: internalformat must generally match format (GL_RGBA). If EXT_sRGB exists, GL_SRGB_ALPHA_EXT is allowed.
        if (srgb && HasExtension(gl, "GL_EXT_sRGB"))
            return GlSrgbAlpha;

        return (int)InternalFormat.Rgba;
    }

    private static bool HasExtension(GL gl, string ext)
    {
        try
        {
            // IMPORTANT:
            // - On desktop OpenGL *core profiles*, glGetString(GL_EXTENSIONS) is invalid; must use glGetStringi.
            // - On OpenGL ES, glGetString(GL_EXTENSIONS) is fine.
            bool isGles = ShaderCompat.IsOpenGlesContext(gl);

            if (!isGles)
            {
                // Desktop GL: try glGetStringi path first (core-profile safe).
                // GL_NUM_EXTENSIONS = 0x821D
                const int GlNumExtensions = 0x821D;
                int numExt = 0;
                try
                {
                    numExt = gl.GetInteger((GLEnum)GlNumExtensions);
                }
                catch
                {
                    numExt = 0;
                }

                if (numExt > 0)
                {
                    unsafe
                    {
                        for (uint i = 0; i < (uint)numExt; i++)
                        {
                            var p = gl.GetStringi(StringName.Extensions, i);
                            var s = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)p);
                            if (s != null && s.Equals(ext, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    return false;
                }

                // Fallback (very old compatibility profiles): try glGetString(GL_EXTENSIONS).
            }

            unsafe
            {
                var ptr = gl.GetString(StringName.Extensions);
                if (ptr == null) return false;
                var s = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)ptr);
                return s != null && s.IndexOf(ext, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static int GetGlesMajorVersion(GL gl)
    {
        try
        {
            unsafe
            {
                var ptr = gl.GetString(StringName.Version);
                if (ptr == null) return 0;
                var version = Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)ptr) ?? "";
                var m = GlesMajorRegex.Match(version);
                if (m.Success && int.TryParse(m.Groups["major"].Value, out int major))
                    return major;
            }
        }
        catch { /* ignore */ }
        return 0;
    }

    private static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;

    private static void ThrowIfGlError(GL gl, string message)
    {
        // Capture the *first* error (most useful root cause), but drain them all so later checks are accurate.
        var first = GLEnum.NoError;
        int drained = 0;
        while (drained < 32)
        {
            var err = gl.GetError();
            if (err == GLEnum.NoError)
                break;

            if (first == GLEnum.NoError)
                first = err;
            drained++;
        }

        if (first != GLEnum.NoError)
            throw new InvalidOperationException($"{message}: {first}");
    }

    private static void AnalyzeAlpha(ReadOnlySpan<Rgba32> pixels, out bool hasNonOpaqueAlpha, out float partialAlphaFraction, out bool isMostlyBinaryAlpha)
    {
        int total = pixels.Length;
        if (total <= 0)
        {
            hasNonOpaqueAlpha = false;
            partialAlphaFraction = 0f;
            isMostlyBinaryAlpha = false;
            return;
        }

        int nonOpaque = 0;
        int partial = 0;

        for (int i = 0; i < total; i++)
        {
            byte a = pixels[i].A;
            if (a == 255) continue;
            
            nonOpaque++;
            if (a != 0) partial++;
        }

        hasNonOpaqueAlpha = nonOpaque > 0;
        partialAlphaFraction = (float)partial / total;
        isMostlyBinaryAlpha = hasNonOpaqueAlpha && partialAlphaFraction < 0.05f;
    }

    private static void AnalyzeAlpha(ReadOnlySpan<byte> rgbaPixels, out bool hasNonOpaqueAlpha, out float partialAlphaFraction, out bool isMostlyBinaryAlpha)
    {
        int totalPixels = rgbaPixels.Length / 4;
        if (totalPixels <= 0)
        {
            hasNonOpaqueAlpha = false;
            partialAlphaFraction = 0f;
            isMostlyBinaryAlpha = false;
            return;
        }

        int nonOpaque = 0;
        int partial = 0;

        for (int i = 0; i < rgbaPixels.Length; i += 4)
        {
            byte a = rgbaPixels[i + 3];
            if (a == 255) continue;
            
            nonOpaque++;
            if (a != 0) partial++;
        }

        hasNonOpaqueAlpha = nonOpaque > 0;
        partialAlphaFraction = (float)partial / totalPixels;
        isMostlyBinaryAlpha = hasNonOpaqueAlpha && partialAlphaFraction < 0.05f;
    }

    public void Bind(int unit = 0)
    {
        _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
        _gl.BindTexture(Target, Handle);
    }

    public void Unbind() => _gl.BindTexture(Target, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        if (Handle != 0)
        {
            _gl.DeleteTexture(Handle);
            Handle = 0;
        }
    }
}
