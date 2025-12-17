#version 330 core

out float FragColor;

in vec2 TexCoord;

uniform sampler2D gPosition;    // View-space position
uniform sampler2D gNormal;      // View-space normal
uniform sampler2D texNoise;     // Random rotation vectors

uniform vec3 samples[64];       // Sample kernel
uniform mat4 projection;
uniform vec2 noiseScale;        // ScreenSize / NoiseTextureSize (usually vec2(width/4, height/4))

// SSAO parameters
const int kernelSize = 64;
const float radius = 0.5;
const float bias = 0.025;

void main()
{
    // Get input for SSAO algorithm
    vec3 fragPos = texture(gPosition, TexCoord).xyz;
    vec3 normal = normalize(texture(gNormal, TexCoord).rgb);
    vec3 randomVec = normalize(texture(texNoise, TexCoord * noiseScale).xyz);
    
    // Create TBN matrix to transform sample kernel to view-space
    vec3 tangent = normalize(randomVec - normal * dot(randomVec, normal));
    vec3 bitangent = cross(normal, tangent);
    mat3 TBN = mat3(tangent, bitangent, normal);
    
    // Iterate over sample kernel and calculate occlusion factor
    float occlusion = 0.0;
    for(int i = 0; i < kernelSize; ++i)
    {
        // Get sample position
        vec3 samplePos = TBN * samples[i]; // From tangent to view-space
        samplePos = fragPos + samplePos * radius; 
        
        // Project sample position to screen-space
        vec4 offset = vec4(samplePos, 1.0);
        offset = projection * offset;    // from view to clip-space
        offset.xyz /= offset.w;          // perspective divide
        offset.xyz = offset.xyz * 0.5 + 0.5; // transform to range [0,1]
        
        // Get sample depth
        float sampleDepth = texture(gPosition, offset.xy).z;
        
        // Range check & accumulate
        float rangeCheck = smoothstep(0.0, 1.0, radius / abs(fragPos.z - sampleDepth));
        occlusion += (sampleDepth >= samplePos.z + bias ? 1.0 : 0.0) * rangeCheck;
    }
    
    occlusion = 1.0 - (occlusion / float(kernelSize));
    FragColor = occlusion;
}
