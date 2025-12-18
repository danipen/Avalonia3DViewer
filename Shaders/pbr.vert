in vec3 aPosition;
in vec3 aNormal;
in vec2 aTexCoord;
in vec3 aTangent;
in vec3 aBitangent;

out VS_OUT {
    vec3 FragPos;
    vec3 Normal;
    vec2 TexCoord;
    mat3 TBN;
} vs_out;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
    vec4 worldPos = uModel * vec4(aPosition, 1.0);
    vs_out.FragPos = worldPos.xyz;
    
    // Transform normal to world space
    mat3 normalMatrix = transpose(inverse(mat3(uModel)));
    vec3 T = normalize(normalMatrix * aTangent);
    vec3 N = normalize(normalMatrix * aNormal);
    T = normalize(T - dot(T, N) * N); // Re-orthogonalize
    vec3 B = cross(N, T);
    
    vs_out.TBN = mat3(T, B, N);
    vs_out.Normal = N;
    vs_out.TexCoord = aTexCoord;
    
    gl_Position = uProjection * uView * worldPos;
}
