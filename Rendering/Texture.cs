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

        DrainStaleGlErrors(gl);
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
        DrainStaleGlErrors(gl);
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
        // ImageSharp cannot decode Radiance .hdr files; use our own decoder.
        if (Path.GetExtension(path).Equals(".hdr", StringComparison.OrdinalIgnoreCase))
            return LoadRadianceHdr(gl, path);

        var texture = new Texture(gl);

        using var img = Image.Load<RgbaVector>(path);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        DownscaleHdrIfNeeded(img, path);

        DrainStaleGlErrors(gl);
        texture.Bind();
        texture.UploadHdrImage(img);

        SetHdrSamplingParameters(gl);

        return texture;
    }

    private static void SetHdrSamplingParameters(GL gl)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    }

    /// <summary>
    /// Loads a Radiance RGBE (.hdr) image and uploads it as an RGBA16F texture.
    /// Supports both new-style (per-component RLE) and old-style flat scanlines.
    /// </summary>
    private static unsafe Texture LoadRadianceHdr(GL gl, string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path));

        string magic = ReadHdrLine(stream);
        if (!magic.StartsWith("#?"))
            throw new InvalidDataException($"Not a Radiance HDR file: {path}");

        // Header: key=value lines until an empty line.
        float exposure = 1.0f;
        string line;
        while ((line = ReadHdrLine(stream)).Length > 0)
        {
            if (line.StartsWith("FORMAT=") && !line.Contains("32-bit_rle_rgbe"))
                throw new InvalidDataException($"Unsupported HDR format: {line}");

            // Cumulative exposure factor: stored radiance = true radiance * EXPOSURE.
            if (line.StartsWith("EXPOSURE=") &&
                float.TryParse(line.Substring("EXPOSURE=".Length).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float exp) &&
                exp > 0f)
            {
                exposure *= exp;
            }
        }

        // Resolution line, e.g. "-Y 1024 +X 2048" (standard orientation).
        string res = ReadHdrLine(stream);
        var parts = res.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0] != "-Y" || parts[2] != "+X")
            throw new InvalidDataException($"Unsupported HDR orientation: {res}");

        int height = int.Parse(parts[1]);
        int width = int.Parse(parts[3]);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"Invalid HDR dimensions: {width}x{height}");

        // RGBA float pixels (alpha = 1): RGBA16F is the most portable HDR format.
        var pixels = new float[width * height * 4];
        var planes = new byte[4][];
        for (int i = 0; i < 4; i++)
            planes[i] = new byte[width];

        float invExposure = 1.0f / exposure;

        for (int y = 0; y < height; y++)
        {
            ReadRadianceScanline(stream, width, planes);

            // First scanline is the top row (-Y). Flip vertically so v=1 samples
            // the sky, matching the LDR loading path.
            int destRow = height - 1 - y;
            int rowOffset = destRow * width * 4;

            for (int x = 0; x < width; x++)
            {
                byte e = planes[3][x];
                int o = rowOffset + x * 4;
                if (e == 0)
                {
                    pixels[o] = pixels[o + 1] = pixels[o + 2] = 0f;
                }
                else
                {
                    float scale = MathF.Pow(2.0f, e - 136) * invExposure; // ldexp(1, e-128-8)
                    pixels[o] = (planes[0][x] + 0.5f) * scale;
                    pixels[o + 1] = (planes[1][x] + 0.5f) * scale;
                    pixels[o + 2] = (planes[2][x] + 0.5f) * scale;
                }
                pixels[o + 3] = 1f;
            }
        }

        // Keep within sane GPU limits (same 4096 cap as the ImageSharp HDR path).
        const int maxHdrSize = 4096;
        if (width > maxHdrSize || height > maxHdrSize)
        {
            int blockSize = (Math.Max(width, height) + maxHdrSize - 1) / maxHdrSize;
            pixels = BoxDownsampleRgba(pixels, width, height, blockSize, out width, out height);
            Console.WriteLine($"[Texture] Downscaled HDR {path} to {width}x{height}");
        }

        var texture = new Texture(gl);
        DrainStaleGlErrors(gl);
        texture.Bind();
        fixed (float* p = pixels)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba16f,
                (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.Float, p);
        }

        SetHdrSamplingParameters(gl);
        ThrowIfGlError(gl, $"HDR upload failed ({Path.GetFileName(path)})");

        return texture;
    }

    /// <summary>
    /// Box-filter downsample of a tightly packed float RGBA image by an integer factor.
    /// </summary>
    private static float[] BoxDownsampleRgba(float[] src, int width, int height, int blockSize,
        out int newWidth, out int newHeight)
    {
        newWidth = Math.Max(1, width / blockSize);
        newHeight = Math.Max(1, height / blockSize);

        var dst = new float[newWidth * newHeight * 4];

        for (int dy = 0; dy < newHeight; dy++)
        {
            for (int dx = 0; dx < newWidth; dx++)
            {
                float r = 0f, g = 0f, b = 0f;
                int samples = 0;

                int y0 = dy * blockSize;
                int x0 = dx * blockSize;
                int y1 = Math.Min(y0 + blockSize, height);
                int x1 = Math.Min(x0 + blockSize, width);

                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = (sy * width + sx) * 4;
                        r += src[o];
                        g += src[o + 1];
                        b += src[o + 2];
                        samples++;
                    }
                }

                int d = (dy * newWidth + dx) * 4;
                float inv = 1.0f / Math.Max(1, samples);
                dst[d] = r * inv;
                dst[d + 1] = g * inv;
                dst[d + 2] = b * inv;
                dst[d + 3] = 1f;
            }
        }

        return dst;
    }

    private static string ReadHdrLine(Stream stream)
    {
        var sb = new System.Text.StringBuilder();
        int b;
        while ((b = stream.ReadByte()) != -1 && b != '\n')
        {
            if (b != '\r')
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static int ReadByteChecked(Stream stream)
    {
        int b = stream.ReadByte();
        if (b == -1)
            throw new EndOfStreamException("Unexpected end of HDR file");
        return b;
    }

    private static void ReadRadianceScanline(Stream stream, int width, byte[][] planes)
    {
        int b0 = ReadByteChecked(stream);
        int b1 = ReadByteChecked(stream);
        int b2 = ReadByteChecked(stream);
        int b3 = ReadByteChecked(stream);

        bool newRle = b0 == 2 && b1 == 2 && ((b2 << 8) | b3) == width && width >= 8 && width <= 32767;
        if (newRle)
        {
            // New-style: each of the 4 component planes is RLE-encoded separately.
            for (int p = 0; p < 4; p++)
            {
                int x = 0;
                while (x < width)
                {
                    int count = ReadByteChecked(stream);
                    if (count > 128)
                    {
                        // Run of a single value
                        count -= 128;
                        if (count == 0 || x + count > width)
                            throw new InvalidDataException("Corrupt HDR RLE run");
                        byte value = (byte)ReadByteChecked(stream);
                        for (int i = 0; i < count; i++)
                            planes[p][x++] = value;
                    }
                    else
                    {
                        // Literal values
                        if (count == 0 || x + count > width)
                            throw new InvalidDataException("Corrupt HDR RLE literal");
                        for (int i = 0; i < count; i++)
                            planes[p][x++] = (byte)ReadByteChecked(stream);
                    }
                }
            }
            return;
        }

        // Old-style: flat RGBE pixels, with (1,1,1,count) repeat markers.
        int xPos = 0;
        int shift = 0;
        int r = b0, g = b1, bch = b2, e = b3;
        while (true)
        {
            if (r == 1 && g == 1 && bch == 1)
            {
                if (xPos == 0)
                    throw new InvalidDataException("Corrupt HDR old-style RLE (repeat at start of scanline)");

                int repeat = e << shift;
                if (xPos + repeat > width)
                    throw new InvalidDataException("Corrupt HDR old-style RLE (run too long)");

                byte pr = planes[0][xPos - 1];
                byte pg = planes[1][xPos - 1];
                byte pb = planes[2][xPos - 1];
                byte pe = planes[3][xPos - 1];
                for (int i = 0; i < repeat; i++)
                {
                    planes[0][xPos] = pr;
                    planes[1][xPos] = pg;
                    planes[2][xPos] = pb;
                    planes[3][xPos] = pe;
                    xPos++;
                }
                shift += 8;
            }
            else
            {
                planes[0][xPos] = (byte)r;
                planes[1][xPos] = (byte)g;
                planes[2][xPos] = (byte)bch;
                planes[3][xPos] = (byte)e;
                xPos++;
                shift = 0;
            }

            if (xPos >= width)
                break;

            r = ReadByteChecked(stream);
            g = ReadByteChecked(stream);
            bch = ReadByteChecked(stream);
            e = ReadByteChecked(stream);
        }
    }

    public static Texture LoadFromMemory(GL gl, byte[] data, bool srgb = false)
    {
        var texture = new Texture(gl);
        
        using var stream = new MemoryStream(data);
        using var img = Image.Load<Rgba32>(stream);
        DownscaleIfNeeded(img, "memory");

        DrainStaleGlErrors(gl);
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

        // Desktop GL: use SIZED formats. Unsized GL_SRGB_ALPHA is legacy and is
        // rejected by strict core-profile drivers (notably macOS), which makes
        // every sRGB texture fail to upload. GL_SRGB8_ALPHA8 is core since GL 3.0.
        if (!isGles)
            return srgb ? GlSrgb8Alpha8 : (int)InternalFormat.Rgba8;

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
                    // Silk.NET exposes glGetStringi via GetStringS(StringName, uint index).
                    // This path is required for desktop core profiles (e.g. macOS).
                    for (uint i = 0; i < (uint)numExt; i++)
                    {
                        var s = gl.GetStringS(StringName.Extensions, i);
                        if (s != null && s.Equals(ext, StringComparison.OrdinalIgnoreCase))
                            return true;
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

    /// <summary>
    /// Drains any queued GL errors. Call BEFORE an upload so that stale errors
    /// (e.g. left over from the host UI's own GL usage on the shared context)
    /// are not misattributed to the upload.
    /// </summary>
    private static void DrainStaleGlErrors(GL gl)
    {
        for (int i = 0; i < 32 && gl.GetError() != GLEnum.NoError; i++)
        {
        }
    }

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
