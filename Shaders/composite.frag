#version 330 core

out vec4 FragColor;

in vec2 TexCoord;

uniform sampler2D scene;
uniform sampler2D bloomBlur;
uniform sampler2D ssaoTexture;
uniform float exposure;
uniform bool useBloom;
uniform bool useSSAO;
uniform bool useTonemapping;
uniform float bloomIntensity; // 0.0 - 1.0, default 0.3
uniform float ssaoIntensity;  // 0.0 - 1.0, default 0.5

// ============================================================================
// ACES FITTED TONE MAPPING
// More accurate than the simple approximation, properly handles color
// ============================================================================

// sRGB => XYZ => D65_2_D60 => AP1 => RRT_SAT
// NOTE: GLSL mat3 is column-major. We store matrices in column-major order and
// multiply as (mat3 * vec3) to avoid accidental transposes.
const mat3 ACESInputMat = mat3(
    0.59719, 0.35458, 0.04823,  // Column 0
    0.07600, 0.90834, 0.01566,  // Column 1
    0.02840, 0.13383, 0.83777   // Column 2
);

// ODT_SAT => XYZ => D60_2_D65 => sRGB
const mat3 ACESOutputMat = mat3(
     1.60475, -0.53108, -0.07367,  // Column 0
    -0.10208,  1.10813, -0.00605,  // Column 1
    -0.00327, -0.07276,  1.07602   // Column 2
);

vec3 RRTAndODTFit(vec3 v)
{
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

vec3 ACESFitted(vec3 color)
{
    color = ACESInputMat * color;
    color = RRTAndODTFit(color);
    color = ACESOutputMat * color;
    return clamp(color, 0.0, 1.0);
}

// Alternative: Uncharted 2 / Hable Filmic for comparison
vec3 Uncharted2Tonemap(vec3 x)
{
    float A = 0.15;
    float B = 0.50;
    float C = 0.10;
    float D = 0.20;
    float E = 0.02;
    float F = 0.30;
    return ((x*(A*x+C*B)+D*E)/(x*(A*x+B)+D*F))-E/F;
}

vec3 Uncharted2(vec3 color, float exposure)
{
    float W = 11.2; // Linear white point
    vec3 curr = Uncharted2Tonemap(color * exposure);
    vec3 whiteScale = vec3(1.0) / Uncharted2Tonemap(vec3(W));
    return curr * whiteScale;
}

// ============================================================================
// LINEAR TO SRGB CONVERSION
// More accurate than simple pow(x, 1/2.2)
// ============================================================================

vec3 linearToSRGB(vec3 linear)
{
    vec3 higher = vec3(1.055) * pow(linear, vec3(1.0/2.4)) - vec3(0.055);
    vec3 lower = linear * vec3(12.92);
    return mix(lower, higher, step(vec3(0.0031308), linear));
}

// ============================================================================
// DITHERING
// Reduces banding in gradients
// ============================================================================

float InterleavedGradientNoise(vec2 position)
{
    vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
    return fract(magic.z * fract(dot(position, magic.xy)));
}

vec3 Dither(vec3 color)
{
    float noise = InterleavedGradientNoise(gl_FragCoord.xy);
    // Dither by 1 LSB in 8-bit output
    return color + (noise - 0.5) / 255.0;
}

// ============================================================================
// VIGNETTE (optional, Sketchfab-like subtle darkening at edges)
// ============================================================================

float Vignette(vec2 uv, float intensity)
{
    vec2 centered = uv - 0.5;
    float dist = length(centered);
    float vignette = 1.0 - smoothstep(0.4, 0.8, dist * intensity);
    return vignette;
}

void main()
{
    vec3 hdrColor = texture(scene, TexCoord).rgb;
    
    // Apply SSAO with intensity control
    if (useSSAO) {
        float ao = texture(ssaoTexture, TexCoord).r;
        // Lerp between full AO and no AO based on intensity
        ao = mix(1.0, ao, ssaoIntensity);
        hdrColor *= ao;
    }
    
    // Add bloom with intensity control
    if (useBloom) {
        vec3 bloomColor = texture(bloomBlur, TexCoord).rgb;
        hdrColor += bloomColor * bloomIntensity;
    }
    
    // Apply exposure
    vec3 exposed = hdrColor * exposure;
    
    // Tonemapping and color space conversion
    vec3 srgb;
    if (useTonemapping) {
        // ACES Fitted tone mapping (best quality)
        vec3 tonemapped = ACESFitted(exposed);
        
        // Proper linear to sRGB conversion
        srgb = linearToSRGB(tonemapped);
    } else {
        // Skip tonemapping - just clamp and convert to sRGB
        srgb = linearToSRGB(clamp(exposed, 0.0, 1.0));
    }
    
    // Optional subtle vignette (comment out if not desired)
    // float vignette = Vignette(TexCoord, 1.2);
    // srgb *= mix(1.0, vignette, 0.15);
    
    // Apply dithering to reduce banding
    vec3 dithered = Dither(srgb);
    
    FragColor = vec4(dithered, 1.0);
}
