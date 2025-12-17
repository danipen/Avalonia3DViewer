#version 150

out vec4 FragColor;

in VS_OUT {
    vec3 FragPos;
    vec3 Normal;
    vec2 TexCoord;
    mat3 TBN;
} fs_in;

// Material properties
uniform sampler2D albedoMap;
uniform sampler2D normalMap;
uniform sampler2D metallicMap;
uniform sampler2D roughnessMap;
uniform sampler2D aoMap;

uniform vec3 albedo;
uniform float metallic;
uniform float roughness;
uniform float ao;
uniform float opacity;

// Alpha handling:
// 0 = OPAQUE (ignore texture alpha)
// 1 = MASK   (alpha test / cutout, writes depth)
// 2 = BLEND  (standard alpha blending, no depth writes)
uniform int alphaMode;
uniform float alphaCutoff;

uniform bool useAlbedoMap;
uniform bool useNormalMap;
uniform bool useMetallicMap;
uniform bool useRoughnessMap;
uniform bool useAOMap;

// IBL
uniform samplerCube irradianceMap;
uniform samplerCube prefilterMap;
uniform sampler2D brdfLUT;
uniform bool useIBL;
uniform float iblIntensity; // Overall IBL brightness control (default 1.0)

// Lighting
uniform vec3 camPos;
uniform vec3 lightDir;

// Configurable lighting intensities
uniform float mainLightIntensity;
uniform float fillLightIntensity;
uniform float rimLightIntensity;
uniform float topLightIntensity;
uniform float ambientIntensity;

// Shadow mapping
uniform sampler2D shadowMap;
uniform mat4 lightSpaceMatrix;
uniform bool useShadows;
uniform float shadowStrength;

// Shadow-catcher ground:
// When enabled, the material becomes invisible by outputting exactly the background color,
// only darkened by the shadow term. This keeps the ground visually "blended" with the
// background while still receiving shadows.
uniform bool shadowCatcher;
uniform vec3 shadowCatcherBackground; // linear
uniform float shadowCatcherOpacity;   // 0..1

// Debug mode: 0=normal, 1=raw texture, 2=linear albedo, 3=IBL only, 4=pre-tonemap HDR
uniform int debugMode;

// Material override controls
uniform float specularScale;
uniform float roughnessOffset;
uniform float metallicOffset;

const float PI = 3.14159265359;
const float MIN_ROUGHNESS = 0.04; // Prevents specular aliasing
const float MAX_REFLECTION_LOD = 7.0; // Increased for higher quality prefilter

// ============================================================================
// BRDF FUNCTIONS - Physically correct implementations
// ============================================================================

// Normal Distribution Function - GGX/Trowbridge-Reitz
// Using the Disney/Burley squared roughness remapping
float DistributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float NdotH2 = NdotH * NdotH;
    
    float denom = NdotH2 * (a2 - 1.0) + 1.0;
    return a2 / (PI * denom * denom);
}

// Geometry Function - Smith GGX with height-correlated masking-shadowing
// Using the separable form for simplicity, but height-correlated is more accurate
float GeometrySmithGGX(float NdotV, float NdotL, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    
    float GGXV = NdotL * sqrt(NdotV * NdotV * (1.0 - a2) + a2);
    float GGXL = NdotV * sqrt(NdotL * NdotL * (1.0 - a2) + a2);
    
    return 0.5 / max(GGXV + GGXL, 0.0001);
}

// Schlick-GGX for direct lighting (different k than IBL)
float GeometrySchlickGGX_Direct(float NdotX, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return NdotX / (NdotX * (1.0 - k) + k);
}

// Schlick-GGX for IBL
float GeometrySchlickGGX_IBL(float NdotX, float roughness)
{
    float a = roughness * roughness;
    float k = a / 2.0;
    return NdotX / (NdotX * (1.0 - k) + k);
}

float GeometrySmith_Direct(float NdotV, float NdotL, float roughness)
{
    return GeometrySchlickGGX_Direct(NdotV, roughness) * GeometrySchlickGGX_Direct(NdotL, roughness);
}

float GeometrySmith_IBL(float NdotV, float NdotL, float roughness)
{
    return GeometrySchlickGGX_IBL(NdotV, roughness) * GeometrySchlickGGX_IBL(NdotL, roughness);
}

// Fresnel - Schlick approximation with spherical Gaussian approximation for roughness
vec3 fresnelSchlick(float VdotH, vec3 F0)
{
    float Fc = pow(1.0 - VdotH, 5.0);
    return F0 + (1.0 - F0) * Fc;
}

// Fresnel with roughness for IBL (accounts for rough surfaces reflecting less at grazing angles)
vec3 fresnelSchlickRoughness(float NdotV, vec3 F0, float roughness)
{
    float Fc = pow(1.0 - NdotV, 5.0);
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * Fc;
}

// ============================================================================
// NORMAL MAPPING
// ============================================================================

vec3 getNormalFromMap()
{
    vec3 tangentNormal = texture(normalMap, fs_in.TexCoord).xyz;
    // Handle both [0,1] and [-1,1] encoded normals
    tangentNormal = tangentNormal * 2.0 - 1.0;
    // Ensure normal is normalized (fixes issues with compressed textures)
    return normalize(fs_in.TBN * tangentNormal);
}

// ============================================================================
// TONE MAPPING - ACES Fitted (more accurate than simple ACES)
// ============================================================================

// sRGB => XYZ => D65_2_D60 => AP1 => RRT_SAT
const mat3 ACESInputMat = mat3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777
);

// ODT_SAT => XYZ => D60_2_D65 => sRGB
const mat3 ACESOutputMat = mat3(
    1.60475, -0.10208, -0.00327,
    -0.53108, 1.10813, -0.07276,
    -0.07367, -0.00605, 1.07602
);

vec3 RRTAndODTFit(vec3 v)
{
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

vec3 ACESFitted(vec3 color)
{
    color = color * ACESInputMat;
    color = RRTAndODTFit(color);
    color = color * ACESOutputMat;
    return clamp(color, 0.0, 1.0);
}

// ============================================================================
// SHADOW CALCULATION - Improved PCF with Poisson Disk Sampling
// ============================================================================

const vec2 poissonDisk[16] = vec2[](
    vec2(-0.94201624, -0.39906216),
    vec2(0.94558609, -0.76890725),
    vec2(-0.094184101, -0.92938870),
    vec2(0.34495938, 0.29387760),
    vec2(-0.91588581, 0.45771432),
    vec2(-0.81544232, -0.87912464),
    vec2(-0.38277543, 0.27676845),
    vec2(0.97484398, 0.75648379),
    vec2(0.44323325, -0.97511554),
    vec2(0.53742981, -0.47373420),
    vec2(-0.26496911, -0.41893023),
    vec2(0.79197514, 0.19090188),
    vec2(-0.24188840, 0.99706507),
    vec2(-0.81409955, 0.91437590),
    vec2(0.19984126, 0.78641367),
    vec2(0.14383161, -0.14100790)
);

float random(vec3 seed, int i)
{
    vec4 seed4 = vec4(seed, float(i));
    float dot_product = dot(seed4, vec4(12.9898, 78.233, 45.164, 94.673));
    return fract(sin(dot_product) * 43758.5453);
}

float calculateShadow(vec4 fragPosLightSpace, vec3 normal, vec3 lightDirection)
{
    if (!useShadows) return 0.0;
    
    vec3 projCoords = fragPosLightSpace.xyz / fragPosLightSpace.w;
    projCoords = projCoords * 0.5 + 0.5;
    
    if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || 
        projCoords.y < 0.0 || projCoords.y > 1.0)
        return 0.0;
    
    float currentDepth = projCoords.z;
    
    // Improved bias calculation - slope-scaled with min/max bounds
    float cosTheta = max(dot(normal, -lightDirection), 0.0);
    float bias = max(0.005 * (1.0 - cosTheta), 0.001);
    
    // PCSS-style variable penumbra (simplified)
    float shadow = 0.0;
    vec2 texelSize = 1.0 / textureSize(shadowMap, 0);
    float diskRadius = 3.0; // Penumbra size
    
    // Poisson disk sampling for smooth shadows
    for (int i = 0; i < 16; ++i)
    {
        int index = int(16.0 * random(fs_in.FragPos, i)) % 16;
        float pcfDepth = texture(shadowMap, projCoords.xy + poissonDisk[index] * texelSize * diskRadius).r;
        shadow += currentDepth - bias > pcfDepth ? 1.0 : 0.0;
    }
    shadow /= 16.0;
    
    // Soft edge falloff
    float edgeFade = 1.0;
    float fadeMargin = 0.1;
    edgeFade *= smoothstep(0.0, fadeMargin, projCoords.x);
    edgeFade *= smoothstep(0.0, fadeMargin, 1.0 - projCoords.x);
    edgeFade *= smoothstep(0.0, fadeMargin, projCoords.y);
    edgeFade *= smoothstep(0.0, fadeMargin, 1.0 - projCoords.y);
    
    return shadow * edgeFade;
}

// ============================================================================
// DIRECT LIGHTING CALCULATION
// ============================================================================

vec3 calculateDirectionalLight(vec3 L, vec3 N, vec3 V, vec3 F0, vec3 albedoColor, 
                                float metallicValue, float roughnessValue, vec3 lightColor, float intensity)
{
    float NdotL = max(dot(N, L), 0.0);
    if (NdotL <= 0.0) return vec3(0.0);
    
    vec3 H = normalize(V + L);
    float NdotV = max(dot(N, V), 0.001);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    
    // Cook-Torrance BRDF
    float D = DistributionGGX(NdotH, roughnessValue);
    float G = GeometrySmith_Direct(NdotV, NdotL, roughnessValue);
    vec3 F = fresnelSchlick(VdotH, F0);
    
    vec3 numerator = D * G * F;
    float denominator = 4.0 * NdotV * NdotL + 0.0001;
    vec3 specular = numerator / denominator;
    
    // Energy conservation
    vec3 kS = F;
    vec3 kD = (1.0 - kS) * (1.0 - metallicValue);
    
    return (kD * albedoColor / PI + specular) * lightColor * intensity * NdotL;
}

// ============================================================================
// IBL CALCULATION with Multi-scattering Compensation
// ============================================================================

// Debug storage for diffuse component (accessed from main)
vec3 debugDiffuseOnly;

vec3 calculateIBL(vec3 N, vec3 V, vec3 R, vec3 F0, vec3 albedoColor, 
                  float metallicValue, float roughnessValue, float aoValue)
{
    float NdotV = max(dot(N, V), 0.001);
    
    // Fresnel with roughness for IBL
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughnessValue);
    
    // Diffuse IBL - for dielectrics, this is the main color contribution
    vec3 kS = F;
    vec3 kD = (1.0 - kS) * (1.0 - metallicValue);
    vec3 irradiance = texture(irradianceMap, N).rgb;
    
    // Lambertian BRDF is albedo/PI, but the irradiance already has the /PI factor
    // baked in from the hemisphere convolution (see irradiance_convolution.frag line 78)
    // So we just apply: diffuse = kD * irradiance * albedo
    vec3 diffuse = kD * irradiance * albedoColor;
    
    // Store for debug
    debugDiffuseOnly = diffuse * aoValue;
    
    // Specular IBL with proper mip selection
    float mipLevel = roughnessValue * MAX_REFLECTION_LOD;
    vec3 prefilteredColor = textureLod(prefilterMap, R, mipLevel).rgb;
    vec2 brdf = texture(brdfLUT, vec2(NdotV, roughnessValue)).rg;
    
    // Standard split-sum approximation
    // brdf.x = scale (multiplied by F0), brdf.y = bias (added constant)
    vec3 specular = prefilteredColor * (F * brdf.x + brdf.y);
    
    // Apply user-controlled specular scale (default should be 1.0)
    specular *= specularScale;
    
    // Apply overall IBL intensity
    vec3 ambient = (diffuse + specular) * aoValue;
    return ambient * iblIntensity;
}

// ============================================================================
// MAIN
// ============================================================================

void main()
{
    // Shadow catcher early-out (used for the ground plane).
    // Important: we still compute the shadow value so contact shadows appear.
    if (shadowCatcher)
    {
        vec3 N = normalize(fs_in.Normal);
        float shadow = 0.0;
        if (useShadows)
        {
            vec4 fragPosLightSpace = lightSpaceMatrix * vec4(fs_in.FragPos, 1.0);
            shadow = calculateShadow(fragPosLightSpace, N, normalize(lightDir));
        }

        float darken = shadow * shadowStrength * clamp(shadowCatcherOpacity, 0.0, 1.0);
        vec3 color = shadowCatcherBackground * (1.0 - darken);
        FragColor = vec4(color, 1.0);
        return;
    }

    // Sample material properties
    // NOTE: If albedo texture was loaded with sRGB internal format, the GPU already
    // converts to linear space on sampling. If not (Rgba8), we need manual conversion.
    // Currently using sRGB format, so texture() returns linear values directly.
    vec4 albedoTexture = useAlbedoMap ? texture(albedoMap, fs_in.TexCoord) : vec4(albedo, 1.0);
    vec3 albedoSample = albedoTexture.rgb;
    float texAlpha = albedoTexture.a;
    
    float finalAlpha = texAlpha * opacity;
    if (alphaMode == 0) // OPAQUE
    {
        finalAlpha = 1.0;
    }
    else if (alphaMode == 1) // MASK
    {
        if (finalAlpha < alphaCutoff)
            discard;
        finalAlpha = 1.0;
    }
    else // BLEND
    {
        // Avoid doing expensive shading for almost fully transparent pixels
        if (finalAlpha < 0.01)
            discard;
    }
    
    // Debug mode 1: Show raw texture values (what the GPU gives us after sRGB decode)
    if (debugMode == 1) {
        // Convert back to sRGB for display since we're bypassing tone mapping
        FragColor = vec4(pow(albedoSample, vec3(1.0/2.2)), finalAlpha);
        return;
    }
    
    // The albedo is already in linear space due to sRGB texture format
    // No need for manual pow(x, 2.2) conversion
    
    // Debug mode 2: Show albedo in linear space (will look dark on sRGB monitor)
    if (debugMode == 2) {
        FragColor = vec4(albedoSample, finalAlpha);
        return;
    }
    
    float metallicSample = useMetallicMap ? texture(metallicMap, fs_in.TexCoord).r : metallic;
    float roughnessSample = useRoughnessMap ? texture(roughnessMap, fs_in.TexCoord).r : roughness;
    float aoSample = useAOMap ? texture(aoMap, fs_in.TexCoord).r : ao;
    
    // Apply material overrides from UI controls
    metallicSample = clamp(metallicSample + metallicOffset, 0.0, 1.0);
    roughnessSample = clamp(roughnessSample + roughnessOffset, 0.0, 1.0);
    
    // Clamp roughness to prevent specular aliasing
    roughnessSample = clamp(roughnessSample, MIN_ROUGHNESS, 1.0);
    
    // Get surface normal
    vec3 N = useNormalMap ? getNormalFromMap() : normalize(fs_in.Normal);
    vec3 V = normalize(camPos - fs_in.FragPos);
    vec3 R = reflect(-V, N);
    
    // Calculate F0 (reflectance at normal incidence)
    // For dielectrics, use 0.04 (4% reflectance). For metals, use albedo
    vec3 F0 = vec3(0.04);
    F0 = mix(F0, albedoSample, metallicSample);
    
    vec3 color = vec3(0.0);
    
    // Calculate shadow (used in both IBL and non-IBL paths)
    float shadow = 0.0;
    if (useShadows) {
        vec4 fragPosLightSpace = lightSpaceMatrix * vec4(fs_in.FragPos, 1.0);
        shadow = calculateShadow(fragPosLightSpace, N, normalize(lightDir));
    }
    
    if (useIBL) {
        // IBL path with multi-scattering compensation
        vec3 iblColor = calculateIBL(N, V, R, F0, albedoSample, metallicSample, roughnessSample, aoSample);
        
        // Add direct lighting contribution for crisp specular highlights on metals and glass
        // IBL alone provides soft environment reflections but lacks sharp highlights
        // Use dynamic lightDir (camera-relative) for the main light
        vec3 L1 = normalize(lightDir); 
        vec3 directLight1 = calculateDirectionalLight(-L1, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                                       vec3(1.0, 0.98, 0.95), mainLightIntensity * 1.5);
        
        // Secondary fill light from the opposite side to illuminate dark areas
        vec3 L2 = normalize(-lightDir + vec3(0.0, 0.3, 0.0)); // Opposite direction, slightly elevated
        vec3 directLight2 = calculateDirectionalLight(-L2, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                                       vec3(0.7, 0.8, 1.0), fillLightIntensity * 0.8);
        
        // Additional rim/side light for definition
        vec3 L3 = normalize(vec3(0.6, -0.4, 0.7));
        vec3 directLight3 = calculateDirectionalLight(-L3, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                                       vec3(0.8, 0.85, 1.0), fillLightIntensity * 0.5);

        // Rim/back light - for edge definition (now also applied in IBL mode)
        vec3 L4 = normalize(vec3(0.0, 0.3, 1.0));
        vec3 directLight4 = calculateDirectionalLight(-L4, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                                       vec3(1.0, 0.95, 0.9), rimLightIntensity);

        // Top light (now also applied in IBL mode)
        vec3 L5 = normalize(vec3(0.0, -1.0, 0.0));
        vec3 directLight5 = calculateDirectionalLight(-L5, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                                       vec3(0.9, 0.92, 1.0), topLightIntensity);
        
        // Combine IBL with direct lighting
        // Direct lights add crisp highlights, IBL provides ambient fill
        vec3 directContribution = (directLight1 + directLight2 + directLight3 + directLight4 + directLight5) * (1.0 - shadow * shadowStrength);
        
        // Debug mode 3: Show IBL result only (before shadow)
        if (debugMode == 3) {
            FragColor = vec4(iblColor, 1.0);
            return;
        }
        
        // Debug mode 5: Show diffuse only
        if (debugMode == 5) {
            FragColor = vec4(debugDiffuseOnly, 1.0);
            return;
        }

        // Optional artistic ambient lift in IBL mode (controlled by the same Ambient slider as non-IBL).
        // This is intentionally subtle because IBL already provides physically-based ambient.
        vec3 skyColor = vec3(0.25, 0.30, 0.40);
        vec3 groundColor = vec3(0.15, 0.14, 0.13);
        float hemisphereBlend = N.y * 0.5 + 0.5;
        vec3 ambientColor = mix(groundColor, skyColor, hemisphereBlend);
        vec3 extraAmbient = ambientColor * albedoSample * aoSample * ambientIntensity;
        
        // Apply shadow to IBL result - shadows darken the scene realistically
        // We apply a softer shadow to IBL since ambient light would still illuminate shadowed areas
        float shadowFactor = 1.0 - shadow * shadowStrength * 0.5; // Even softer shadows for IBL
        color = (iblColor + extraAmbient) * shadowFactor + directContribution;
    } else {
        // Multi-light setup for non-IBL rendering
        // Shadow already calculated above
        
        // Main light (key) - warm sunlight
        vec3 L1 = normalize(lightDir);
        vec3 mainLight = calculateDirectionalLight(-L1, N, V, F0, albedoSample, metallicSample, roughnessSample, 
                                                    vec3(1.0, 0.98, 0.95), mainLightIntensity);
        color += mainLight * (1.0 - shadow * shadowStrength);
        
        // Back-fill light - from opposite direction to illuminate dark side
        vec3 L2 = normalize(-lightDir + vec3(0.0, 0.3, 0.0));
        color += calculateDirectionalLight(-L2, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                           vec3(0.7, 0.8, 1.0), fillLightIntensity * 0.7);
        
        // Side fill light - cooler sky light
        vec3 L3 = normalize(vec3(0.8, -0.3, 0.5));
        color += calculateDirectionalLight(-L3, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                           vec3(0.75, 0.85, 1.0), fillLightIntensity * 0.5);
        
        // Rim/back light - for edge definition
        vec3 L4 = normalize(vec3(0.0, 0.3, 1.0));
        color += calculateDirectionalLight(-L4, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                           vec3(1.0, 0.95, 0.9), rimLightIntensity);
        
        // Top light
        vec3 L5 = normalize(vec3(0.0, -1.0, 0.0));
        color += calculateDirectionalLight(-L5, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                           vec3(0.9, 0.92, 1.0), topLightIntensity);
        
        // Ground bounce - brightened for better shadow fill
        vec3 L6 = normalize(vec3(0.0, 1.0, 0.0));
        color += calculateDirectionalLight(-L6, N, V, F0, albedoSample, metallicSample, roughnessSample,
                                           vec3(0.6, 0.6, 0.65), fillLightIntensity * 0.4);
        
        // Hemisphere ambient - increased for better shadow visibility
        vec3 skyColor = vec3(0.25, 0.30, 0.40);
        vec3 groundColor = vec3(0.15, 0.14, 0.13);
        float hemisphereBlend = N.y * 0.5 + 0.5;
        vec3 ambientColor = mix(groundColor, skyColor, hemisphereBlend);
        color += ambientColor * albedoSample * aoSample * ambientIntensity * 3.0;
        
        // Approximate environment reflection for non-IBL
        vec3 envReflection = mix(groundColor, skyColor, clamp(R.y * 0.5 + 0.5, 0.0, 1.0));
        float fresnel = pow(1.0 - max(dot(N, V), 0.0), 5.0);
        float smoothness = 1.0 - roughnessSample;
        color += envReflection * fresnel * (1.0 - metallicSample) * smoothness * 0.5;
    }
    
    // Debug mode 4: Show final HDR color before any tone mapping
    if (debugMode == 4) {
        // Clamp to 0-1 just for display
        FragColor = vec4(clamp(color, 0.0, 1.0), finalAlpha);
        return;
    }
    
    // Output HDR (tone mapping done in composite pass)
    // finalAlpha already calculated at top of main() from texture alpha * opacity
    
    // Glass/transparent material handling
    if (finalAlpha < 0.99) {
        float glassReflection = pow(1.0 - max(dot(N, V), 0.0), 3.0);
        color = mix(color, color * 1.5, glassReflection * 0.5);
    }
    
    FragColor = vec4(color, finalAlpha);
}
