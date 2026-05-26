out vec4 FragColor;

in vec2 TexCoord;

uniform sampler2D scene;
uniform float threshold; // typical ~1.0 in HDR space

void main()
{
    vec3 color = texture(scene, TexCoord).rgb;

    // Luminance-based bright-pass (keeps highlights, suppresses midtones).
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));
    float soft = smoothstep(threshold, threshold + 0.5, brightness);

    FragColor = vec4(color * soft, 1.0);
}


