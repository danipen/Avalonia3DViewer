#version 330 core

out vec4 FragColor;

in vec2 TexCoord;

uniform sampler2D screenTexture;

uniform float fxaaSubpix;           // 0..1
uniform float fxaaEdgeThreshold;    // default ~0.125
uniform float fxaaEdgeThresholdMin; // default ~0.0312
uniform float fxaaSpanMax;          // default ~8-12
uniform float fxaaReduceMul;        // default 1/8
uniform float fxaaReduceMin;        // default 1/128

void main()
{
    vec2 texelSize = 1.0 / textureSize(screenTexture, 0);
    
    vec3 rgbNW = texture(screenTexture, TexCoord + vec2(-1.0, -1.0) * texelSize).rgb;
    vec3 rgbNE = texture(screenTexture, TexCoord + vec2(1.0, -1.0) * texelSize).rgb;
    vec3 rgbSW = texture(screenTexture, TexCoord + vec2(-1.0, 1.0) * texelSize).rgb;
    vec3 rgbSE = texture(screenTexture, TexCoord + vec2(1.0, 1.0) * texelSize).rgb;
    vec3 rgbM  = texture(screenTexture, TexCoord).rgb;
    
    vec3 luma = vec3(0.299, 0.587, 0.114);
    float lumaNW = dot(rgbNW, luma);
    float lumaNE = dot(rgbNE, luma);
    float lumaSW = dot(rgbSW, luma);
    float lumaSE = dot(rgbSE, luma);
    float lumaM  = dot(rgbM, luma);
    
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    // Early exit for low-contrast pixels (reduces unnecessary blur).
    float lumaRange = lumaMax - lumaMin;
    float rangeThreshold = max(fxaaEdgeThresholdMin, lumaMax * fxaaEdgeThreshold);
    if (lumaRange < rangeThreshold)
    {
        FragColor = vec4(rgbM, 1.0);
        return;
    }
    
    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));
    
    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * fxaaReduceMul), fxaaReduceMin);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    
    float spanMax = max(1.0, fxaaSpanMax);
    dir = min(vec2(spanMax, spanMax),
          max(vec2(-spanMax, -spanMax), dir * rcpDirMin)) * texelSize;
    
    vec3 rgbA = 0.5 * (
        texture(screenTexture, TexCoord + dir * (1.0/3.0 - 0.5)).rgb +
        texture(screenTexture, TexCoord + dir * (2.0/3.0 - 0.5)).rgb);
    vec3 rgbB = rgbA * 0.5 + 0.25 * (
        texture(screenTexture, TexCoord + dir * -0.5).rgb +
        texture(screenTexture, TexCoord + dir * 0.5).rgb);
    
    float lumaB = dot(rgbB, luma);
    
    vec3 rgbOut = ((lumaB < lumaMin) || (lumaB > lumaMax)) ? rgbA : rgbB;

    // Subpixel aliasing removal: blend towards the filtered result where local contrast suggests shimmering.
    float lumaAvg = (lumaNW + lumaNE + lumaSW + lumaSE) * 0.25;
    float subpix = clamp(abs(lumaM - lumaAvg) / max(lumaRange, 1e-5), 0.0, 1.0);
    // Smoothstep-like curve to avoid over-blurring mid-tones.
    subpix = subpix * subpix * (3.0 - 2.0 * subpix);
    float subpixBlend = clamp(fxaaSubpix * subpix, 0.0, 1.0);
    rgbOut = mix(rgbM, rgbOut, subpixBlend);

    FragColor = vec4(rgbOut, 1.0);
}
