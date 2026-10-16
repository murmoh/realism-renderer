namespace Realism.Rendering;

/// <summary>GLSL for the post-processing chain: SSAO, bloom, final tone mapping.</summary>
public static class PostShaders
{
    const string Header = "#version 330 core\n";

    public const string Fullscreen = Header + """
        out vec2 vUv;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vUv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Hemisphere SSAO from the depth buffer: normals are rebuilt from neighbouring depths (choosing the
    /// flatter side so silhouettes don't smear), and samples further than the radius are faded out.
    /// </summary>
    public const string Ssao = Header + """
        in vec2 vUv;
        uniform sampler2D uDepth;
        uniform mat4 uProj;
        uniform mat4 uInvProj;
        uniform vec2 uTexel;
        uniform vec3 uKernel[16];
        out float FragColor;

        vec3 viewPos(vec2 uv) {
            float d = texture(uDepth, uv).r;
            vec4 p = uInvProj * vec4(vec3(uv, d) * 2.0 - 1.0, 1.0);
            return p.xyz / p.w;
        }

        void main() {
            if (texture(uDepth, vUv).r >= 1.0) { FragColor = 1.0; return; }
            vec3 P = viewPos(vUv);
            vec3 r = viewPos(vUv + vec2(uTexel.x, 0.0)) - P, l = P - viewPos(vUv - vec2(uTexel.x, 0.0));
            vec3 t = viewPos(vUv + vec2(0.0, uTexel.y)) - P, b = P - viewPos(vUv - vec2(0.0, uTexel.y));
            vec3 N = normalize(cross(abs(r.z) < abs(l.z) ? r : l, abs(t.z) < abs(b.z) ? t : b));

            float rnd = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
            vec3 rv = vec3(cos(rnd * 6.2831), sin(rnd * 6.2831), 0.0);
            vec3 T = normalize(rv - N * dot(rv, N));
            mat3 TBN = mat3(T, cross(N, T), N);

            float radius = 0.9 + max(-P.z, 0.0) * 0.012;
            float occ = 0.0;
            for (int i = 0; i < 16; i++) {
                vec3 Q = P + TBN * uKernel[i] * radius;
                vec4 clip = uProj * vec4(Q, 1.0);
                vec2 suv = clip.xy / clip.w * 0.5 + 0.5;
                float sceneZ = viewPos(suv).z;
                float range = smoothstep(0.0, 1.0, radius / max(abs(P.z - sceneZ), 1e-4));
                occ += (sceneZ >= Q.z + 0.02 ? 1.0 : 0.0) * range;
            }
            float ao = 1.0 - occ / 16.0;
            FragColor = mix(ao, 1.0, smoothstep(60.0, 140.0, -P.z)); // fade out in the distance
        }
        """;

    /// <summary>
    /// Volumetric sunlight: march from the camera to the scene depth, sampling the shadow cascades.
    /// Lit air scatters sunlight toward the camera (Henyey-Greenstein phase, strongest looking toward the sun);
    /// shadowed air doesn't, which carves visible shafts behind trees and cliffs.
    /// </summary>
    public const string Volumetric = Header + """
        in vec2 vUv;
        uniform sampler2D uDepth;
        uniform sampler2DShadow uShadow;
        uniform sampler2DShadow uShadowNear;
        uniform mat4 uLightViewProj;
        uniform mat4 uLightViewProjNear;
        uniform mat4 uInvViewProj;
        uniform vec3 uCamPos;
        uniform vec3 uSunDir;
        uniform float uMaxDist;
        out float FragColor;

        float shadowAt(vec3 p) {
            vec4 ln = uLightViewProjNear * vec4(p, 1.0);
            vec3 pn = ln.xyz * 0.5 + 0.5;
            if (all(greaterThan(pn.xy, vec2(0.02))) && all(lessThan(pn.xy, vec2(0.98))) && pn.z < 1.0)
                return texture(uShadowNear, vec3(pn.xy, pn.z - 0.001));
            vec4 lf = uLightViewProj * vec4(p, 1.0);
            vec3 pf = lf.xyz * 0.5 + 0.5;
            if (any(lessThan(pf.xy, vec2(0.0))) || any(greaterThan(pf.xy, vec2(1.0))) || pf.z >= 1.0) return 1.0;
            return texture(uShadow, vec3(pf.xy, pf.z - 0.001));
        }

        void main() {
            float d = texture(uDepth, vUv).r;
            vec4 w = uInvViewProj * vec4(vec3(vUv, d) * 2.0 - 1.0, 1.0);
            vec3 ray = w.xyz / w.w - uCamPos;
            float len = min(length(ray), uMaxDist);
            vec3 dir = normalize(ray);

            const int Steps = 40;
            float stepLen = len / float(Steps);
            float dither = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            float scatter = 0.0, trans = 1.0;
            for (int i = 0; i < Steps; i++) {
                vec3 p = uCamPos + dir * ((float(i) + dither) * stepLen);
                float density = 0.006 * exp(-max(p.y - 62.0, 0.0) * 0.03) + 0.0008;
                scatter += shadowAt(p) * density * trans * stepLen;
                trans *= exp(-density * stepLen);
            }
            const float g = 0.6;
            float cosT = dot(dir, uSunDir);
            float phase = (1.0 - g * g) / (12.566 * pow(1.0 + g * g - 2.0 * g * cosT, 1.5));
            FragColor = scatter * (phase * 4.0 + 0.02);
        }
        """;

    /// <summary>
    /// Scene brightness for eye adaptation: log luminance, each output texel averaging a 4x4 grid of the scene
    /// under it. Mipmapped down to one texel, that is the log-average brightness of the whole view.
    /// </summary>
    public const string LogLuminance = Header + """
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform vec2 uCell; // one output texel, in scene uv
        out float FragColor;
        void main() {
            float sum = 0.0;
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) {
                vec3 c = texture(uScene, vUv + (vec2(x, y) - 1.5) / 4.0 * uCell).rgb;
                sum += log(max(dot(c, vec3(0.2126, 0.7152, 0.0722)), 1e-4));
            }
            FragColor = sum / 16.0;
        }
        """;

    /// <summary>Depth-aware 5x5 blur that removes SSAO noise without bleeding across silhouettes.</summary>
    public const string SsaoBlur = Header + """
        in vec2 vUv;
        uniform sampler2D uAo;
        uniform sampler2D uDepth;
        uniform vec2 uTexel;
        out float FragColor;
        float linearDepth(vec2 uv) { float d = texture(uDepth, uv).r; return 1.0 / (1.0 - d + 1e-5); }
        void main() {
            float centre = linearDepth(vUv);
            float sum = 0.0, wsum = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++) {
                vec2 uv = vUv + vec2(x, y) * uTexel;
                float w = 1.0 / (1.0 + abs(linearDepth(uv) - centre) / centre * 40.0);
                sum += texture(uAo, uv).r * w;
                wsum += w;
            }
            FragColor = sum / wsum;
        }
        """;

    /// <summary>13-tap downsample (Call of Duty: AW style). The first pass keeps only light above a soft threshold.</summary>
    public const string BloomDown = Header + """
        in vec2 vUv;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        uniform int uFirst;
        out vec3 FragColor;
        vec3 s(vec2 o) { return texture(uSrc, vUv + o * uTexel).rgb; }
        void main() {
            vec3 a = s(vec2(-2, 2)), b = s(vec2(0, 2)), c = s(vec2(2, 2));
            vec3 d = s(vec2(-2, 0)), e = s(vec2(0, 0)), f = s(vec2(2, 0));
            vec3 g = s(vec2(-2, -2)), h = s(vec2(0, -2)), i = s(vec2(2, -2));
            vec3 j = s(vec2(-1, 1)), k = s(vec2(1, 1)), l = s(vec2(-1, -1)), m = s(vec2(1, -1));
            vec3 col = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;
            if (uFirst == 1) {
                float lum = dot(col, vec3(0.2126, 0.7152, 0.0722));
                const float threshold = 3.5, knee = 1.5;
                float soft = clamp(lum - threshold + knee, 0.0, 2.0 * knee);
                soft = soft * soft / (4.0 * knee + 1e-4);
                float contrib = max(soft, lum - threshold) / max(lum, 1e-4);
                col *= contrib / (1.0 + lum * 0.05); // tame single-pixel fireflies
            }
            FragColor = col;
        }
        """;

    /// <summary>3x3 tent upsample, added onto the next larger mip.</summary>
    public const string BloomUp = Header + """
        in vec2 vUv;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        out vec3 FragColor;
        vec3 s(vec2 o) { return texture(uSrc, vUv + o * uTexel).rgb; }
        void main() {
            FragColor = (s(vec2(-1, 1)) + s(vec2(1, 1)) + s(vec2(-1, -1)) + s(vec2(1, -1))
                       + 2.0 * (s(vec2(0, 1)) + s(vec2(0, -1)) + s(vec2(-1, 0)) + s(vec2(1, 0)))
                       + 4.0 * s(vec2(0, 0))) / 16.0;
        }
        """;

    public const string Composite = Header + """
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uAo;
        uniform sampler2D uBloom;
        uniform sampler2D uVolumetric;
        uniform vec3 uSunColor;
        uniform float uExposure;
        uniform float uBloomStrength;
        uniform float uVolumetricStrength;
        uniform float uNight;
        uniform float uBrightness; // Minecraft's Brightness slider: -0.5 Moody .. 0 default .. +0.5 Bright (and night vision)
        out vec4 FragColor;
        void main() {
            vec3 c = texture(uScene, vUv).rgb;
            c *= mix(1.0, texture(uAo, vUv).r, 0.8);
            c += uSunColor * texture(uVolumetric, vUv).r * uVolumetricStrength;
            c += texture(uBloom, vUv).rgb * uBloomStrength;
            c *= uExposure;
            // Night vision: dim light loses its colour and shifts blue (bright torchlight keeps its warmth).
            float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
            float scotopic = uNight * (1.0 - smoothstep(0.15, 0.8, lum));
            c = mix(c, lum * vec3(0.55, 0.75, 1.2), scotopic * 0.3); // a touch of blue night vision; colours survive
            c = (c * (2.51 * c + 0.03)) / (c * (2.43 * c + 0.59) + 0.14); // ACES fit
            vec2 q = vUv - 0.5;
            c *= 1.0 - 0.22 * pow(dot(q, q) * 2.2, 1.3);                   // lens vignette
            // Brightness, like Minecraft's slider: it lifts the dark parts (caves, shadows, night) and barely touches
            // daylight. Brighter uses Minecraft's own curve 1 - (1 - x)^4; Moody deepens the shadows a little.
            c = clamp(c, 0.0, 1.0);
            if (uBrightness > 0.0) c = mix(c, 1.0 - pow(1.0 - c, vec3(4.0)), min(uBrightness, 1.0) * 0.55);
            else c = mix(c, pow(c, vec3(1.35)), -uBrightness * 1.2);
            FragColor = vec4(pow(clamp(c, 0.0, 1.0), vec3(1.0 / 2.2)), 1.0);
        }
        """;
}
