out vec4 FragColor;

in vec2 TexCoord;

uniform sampler2D image;
uniform bool horizontal;

void main()
{
    vec2 texelSize = 1.0 / vec2(textureSize(image, 0));

    // 9-tap Gaussian weights (sum ~ 1.0 when doubled symmetrically + center)
    float weights[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);

    vec3 result = texture(image, TexCoord).rgb * weights[0];
    for (int i = 1; i < 5; ++i)
    {
        vec2 offset = (horizontal ? vec2(texelSize.x * float(i), 0.0) : vec2(0.0, texelSize.y * float(i)));
        result += texture(image, TexCoord + offset).rgb * weights[i];
        result += texture(image, TexCoord - offset).rgb * weights[i];
    }

    FragColor = vec4(result, 1.0);
}


