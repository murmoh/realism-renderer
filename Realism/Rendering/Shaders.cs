namespace Realism.Rendering;

/// <summary>GLSL sources. Lighting is done in linear HDR and tone-mapped at the end of each fragment shader.</summary>
public static class Shaders
{
    const string Header = "#version 330 core\n";

    /// <summary>Shared uniforms and functions: noise, sky, fog, shadows, tone mapping.</summary>
    const string Common = """
        uniform vec3 uCamPos;
        uniform vec3 uSunDir;
        uniform vec3 uSunColor;
        uniform float uTime;
        uniform float uFogRange;
        uniform float uExposure;
        uniform float uOvercast;  // 0 clear .. 1 fully overcast
        uniform float uRain;      // 0 dry .. 1 raining (surfaces wet)
        uniform vec3 uTrueSunDir; // the sun itself (uSunDir is the key light: the sun by day, the moon by night)
        uniform float uNight;     // 0 day .. 1 full night
        uniform float uUnderground; // 0 under the sky .. 1 camera inside a cave

        float hash13(vec3 p) {
            p = fract(p * 0.1031);
            p += dot(p, p.zyx + 31.32);
            return fract((p.x + p.y) * p.z);
        }

        float vnoise(vec3 x) {
            vec3 i = floor(x), f = fract(x);
            f = f * f * (3.0 - 2.0 * f);
            return mix(mix(mix(hash13(i), hash13(i + vec3(1,0,0)), f.x),
                           mix(hash13(i + vec3(0,1,0)), hash13(i + vec3(1,1,0)), f.x), f.y),
                       mix(mix(hash13(i + vec3(0,0,1)), hash13(i + vec3(1,0,1)), f.x),
                           mix(hash13(i + vec3(0,1,1)), hash13(i + vec3(1,1,1)), f.x), f.y), f.z);
        }

        float fbm(vec3 p, int octaves) {
            float sum = 0.0, amp = 0.5, norm = 0.0;
            for (int i = 0; i < octaves; i++) {
                sum += amp * vnoise(p);
                norm += amp;
                p = p * 2.03 + vec3(1.7, 9.2, 3.1);
                amp *= 0.5;
            }
            return sum / norm;
        }

        // Analytic sky: horizon/zenith gradient driven by the real sun (day, twilight, night), sunset tint,
        // and a glow around the current key light (sun by day, moon by night).
        vec3 skyColor(vec3 rd) {
            vec3 S = uTrueSunDir;
            vec3 L = uSunDir;
            float day = clamp(S.y * 4.0 + 0.25, 0.0, 1.0);
            float low = (1.0 - smoothstep(0.0, 0.35, S.y)) * smoothstep(-0.12, 0.0, S.y); // sunrise / sunset
            float y = max(rd.y, 0.0);
            vec3 zenith  = mix(vec3(0.0012, 0.0035, 0.016), vec3(0.10, 0.25, 0.65), day);
            vec3 horizon = mix(vec3(0.006, 0.014, 0.040), vec3(0.62, 0.74, 0.90), day);
            float toSun = max(dot(rd, S), 0.0);
            horizon = mix(horizon, vec3(1.0, 0.48, 0.20) * max(day, 0.35), pow(toSun, 4.0) * low * 0.85);
            vec3 col = mix(horizon, zenith, pow(y, 0.42));
            // Overcast: a flat, slightly blue-grey sky with no visible sun.
            vec3 grey = vec3(dot(col, vec3(0.3, 0.5, 0.2))) * vec3(0.9, 0.95, 1.0) * mix(1.0, 0.75, uRain);
            col = mix(col, grey, uOvercast * 0.9);
            float toLight = max(dot(rd, L), 0.0);
            col += uSunColor * (1.0 - uOvercast) * (pow(toLight, 600.0) * 0.6 * (1.0 - uNight) + pow(toLight, 24.0) * 0.06 + pow(toLight, 4.0) * 0.03);
            if (rd.y < 0.0) col = mix(col, horizon * 0.55, smoothstep(0.0, -0.25, rd.y));
            return col;
        }

        // Torches, lanterns and glowing blocks near the camera: warm light with a soft ~14 block reach.
        // w < 0 marks lava: a deeper orange-red glow that reaches further and pulses slowly instead of flickering.
        uniform int uLightCount;
        uniform int uGlintCount;          // glints and haze glow use only the nearest few lights (they are sorted)
        uniform vec4 uLights[32];         // xyz position, w intensity (negative: lava)
        uniform vec4 uLightColors[32];    // this frame's colour (flicker included, computed on the CPU), w reach²
        vec3 pointLights(vec3 p, vec3 N, vec3 albedo) {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < uLightCount; i++) {
                vec3 d = uLights[i].xyz - p;
                float dist2 = max(dot(d, d), 0.05), reach2 = uLightColors[i].w;
                if (dist2 > reach2) continue;
                float atten = 1.0 / (2.5 + dist2) * smoothstep(reach2, reach2 * 0.33, dist2); // soft near the flame, ~1/d² further out
                float wrap = max(dot(N, d * inversesqrt(dist2)), 0.0) * 0.85 + 0.15;
                sum += uLightColors[i].rgb * atten * wrap;
            }
            return albedo * sum;
        }
        // Glints of the lights on glossy surfaces (wet rock, water).
        vec3 pointSpecular(vec3 p, vec3 N, vec3 V, float power) {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < uGlintCount; i++) {
                vec3 d = uLights[i].xyz - p;
                float dist2 = max(dot(d, d), 0.05), reach2 = uLightColors[i].w;
                if (dist2 > reach2) continue;
                vec3 Ld = d * inversesqrt(dist2);
                float atten = 1.0 / (2.5 + dist2) * smoothstep(reach2, reach2 * 0.33, dist2);
                sum += uLightColors[i].rgb * atten * pow(max(dot(N, normalize(Ld + V)), 0.0), power) * max(dot(N, Ld), 0.0);
            }
            return sum * (power + 8.0) / 25.0; // energy-ish normalisation: tighter highlights are brighter
        }
        // How much light the air at p receives from the lights (for haze and dust).
        vec3 caveGlowAt(vec3 p) {
            vec3 sum = vec3(0.0);
            for (int i = 0; i < uGlintCount; i++) {
                vec3 d = uLights[i].xyz - p;
                float dist2 = dot(d, d), reach2 = uLightColors[i].w;
                if (dist2 > reach2) continue;
                sum += uLightColors[i].rgb / (1.0 + dist2 * 0.6) * smoothstep(reach2, reach2 * 0.33, dist2);
            }
            return sum;
        }
        // Light your eyes adjust to in a cave: faint, cool, mostly from below (scattered off the floor and pools).
        vec3 caveFill(vec3 N, vec3 p) {
            // A dim blue ambient (violet toward the floor): the haze carries the mood, not the rock.
            vec3 ambient = mix(vec3(0.020, 0.032, 0.062), vec3(0.034, 0.026, 0.055), smoothstep(-0.2, 0.8, N.y));
            // Around you, a soft neutral light at half strength ~11 blocks away (eyes adjusting): nearby rock keeps its
            // colour and detail, and near-to-far falloff gives the cave depth.
            vec3 d = uCamPos - p;
            float dist2 = dot(d, d);
            float wrap = max(dot(N, d * inversesqrt(max(dist2, 1e-4))), 0.0) * 0.7 + 0.3;
            vec3 nearLight = vec3(0.17, 0.16, 0.15) * wrap / (1.0 + dist2 / 120.0);
            return uUnderground * (ambient + nearLight);
        }

        // Exponential height fog that blends into the sky colour, plus a fade at the edge of the loaded area.
        vec3 applyFog(vec3 col, vec3 p) {
            vec3 d = p - uCamPos;
            float dist = length(d);
            vec3 rd = d / max(dist, 1e-4);
            const float density = 0.0006, falloff = 0.02, seaLevel = 63.0;
            float k = rd.y * falloff;
            float path = abs(k) > 1e-4 ? (1.0 - exp(-dist * k)) / k : dist;
            float weatherFog = 1.0 + uOvercast * 1.5 + uRain * 3.0;
            float fogAmt = 1.0 - exp(-density * weatherFog * exp(-(uCamPos.y - seaLevel) * falloff) * path);
            fogAmt = max(fogAmt, smoothstep(uFogRange * 0.7, uFogRange, length(d.xz)));
            vec3 fogCol = skyColor(normalize(vec3(rd.x, max(rd.y, 0.03), rd.z)));
            // Cave air: a cool, damp haze that swallows the far end of tunnels (warmed near torches and lava).
            // Caves: a glowing blue haze (violet low down) that the distance dissolves into, so nearer rock stands
            // out as dark silhouettes against it.
            vec3 caveHaze = mix(vec3(0.016, 0.012, 0.032), vec3(0.010, 0.017, 0.038), smoothstep(-6.0, 6.0, p.y - uCamPos.y));
            fogCol = mix(fogCol, caveHaze + (uUnderground > 0.01 ? caveGlowAt(p) * 0.03 : vec3(0.0)), uUnderground);
            fogAmt = mix(fogAmt, max(fogAmt, 1.0 - exp(-max(dist - 8.0, 0.0) * 0.015)), uUnderground); // clear close by, layered far away
            // Night: low mist settling in valleys and over water, lit by the moon.
            float mist = uNight * (1.0 - uUnderground) * (1.0 - 0.6 * uRain) * (1.0 - exp(-dist * 0.025))
                       * exp(-max(p.y - 63.0, 0.0) * 0.15) * (0.6 + 0.4 * vnoise(p * 0.05 + vec3(uTime * 0.02, 0.0, 0.0)));
            fogCol = mix(fogCol, skyColor(vec3(rd.x, 0.05, rd.z)) * 1.6, mist / max(fogAmt + mist, 1e-4));
            fogAmt = max(fogAmt, mist * 0.75);
            return mix(col, fogCol, clamp(fogAmt, 0.0, 1.0));
        }

        // Scene shaders write linear HDR; exposure and tone mapping happen in the post-process composite.
        vec3 outputColor(vec3 c) {
            return max(c, 0.0);
        }
        """;

    /// <summary>Two shadow cascades (sharp near the camera, coarse over the whole area) and shared lighting terms.</summary>
    const string ShadowLookup = """
        uniform sampler2DShadow uShadow;      // whole loaded area
        uniform sampler2DShadow uShadowNear;  // high resolution, follows the camera
        uniform mat4 uLightViewProj;
        uniform mat4 uLightViewProjNear;

        float pcf(sampler2DShadow map, vec3 p, float bias, int radius) {
            vec2 texel = 1.0 / vec2(textureSize(map, 0));
            float s = 0.0, n = 0.0;
            for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++) {
                s += texture(map, vec3(p.xy + vec2(x, y) * texel, p.z - bias));
                n += 1.0;
            }
            return s / n;
        }

        float shadowFactor(vec3 worldPos, vec3 n) {
            vec4 lf = uLightViewProj * vec4(worldPos + n * 0.2, 1.0);
            vec3 pf = lf.xyz * 0.5 + 0.5;
            float far = (pf.z >= 1.0 || any(lessThan(pf.xy, vec2(0.0))) || any(greaterThan(pf.xy, vec2(1.0))))
                ? 1.0 : pcf(uShadow, pf, 0.0004, 2);
            vec4 ln = uLightViewProjNear * vec4(worldPos + n * 0.04, 1.0);
            vec3 pn = ln.xyz * 0.5 + 0.5;
            float edge = max(abs(pn.x - 0.5), abs(pn.y - 0.5)) * 2.0;
            if (edge >= 1.0 || pn.z >= 1.0) return mix(far, 1.0, uOvercast * 0.8);
            float near = pcf(uShadowNear, pn, 0.0006, 1);
            return mix(mix(near, far, smoothstep(0.8, 1.0, edge)), 1.0, uOvercast * 0.8); // clouds diffuse the shadows
        }

        // Sky light is mostly desaturated scattered light, not pure zenith blue.
        vec3 ambientLight(vec3 albedo, vec3 N) {
            vec3 skyUp = skyColor(vec3(0, 1, 0));
            vec3 sky = mix(skyUp, vec3(dot(skyUp, vec3(0.3, 0.5, 0.2))), 0.45) * 0.85
                     + skyColor(normalize(vec3(uSunDir.x, 0.2, uSunDir.z))) * 0.2;
            vec3 bounce = albedo * uSunColor * max(uSunDir.y, 0.0) * 0.15;
            return mix(bounce, sky, 0.5 + 0.5 * N.y) * (1.0 - 0.9 * uUnderground); // caves: caveFill takes over
        }

        // Rain-wet surfaces reflect the (usually grey) sky at grazing angles.
        vec3 wetSheen(vec3 N, vec3 V, float wet) {
            float f = 0.02 + 0.98 * pow(1.0 - max(dot(N, V), 0.0), 5.0);
            vec3 R = reflect(-V, N);
            return skyColor(vec3(R.x, abs(R.y), R.z)) * f * wet;
        }

        vec3 grassAlbedo(vec3 p) {
            float patches = fbm(p * 0.045, 4);
            float fine = vnoise(p * 3.7) * 0.6 + vnoise(p * 11.0) * 0.4;
            vec3 lush = vec3(0.060, 0.125, 0.025);
            vec3 meadow = vec3(0.110, 0.165, 0.035);
            vec3 dry = vec3(0.200, 0.190, 0.075);
            vec3 c = mix(lush, meadow, smoothstep(0.35, 0.6, patches));
            c = mix(c, dry, smoothstep(0.58, 0.78, patches));
            return c * (0.65 + 0.6 * fine);
        }
        """;

    /// <summary>
    /// Procedural leaf cluster: jittered small ellipses ("leaves") fading out toward the card edge.
    /// Species (Mat.Oak..PaleOak) set leaf density and shape: broad oak leaves, thin spruce needles, round cherry petals.
    /// </summary>
    const string LeafMask = """
        const float leafCells[10] = float[10](8.0, 10.0, 13.0, 5.5, 10.0, 7.0, 9.0, 9.0, 8.0, 8.0);
        const vec2 leafShape[10] = vec2[10](vec2(0.58, 0.28), vec2(0.50, 0.30), vec2(0.85, 0.06), vec2(0.62, 0.32),
            vec2(0.45, 0.24), vec2(0.60, 0.30), vec2(0.42, 0.38), vec2(0.50, 0.30), vec2(0.60, 0.25), vec2(0.55, 0.30));

        // Conifer sprigs: twigs at random angles, each a fishbone of needles that tapers toward its tip.
        // grow > 1 fattens the foliage (used for shadows, so crowns cast dense shade rather than speckles).
        float sprigMask(vec2 uv, float seed, float grow, out float leafId) {
            vec2 p = uv * 3.2;
            vec2 cell = floor(p);
            float best = 0.0;
            leafId = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) {
                vec2 c = cell + vec2(x, y);
                float h1 = hash13(vec3(c, seed * 91.7));
                float h2 = hash13(vec3(c + 17.0, seed * 37.3));
                vec2 d = p - (c + 0.5 + (vec2(h1, h2) - 0.5) * 0.6);
                float ang = h1 * 6.2831;
                d = mat2(cos(ang), -sin(ang), sin(ang), cos(ang)) * d; // x along the twig, y across
                const float len = 0.8;
                if (abs(d.x) > len) continue;
                float t = abs(d.x) / len;
                float across = abs(d.y);
                if (across > 0.34 * grow * (1.0 - t * t)) continue;
                float needle = abs(fract(d.x * 10.0 + across * 8.0) - 0.5);
                float m = (needle < 0.3 * grow || across < 0.035 || grow > 1.0) ? 1.0 - t * 0.4 : 0.0;
                if (m > best) { best = m; leafId = h2; }
            }
            float r = length(uv - 0.5) * 2.0;
            return best * (1.0 - smoothstep(0.8, 1.0, r));
        }

        float leafMask(vec2 uv, float seed, int species, float grow, out float leafId) {
            if (species == 2) return sprigMask(uv, seed, grow, leafId);
            vec2 p = uv * leafCells[species];
            vec2 cell = floor(p);
            vec2 shape = leafShape[species];
            float best = 0.0;
            leafId = 0.0;
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++) {
                vec2 c = cell + vec2(x, y);
                float h1 = hash13(vec3(c, seed * 91.7));
                float h2 = hash13(vec3(c + 17.0, seed * 37.3));
                vec2 d = p - (c + vec2(h1, h2));
                float ang = h1 * 6.2831;
                d = mat2(cos(ang), -sin(ang), sin(ang), cos(ang)) * d;
                float m = 1.0 - length(d / (shape * grow));
                if (m > best) { best = m; leafId = h2; }
            }
            float r = length(uv - 0.5) * 2.0;
            float reach = species == 2 ? 0.75 : 0.55; // spruce sprigs fill more of the card
            return best * (1.0 - smoothstep(reach, 1.0, r));
        }

        // Leaf colours per species: (shade, sunlit). Cherry is blossom pink, pale oak is grey-green.
        vec3 leafColor(int species, float t) {
            const vec3 dark[10] = vec3[10](vec3(0.030, 0.070, 0.015), vec3(0.060, 0.110, 0.020), vec3(0.012, 0.035, 0.020),
                vec3(0.030, 0.090, 0.015), vec3(0.050, 0.080, 0.020), vec3(0.015, 0.040, 0.010), vec3(0.450, 0.180, 0.300),
                vec3(0.040, 0.090, 0.020), vec3(0.030, 0.070, 0.020), vec3(0.100, 0.110, 0.090));
            const vec3 light[10] = vec3[10](vec3(0.085, 0.120, 0.022), vec3(0.160, 0.200, 0.040), vec3(0.040, 0.080, 0.045),
                vec3(0.080, 0.160, 0.030), vec3(0.110, 0.130, 0.040), vec3(0.040, 0.075, 0.015), vec3(0.750, 0.400, 0.550),
                vec3(0.090, 0.140, 0.030), vec3(0.070, 0.120, 0.030), vec3(0.200, 0.210, 0.180));
            return mix(dark[species], light[species], t);
        }
        """;

    /// <summary>Per-instance orientation and wind sway for leaf cards (vertex stage).</summary>
    const string LeafMaskHeader = """
        mat3 cardRotation(float seed) {
            float a = seed * 6.2831, b = (fract(seed * 13.7) - 0.5) * 1.2;
            mat3 ry = mat3(cos(a), 0.0, -sin(a), 0.0, 1.0, 0.0, sin(a), 0.0, cos(a));
            mat3 rx = mat3(1.0, 0.0, 0.0, 0.0, cos(b), sin(b), 0.0, -sin(b), cos(b));
            return ry * rx;
        }

        float cardSize(int species) {
            return species == 3 ? 2.3 : species == 2 ? 1.7 : species == 1 ? 1.6 : 1.9; // jungle, spruce, birch
        }

        vec3 sway(vec3 p, float t) {
            return vec3(sin(t * 1.3 + p.x * 0.4 + p.y * 0.3), 0.0, cos(t * 1.1 + p.z * 0.4)) * 0.05;
        }
        """;

    public const string WorldVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;
        layout(location = 3) in vec4 aData1;
        uniform mat4 uViewProj;
        uniform int uKind;
        uniform float uTime;
        out vec3 vPos;
        out vec3 vNormal;
        out vec4 vData0;
        out vec4 vData1;
        void main() {
            vec3 p = aPos;
            if (uKind == 1) // leaves sway in the wind
                p += vec3(sin(uTime * 1.3 + p.x * 0.4 + p.y * 0.3), 0.0, cos(uTime * 1.1 + p.z * 0.4)) * 0.05;
            vPos = p;
            vNormal = aNormal;
            vData0 = aData0;
            vData1 = aData1;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    /// <summary>Procedural surface materials shared by terrain and rocks.</summary>
    const string Materials = """
        // Perturb a normal with the gradient of a procedural height function.
        vec3 bump(vec3 n, vec3 p, float freq, float strength) {
            const float e = 0.04;
            float h = fbm(p * freq, 3);
            vec3 g = vec3(fbm((p + vec3(e,0,0)) * freq, 3), fbm((p + vec3(0,e,0)) * freq, 3), fbm((p + vec3(0,0,e)) * freq, 3)) - h;
            g /= e;
            return normalize(n - strength * (g - dot(g, n) * n));
        }

        vec3 dirtAlbedo(vec3 p)   { return vec3(0.16, 0.105, 0.065) * (0.65 + 0.7 * fbm(p * 2.5, 3)); }
        vec3 sandAlbedo(vec3 p)   {
            float ripple = sin(p.x * 2.1 + p.z * 0.9 + fbm(p * 0.5, 2) * 6.0) * 0.5 + 0.5;
            return vec3(0.56, 0.47, 0.32) * (0.88 + 0.12 * ripple + 0.1 * vnoise(p * 9.0));
        }
        vec3 snowAlbedo(vec3 p)   { return vec3(0.86, 0.89, 0.94) * (0.94 + 0.06 * vnoise(p * 3.0)); }
        vec3 gravelAlbedo(vec3 p) { return vec3(0.26, 0.25, 0.24) * (0.55 + 0.9 * vnoise(p * 9.0)); }
        vec3 stoneAlbedo(vec3 p)  {
            float strata = vnoise(vec3(p.x * 0.04, p.y * 0.9, p.z * 0.04));
            float grain = fbm(p * 1.3, 4);
            vec3 c = mix(vec3(0.20, 0.185, 0.165), vec3(0.36, 0.33, 0.29), strata);
            c *= 0.55 + 0.8 * grain;
            c *= 0.75 + 0.25 * smoothstep(0.3, 0.7, vnoise(p * 0.15)); // large-scale weathering
            float lichen = smoothstep(0.62, 0.8, fbm(p * 0.6 + 7.0, 3));
            c = mix(c, vec3(0.16, 0.18, 0.09), lichen * 0.6 * smoothstep(-4.0, 12.0, p.y));
            // Deepslate: cooler and darker deep down, with the odd rust-stained seam through it.
            c = mix(c, c * vec3(0.62, 0.66, 0.74), smoothstep(8.0, -8.0, p.y));
            // Veins are rare: only where a large-scale mask allows, thin and broken up.
            float veinZone = smoothstep(0.62, 0.72, vnoise(p * 0.06 + 21.0));
            float vein = smoothstep(0.018, 0.0, abs(fbm(p * vec3(0.35, 0.7, 0.35) + 3.0, 3) - 0.5)) * veinZone * smoothstep(0.3, 0.6, vnoise(p * 1.5));
            return mix(c, c * vec3(1.5, 0.85, 0.6), vein * 0.6); // iron-stained seams, a shade warmer than the rock
        }
        // Crystal facets: irregular cells (nearest of jittered points), the id of the facet a point lies on.
        vec3 facetId(vec3 p) {
            vec3 c = floor(p), best = c;
            float bd = 9.0;
            for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                vec3 g = c + vec3(x, y, z);
                vec3 o = g + vec3(hash13(g + 0.1), hash13(g + 7.3), hash13(g + 13.7));
                float d = dot(p - o, p - o);
                if (d < bd) { bd = d; best = g; }
            }
            return best;
        }
        // Amethyst (geode walls): crystal facets in deep violet to pale lilac.
        vec3 amethystAlbedo(vec3 p) {
            vec3 cell = facetId(p * 3.0);
            float f = hash13(cell);
            return mix(vec3(0.16, 0.06, 0.30), vec3(0.50, 0.33, 0.72), 0.25 + 0.6 * f) * (0.85 + 0.25 * vnoise(p * 9.0));
        }
        vec3 clayAlbedo(vec3 p) {
            float bands = sin(p.y * 1.7 + fbm(p * 0.2, 2) * 3.0) * 0.5 + 0.5;
            return mix(vec3(0.42, 0.19, 0.10), vec3(0.58, 0.38, 0.24), bands) * (0.8 + 0.3 * vnoise(p * 4.0));
        }
        """;

    public const string WorldFragment = Header + Common + ShadowLookup + LeafMask + Materials + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec4 vData0;
        in vec4 vData1;
        uniform int uKind; // 0 terrain, 1 leaves, 2 bark, 3 buildings
        uniform float uGrassFar;
        out vec4 FragColor;

        void main() {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            float ao = vData1.w;
            vec3 albedo;
            vec3 emissive = vec3(0.0);
            float spec = 0.0;
            float translucency = 0.0;

            float amethyst = 0.0;
            if (uKind == 0) {
                float w[7] = float[7](vData0.x, vData0.y, vData0.z, vData0.w, vData1.x, vData1.y, vData1.z);
                // Under cover (no sky light baked into ao) the snow/ice channel is amethyst: snow never forms there.
                float cov = 1.0 - smoothstep(0.1, 0.3, ao);
                // Break up block-aligned material borders with noise.
                float jitter = fbm(vPos * 0.5, 3) - 0.5;
                for (int i = 0; i < 7; i++) w[i] = max(w[i] + jitter * 0.6 * (i % 2 == 0 ? 1.0 : -1.0) * w[i], 0.0);

                // Steep ground can't hold soil, sand or snow: expose rock and scree on slopes.
                float slope = 1.0 - N.y;
                float rock = smoothstep(0.38, 0.62, slope + jitter * 0.25);
                float loose = (w[0] + w[1] + w[3] + w[4]) * rock;
                w[0] *= 1.0 - rock; w[1] *= 1.0 - rock; w[3] *= 1.0 - rock; w[4] *= 1.0 - rock * 0.8 * (1.0 - cov); // crystal walls stay crystal
                w[2] += loose * 0.8;
                w[5] += loose * 0.2;
                // Grass thins into bare dirt on moderate slopes.
                float thin = smoothstep(0.22, 0.42, slope + jitter * 0.3) * w[0];
                w[0] -= thin; w[1] += thin;

                float total = 0.0;
                for (int i = 0; i < 7; i++) total += w[i];
                for (int i = 0; i < 7; i++) w[i] /= max(total, 1e-4);

                N = bump(N, vPos, 1.3, 0.18 * w[0] + 0.25 * w[1] + 0.7 * w[2] + 0.12 * w[3] + 0.06 * w[4] + 0.5 * w[5] + 0.4 * w[6]);
                amethyst = w[4] * cov;
                // Crystal facets: each tilts its own way, so lights glint off some and not others.
                vec3 facet = amethyst > 0.01 ? facetId(vPos * 3.0) : vec3(0.0);
                N = normalize(N + (vec3(hash13(facet + 1.0), hash13(facet + 2.0), hash13(facet + 3.0)) - 0.5) * 0.9 * amethyst);
                albedo = w[0] * grassAlbedo(vPos) + w[1] * dirtAlbedo(vPos) + w[2] * stoneAlbedo(vPos)
                       + w[3] * sandAlbedo(vPos) + w[4] * mix(snowAlbedo(vPos), amethystAlbedo(vPos), cov) + w[5] * gravelAlbedo(vPos) + w[6] * clayAlbedo(vPos);
                spec = 0.04 * w[2] + 0.25 * w[4] * (1.0 - cov);
                // Where grass blades are drawn, the soil between them is in their shade.
                float grassNear = 1.0 - smoothstep(uGrassFar * 0.5, uGrassFar, distance(uCamPos, vPos));
                albedo *= 1.0 - 0.45 * w[0] * grassNear;
            } else if (uKind == 3) {
                // Buildings: face-aligned UVs (1 unit = 1 block) and a procedural pattern per material.
                int app = int(vData0.x * 255.0 + 0.5);
                vec3 an = abs(N);
                vec2 uv = an.x > 0.5 ? vPos.zy : an.y > 0.5 ? vPos.xz : vPos.xy;
                float groove = 0.0; // 1 inside mortar lines / board seams
                if (app <= 5) {
                    // Wood planks: four boards per block, staggered ends, per-board tint, fine grain.
                    const vec3 wood[6] = vec3[6](vec3(0.33, 0.22, 0.12), vec3(0.20, 0.13, 0.07), vec3(0.11, 0.07, 0.04),
                                                 vec3(0.45, 0.38, 0.25), vec3(0.50, 0.30, 0.30), vec3(0.38, 0.20, 0.10));
                    vec2 q = uv * vec2(1.0, 4.0);
                    float row = floor(q.y);
                    q.x += hash13(vec3(row, 1.0, 7.0)) * 3.0;
                    float board = floor(q.x * 0.5);
                    float tint = hash13(vec3(row, board, 3.0));
                    float grain = vnoise(vec3(q.x * 1.5, fract(q.y) * 18.0, row + board * 13.0));
                    albedo = wood[app] * (0.75 + 0.35 * tint) * (0.8 + 0.35 * grain);
                    groove = max(smoothstep(0.06, 0.0, fract(q.y)), smoothstep(0.02, 0.0, abs(fract(q.x * 0.5) - 0.5) - 0.48));
                } else if (app == 6) {
                    // Cobblestone: rounded stones (cellular) in dark mortar.
                    vec2 p = uv * 3.0;
                    vec2 cell = floor(p);
                    float d1 = 9.0, d2 = 9.0, id = 0.0;
                    for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++) {
                        vec2 c = cell + vec2(x, y);
                        vec2 o = c + vec2(hash13(vec3(c, 1.0)), hash13(vec3(c, 2.0)));
                        float d = length(p - o);
                        if (d < d1) { d2 = d1; d1 = d; id = hash13(vec3(c, 3.0)); } else if (d < d2) d2 = d;
                    }
                    groove = smoothstep(0.12, 0.02, d2 - d1);
                    albedo = vec3(0.30, 0.29, 0.27) * (0.65 + 0.55 * id) * (0.8 + 0.3 * vnoise(vPos * 6.0));
                } else if (app == 7 || app == 8) {
                    // Coursed masonry: stone bricks (2 courses) or clay bricks (4 courses), half-offset rows.
                    float courses = app == 7 ? 2.0 : 4.0;
                    vec2 q = uv * vec2(courses * 0.5, courses);
                    float row = floor(q.y);
                    q.x += mod(row, 2.0) * 0.5;
                    vec2 f = fract(q);
                    float id = hash13(vec3(floor(q), 5.0));
                    groove = max(smoothstep(0.08, 0.0, min(f.y, 1.0 - f.y)), smoothstep(0.05, 0.0, min(f.x, 1.0 - f.x)));
                    albedo = app == 7 ? vec3(0.33, 0.32, 0.30) * (0.8 + 0.3 * id)
                                      : vec3(0.36, 0.13, 0.08) * (0.75 + 0.45 * id);
                    albedo *= 0.85 + 0.3 * vnoise(vPos * 8.0);
                    if (app == 8) albedo = mix(albedo, vec3(0.45, 0.43, 0.40), groove);
                } else if (app == 10) {
                    albedo = vec3(0.45, 0.42, 0.38) * (0.85 + 0.2 * vnoise(vPos * 5.0));
                    spec = 0.6;
                } else if (app >= 38) {
                    // Building log: bark down the sides, growth rings on the cut ends.
                    int sp = app - 38;
                    const vec3 logBark[10] = vec3[10](vec3(0.10, 0.07, 0.045), vec3(0.55, 0.53, 0.48), vec3(0.08, 0.05, 0.035),
                        vec3(0.12, 0.09, 0.05), vec3(0.16, 0.14, 0.12), vec3(0.05, 0.035, 0.02), vec3(0.10, 0.04, 0.04),
                        vec3(0.10, 0.07, 0.045), vec3(0.10, 0.06, 0.04), vec3(0.30, 0.29, 0.27));
                    if (abs(N.y) > 0.5) {
                        float r = length(fract(vPos.xz) - 0.5);
                        float rings = sin(r * 60.0 + vnoise(vPos * 6.0) * 3.0) * 0.5 + 0.5;
                        albedo = mix(vec3(0.42, 0.30, 0.17), vec3(0.30, 0.20, 0.10), rings) * (0.85 + 0.2 * vnoise(vPos * 12.0));
                        groove = smoothstep(0.42, 0.5, r) * 0.8; // bark rim
                    } else {
                        // Vertical bark: deep grooves and fine fibres running up the log, plus pixel grain.
                        float grooves = vnoise(vec3(uv.x * 14.0, uv.y * 1.2, 3.0));
                        float fibres = vnoise(vec3(uv.x * 48.0, uv.y * 3.0, 7.0));
                        albedo = logBark[sp] * (0.45 + 0.9 * smoothstep(0.25, 0.75, grooves)) * (0.8 + 0.35 * fibres);
                        albedo *= 0.85 + 0.3 * hash13(floor(vPos * 16.0 + N * 0.5));
                        groove = (1.0 - smoothstep(0.2, 0.45, grooves)) * 0.6;
                    }
                } else if (app >= 30 && app <= 36) {
                    // Natural material on a crisp block (thin player builds): same look as the smooth terrain.
                    int ch = app - 30;
                    albedo = ch == 0 ? (N.y > 0.5 ? grassAlbedo(vPos) : dirtAlbedo(vPos)) : ch == 1 ? dirtAlbedo(vPos)
                           : ch == 3 ? sandAlbedo(vPos) : ch == 4 ? snowAlbedo(vPos) : ch == 5 ? gravelAlbedo(vPos)
                           : ch == 6 ? clayAlbedo(vPos) : stoneAlbedo(vPos);
                    // Fine per-pixel grain (16 per block, like Minecraft textures): reads as a block surface up close.
                    vec3 cell = floor(vPos * 16.0 + N * 0.5);
                    albedo *= 0.78 + 0.44 * hash13(cell + 17.0);
                    // Slightly darker block edges, as worn natural blocks have.
                    vec2 e = abs(fract(uv) - 0.5);
                    groove = smoothstep(0.47, 0.5, max(e.x, e.y)) * 0.5;
                } else if (app == 37) {
                    // Amethyst crystal: glassy violet, edges lit from within, sharp glints of nearby light.
                    float facet = hash13(floor(vPos * 9.0));
                    albedo = mix(vec3(0.20, 0.07, 0.40), vec3(0.58, 0.40, 0.85), 0.35 + 0.45 * facet);
                    spec = 0.8;
                    emissive = vec3(0.30, 0.10, 0.55) * 0.22 + vec3(0.65, 0.45, 0.95) * pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.35
                             + pointSpecular(vPos, N, V, 90.0) * 1.5;
                } else if (app == 29) {
                    // Lava: molten rock creeping along, a dark cooling crust that cracks into glowing seams.
                    float t = uTime;
                    bool pouring = vData0.w > 0.5 || abs(N.y) < 0.5;
                    vec2 lflow = vData0.yz * 2.0 - 1.0; // which way this lava runs (from Minecraft's levels)
                    float flow, plates;
                    if (pouring) {
                        // A lava fall: molten rock pouring down, stretched into streaks.
                        // A lava fall: thick, slow molten rock - broad glowing folds sliding down, soft darker
                        // patches cooling on them (not thin streaks: lava is far too viscous for that).
                        // Mapped triplanar (along X or Z by facing, blended): seamless around rounded falls.
                        vec2 tw = abs(N.xz) + 1e-3;
                        tw /= tw.x + tw.y;
                        flow = 0.0;
                        plates = 0.0;
                        for (int axis = 0; axis < 2; axis++) {
                            vec2 q = vec2(axis == 0 ? vPos.z : vPos.x, vPos.y + t * 0.6);
                            float fl = fbm(vec3(q.x * 0.7, q.y * 0.45, t * 0.05 + float(axis) * 5.0), 3);
                            float w = axis == 0 ? tw.x : tw.y;
                            flow += fl * w;
                            plates += fbm(vec3(q.x * 1.1, q.y * 0.6 + fl * 1.0, t * 0.08 + float(axis) * 5.0), 3) * w;
                        }
                        plates += 0.08; // mostly molten
                    } else {
                        // Crust plates drift slowly downstream on the flow (two cross-faded copies, as for water).
                        const float LCycle = 6.0;
                        float a0 = fract(t / LCycle), a1 = fract(t / LCycle + 0.5);
                        float lw = abs(a0 * 2.0 - 1.0);
                        vec2 q0 = vPos.xz - lflow * a0 * LCycle * 0.35, q1 = vPos.xz - lflow * a1 * LCycle * 0.35 + vec2(5.1, 2.7);
                        float f0 = fbm(vec3(q0 * 0.5, t * 0.05), 4), f1 = fbm(vec3(q1 * 0.5, t * 0.05), 4);
                        flow = mix(f0, f1, lw);
                        plates = mix(fbm(vec3(q0 * 1.6 + f0 * 2.5, t * 0.08), 4), fbm(vec3(q1 * 1.6 + f1 * 2.5, t * 0.08), 4), lw);
                        plates -= length(lflow) * 0.06; // running lava breaks its crust up: more glowing gaps
                    }
                    float crust = pouring ? smoothstep(0.5, 0.75, plates) * 0.7 : smoothstep(0.44, 0.54, plates);
                    vec3 hot = mix(vec3(0.85, 0.12, 0.01), vec3(1.0, 0.42, 0.06), smoothstep(0.35, 0.75, flow));
                    // Seams between the dark crust plates burn brightest; the crust itself still glows faintly red.
                    float seam = 1.0 - smoothstep(0.0, 0.05, abs(plates - 0.44));
                    albedo = vec3(0.035, 0.025, 0.02);
                    emissive = hot * mix(0.5 + 0.45 * flow, 0.03, crust) + vec3(1.0, 0.45, 0.1) * seam * 0.8;
                    spec = 0.3 * crust;
                } else if (app == 28) {
                    // Light blocks (glowstone, lanterns, torch flames): bright warm cells that bloom.
                    float cells = fbm(vPos * 5.0, 3);
                    albedo = vec3(0.6, 0.45, 0.25);
                    emissive = vec3(1.0, 0.68, 0.32) * (3.0 + 5.0 * cells);
                } else if (app >= 12) {
                    const vec3 dye[16] = vec3[16](vec3(0.80, 0.80, 0.78), vec3(0.80, 0.35, 0.05), vec3(0.65, 0.20, 0.60), vec3(0.25, 0.50, 0.75),
                        vec3(0.85, 0.70, 0.12), vec3(0.40, 0.65, 0.10), vec3(0.85, 0.45, 0.55), vec3(0.50, 0.50, 0.48),
                        vec3(0.22, 0.23, 0.24), vec3(0.08, 0.40, 0.45), vec3(0.35, 0.12, 0.55), vec3(0.15, 0.18, 0.55),
                        vec3(0.35, 0.22, 0.12), vec3(0.25, 0.33, 0.08), vec3(0.55, 0.10, 0.08), vec3(0.04, 0.04, 0.05));
                    albedo = dye[app - 12] * 0.7 * (0.9 + 0.2 * fbm(vPos * 7.0, 3));
                } else {
                    albedo = vec3(0.28, 0.22, 0.16) * (0.8 + 0.3 * fbm(vPos * 4.0, 3));
                }
                // Grooves are recessed: darker, and their edges bend the normal.
                albedo *= 1.0 - 0.45 * groove;
                N = bump(N, vPos, 3.0, 0.12);
            } else if (uKind == 1) {
                // Inner canopy core, mostly hidden by leaf cards: dark, self-shadowed foliage mass.
                float camD = distance(vPos, uCamPos);
                if (camD < 2.2 && hash13(vec3(floor(gl_FragCoord.xy), 0.5)) > smoothstep(0.8, 2.2, camD)) discard;
                int species = int(vData1.x * 255.0 + 0.5);
                N = bump(N, vPos, 1.5, 1.5);
                albedo = leafColor(species, fbm(vPos * 0.12, 3)) * 0.6 * (0.6 + 0.6 * vnoise(vPos * 6.0));
                translucency = 0.3;
            } else {
                // Bark per species: birch is white with dark lenticels, pale oak grey, dark oak near black.
                int species = int(vData1.x * 255.0 + 0.5);
                const vec3 bark[10] = vec3[10](vec3(0.10, 0.07, 0.045), vec3(0.55, 0.53, 0.48), vec3(0.08, 0.05, 0.035),
                    vec3(0.12, 0.09, 0.05), vec3(0.16, 0.14, 0.12), vec3(0.05, 0.035, 0.02), vec3(0.10, 0.04, 0.04),
                    vec3(0.10, 0.07, 0.045), vec3(0.10, 0.06, 0.04), vec3(0.30, 0.29, 0.27));
                float streak = vnoise(vec3(vPos.x * 9.0, vPos.y * 0.7, vPos.z * 9.0));
                albedo = bark[species] * (0.55 + 0.8 * streak);
                if (species == 1) {
                    float marks = smoothstep(0.62, 0.7, vnoise(vec3(vPos.x * 6.0, vPos.y * 9.0, vPos.z * 6.0)));
                    albedo = mix(bark[1] * (0.85 + 0.3 * streak), vec3(0.03, 0.03, 0.03), marks);
                }
                N = bump(N, vec3(vPos.x, vPos.y * 0.15, vPos.z), 6.0, species == 1 ? 0.15 : 0.6);
            }

            // Rain: everything darkens and gains a glossy sheen; flat ground collects puddles that mirror the sky.
            vec3 geomN = normalize(vNormal);
            // Rain only reaches ground under the open sky (sky light is baked into the terrain's ao).
            float rainReach = uKind == 0 ? smoothstep(0.1, 0.3, ao) : 1.0;
            float wet = uRain * rainReach * (uKind == 1 ? 0.3 : 1.0) * smoothstep(-0.2, 0.6, geomN.y);
            float puddle = uKind == 0 ? uRain * rainReach * smoothstep(0.55, 0.68, fbm(vPos * 0.25, 3)) * smoothstep(0.94, 0.985, geomN.y) : 0.0;
            albedo *= 1.0 - 0.4 * wet - 0.3 * puddle;
            N = normalize(mix(N, geomN, puddle));

            float shadow = shadowFactor(vPos, geomN);
            float NdotL = max(dot(N, L), 0.0);
            vec3 ambient = ambientLight(albedo, N) * ao;

            // Covered ground (sky light baked into ao) gets no direct sun either, even where the shadow map leaks.
            // Fully covered ground bakes ao <= 0.1 (0.1 x its own occlusion), so the sun must be gone by then.
            float sunVis = uKind == 0 ? smoothstep(0.11, 0.2, ao) : 1.0;
            vec3 col = albedo * (uSunColor * NdotL * shadow * sunVis + ambient);
            col += albedo * uSunColor * translucency * shadow * sunVis * pow(max(dot(-V, L), 0.0), 3.0);
            vec3 H = normalize(L + V);
            col += uSunColor * spec * pow(max(dot(N, H), 0.0), 40.0) * shadow * sunVis * NdotL;
            col += wetSheen(N, V, wet * 0.3 + puddle * 0.9) * ao;
            col += pointLights(vPos, N, albedo) * (0.5 + 0.5 * ao) + emissive;

            // Caves: covered rock (no sky light baked into ao) is damp. Water seeps down in streaks, so torchlight
            // glints off the walls; crystalline flecks sparkle; faint eye-adjusted fill keeps the shapes readable.
            float covered = uKind == 0 ? 1.0 - smoothstep(0.1, 0.3, ao) : 0.0;
            float occl = uKind == 0 ? clamp(ao * 6.0, 0.25, 1.0) : ao;
            col += albedo * caveFill(N, vPos) * occl;
            if (covered > 0.0) {
                float seep = smoothstep(0.45, 0.8, vnoise(vec3(vPos.x * 2.2, vPos.y * 0.25 - uTime * 0.02, vPos.z * 2.2)));
                float damp = covered * (0.35 + 0.65 * seep);
                col *= 1.0 - 0.25 * damp;
                // Water films smooth the rock: the sheen follows the broad shape, not every bump of the grain.
                vec3 wetN = normalize(mix(geomN, N, 0.3));
                col += pointSpecular(vPos, wetN, V, 40.0) * damp * 0.12;
                float fleck = step(0.9985, hash13(floor(vPos * 22.0)));
                col += pointSpecular(vPos, normalize(N + (vec3(hash13(floor(vPos * 22.0) + 3.0), 0.5, hash13(floor(vPos * 22.0) + 9.0)) - 0.5)), V, 200.0) * fleck * covered * 0.35;
                // Amethyst: sharp glints of the lights off the facets, and a faint violet glow from within.
                col += pointSpecular(vPos, N, V, 120.0) * amethyst * 1.2;
                col += vec3(0.30, 0.10, 0.55) * 0.14 * amethyst * (0.5 + 0.5 * pow(1.0 - max(dot(geomN, V), 0.0), 2.0)) * (0.8 + 0.4 * hash13(facetId(vPos * 3.0)));
                // Glow lichen: tiny blue-green colonies on damp ceilings and walls.
                float colony = smoothstep(0.68, 0.84, fbm(vPos * 0.3 + 11.0, 3)) * smoothstep(0.3, -0.4, N.y);
                float dots = step(0.8, hash13(floor(vPos * 14.0))) * smoothstep(0.35, 0.7, vnoise(vPos * 4.0));
                col += vec3(0.15, 0.55, 1.0) * colony * (dots * 0.6 + 0.04) * covered;
                // Rim light: edges catch the glowing haze, so the rock reads against it.
                col += vec3(0.05, 0.12, 0.35) * pow(1.0 - max(dot(geomN, V), 0.0), 3.0) * uUnderground * occl * 0.25;
            }

            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    public const string WaterFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec4 vData0;
        in vec4 vData1;
        out vec4 FragColor;
        uniform sampler2D uSceneColor;   // opaque scene, resolved just before water is drawn
        uniform sampler2D uSceneDepth;
        uniform vec2 uScreenSize;
        uniform mat4 uViewProj;
        uniform float uNear;
        uniform float uFar;

        float linearDepth(float d) {
            float z = d * 2.0 - 1.0;
            return 2.0 * uNear * uFar / (uFar + uNear - z * (uFar - uNear));
        }

        // Screen-space reflection: march the reflected ray in growing steps, refine the first hit by bisection,
        // fade near screen edges where the information runs out.
        vec3 traceReflection(vec3 P, vec3 R, out float hit) {
            hit = 0.0;
            vec3 pos = P;
            float stepLen = 0.4;
            for (int i = 0; i < 40; i++) {
                vec3 prev = pos;
                pos += R * stepLen;
                stepLen *= 1.12;
                vec4 clip = uViewProj * vec4(pos, 1.0);
                if (clip.w <= 0.1) break;
                vec2 suv = clip.xy / clip.w * 0.5 + 0.5;
                if (any(lessThan(suv, vec2(0.0))) || any(greaterThan(suv, vec2(1.0)))) break;
                float sceneLin = linearDepth(texture(uSceneDepth, suv).r);
                if (clip.w > sceneLin && clip.w - sceneLin < stepLen * 2.0 + 0.5) {
                    vec3 a = prev, b = pos;
                    for (int j = 0; j < 5; j++) {
                        vec3 m = (a + b) * 0.5;
                        vec4 c = uViewProj * vec4(m, 1.0);
                        if (c.w > linearDepth(texture(uSceneDepth, c.xy / c.w * 0.5 + 0.5).r)) b = m; else a = m;
                    }
                    vec4 c = uViewProj * vec4(b, 1.0);
                    suv = c.xy / c.w * 0.5 + 0.5;
                    vec2 fade = smoothstep(0.0, 0.08, suv) * smoothstep(1.0, 0.92, suv);
                    hit = fade.x * fade.y * (1.0 - float(i) / 40.0);
                    return texture(uSceneColor, suv).rgb;
                }
            }
            return vec3(0.0);
        }

        float waves(vec2 q) {
            float t = uTime;
            return fbm(vec3(q * 0.35 + vec2(t * 0.08, t * 0.05), t * 0.15), 4) * 0.7
                 + fbm(vec3(q * 1.6 - vec2(t * 0.15, t * 0.11), t * 0.3), 3) * 0.3;
        }

        void main() {
            float depth = vData0.x * 32.0;
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            // Open sky above this water (0 in caves): there's no sky to reflect and no sun to glint down there.
            float open = vData0.w;
            float shadow = shadowFactor(vPos, normalize(vNormal)) * open;
            vec3 caveAir = vec3(0.010, 0.013, 0.020) + caveGlowAt(vPos + vec3(0.0, 1.5, 0.0)) * 0.03;

            if (vData0.y > 0.5) {
                // Frozen lake: frosted white-blue ice with cracks and a dusting of snow.
                float frost = fbm(vec3(vPos.xz * 0.8, 0.0), 4);
                float cracks = smoothstep(0.02, 0.0, abs(vnoise(vec3(vPos.xz * 1.5, 1.0)) - 0.5));
                vec3 albedo = mix(vec3(0.50, 0.58, 0.63), vec3(0.84, 0.87, 0.90), smoothstep(0.35, 0.7, frost));
                albedo *= 1.0 - cracks * 0.3;
                vec3 Ni = normalize(vec3((frost - 0.5) * 0.15, 1.0, (fbm(vec3(vPos.zx * 0.8, 2.0), 3) - 0.5) * 0.15));
                vec3 skyUp = skyColor(vec3(0, 1, 0));
                vec3 ambientIce = mix(skyUp, vec3(dot(skyUp, vec3(0.3, 0.5, 0.2))), 0.45) * 0.85 * open + caveFill(Ni, vPos) + pointLights(vPos, Ni, vec3(1.0));
                vec3 ice = albedo * (uSunColor * max(dot(Ni, L), 0.0) * shadow + ambientIce);
                vec3 Ri = reflect(-V, Ni);
                ice += uSunColor * pow(max(dot(Ri, L), 0.0), 120.0) * 0.6 * shadow;
                FragColor = vec4(outputColor(applyFog(ice, vPos)), 1.0);
                return;
            }

            vec3 faceN = normalize(vNormal);
            // Steep water (the smooth fluid surface curving over a lip, a falling column, an underside) is falling
            // water; flat water is a surface; in between, the two looks blend.
            float fallW = 1.0 - smoothstep(0.2, 0.5, faceN.y);
            vec3 fall = vec3(0.0);
            if (fallW > 0.001) {
                // Waterfall / stream edge: streaks and foam rushing down over a refracted view of what's behind.
                // Streaks falling straight down, mapped triplanar: along world X on faces looking along Z and along
                // Z on faces looking along X, blended by facing (seamless around rounded columns, stable at any
                // distance). Two sheets at different speeds, wobbling a little; falling columns mostly white water.
                vec2 tw = abs(faceN.xz) + 1e-3;
                tw /= tw.x + tw.y;
                float streak = 0.0;
                for (int axis = 0; axis < 2; axis++) {
                    float a = axis == 0 ? vPos.z : vPos.x;
                    float wobble = sin(vPos.y * 1.3 + uTime * 3.0 + a * 2.0) * 0.08;
                    float slow = fbm(vec3(a * 1.4 + wobble, vPos.y * 0.35 + uTime * 2.4, float(axis) * 7.0), 3);
                    float fast = fbm(vec3(a * 2.8 - wobble, vPos.y * 0.7 + uTime * 4.6, 3.0 + float(axis) * 7.0), 2);
                    streak += mix(slow, fast, 0.35) * (axis == 0 ? tw.x : tw.y);
                }
                float foam = smoothstep(0.42 - 0.12 * vData0.z, 0.8, streak) * 0.8;
                vec3 behind = texture(uSceneColor, gl_FragCoord.xy / uScreenSize + (streak - 0.5) * 0.03).rgb;
                vec3 skyUp = skyColor(vec3(0, 1, 0)) * open;
                vec3 lit = caveFill(faceN, vPos) + pointLights(vPos, faceN, vec3(1.0));
                vec3 tint = vec3(0.02, 0.07, 0.08) * (uSunColor * max(L.y, 0.0) * 0.6 * open + skyUp * 0.7 + lit);
                vec3 foamCol = vec3(0.85, 0.9, 0.92) * (uSunColor * (0.4 + 0.6 * max(dot(faceN, L), 0.0)) * shadow * 0.5 + skyUp * 0.9 + lit * 0.8);
                fall = mix(behind * vec3(0.55, 0.7, 0.72) + tint, foamCol, foam * 0.85);
                if (fallW > 0.999) {
                    FragColor = vec4(outputColor(applyFog(fall, vPos)), 1.0);
                    return;
                }
            }

            vec2 q = vPos.xz;
            const float e = 0.05;
            // Flow map: the ripple pattern is carried downstream along the water's real flow (from Minecraft's levels).
            // Two copies, half a cycle apart, cross-fade so the pattern never stretches: rivers visibly run,
            // still ponds just shimmer. Faster water gets choppier.
            vec2 flow = vData1.xy * 2.0 - 1.0;
            float speed = length(flow);
            const float Cycle = 1.0; // short travel per cycle: where the flow bends, the pattern can't tear apart
            float ph0 = fract(uTime / Cycle), ph1 = fract(uTime / Cycle + 0.5);
            float mixW = abs(ph0 * 2.0 - 1.0);
            vec2 q0 = q - flow * ph0 * Cycle * 1.1, q1 = q - flow * ph1 * Cycle * 1.1 + vec2(3.7, 1.3);
            // No wind in a cave: the surface lies almost still, broken only by drips from the ceiling.
            float waveAmp = 0.35 * mix(0.15, 1.0, open) * (1.0 + 1.6 * speed);
            float h0 = waves(q0), h1 = waves(q1);
            vec3 N0 = vec3(-(waves(q0 + vec2(e, 0)) - h0) / e, 1.0, -(waves(q0 + vec2(0, e)) - h0) / e);
            vec3 N1 = vec3(-(waves(q1 + vec2(e, 0)) - h1) / e, 1.0, -(waves(q1 + vec2(0, e)) - h1) / e);
            vec3 Nw = mix(N0, N1, mixW);
            vec3 N = normalize(vec3(Nw.x * waveAmp, 1.0, Nw.z * waveAmp));
            if (open < 0.9) {
                vec2 cell = floor(q * 0.5);
                vec2 local = fract(q * 0.5) - 0.5 - (vec2(hash13(vec3(cell, 7.0)), hash13(vec3(cell, 8.0))) - 0.5) * 0.5;
                float period = 3.0 + 5.0 * hash13(vec3(cell, 9.0));
                float phase = fract(uTime / period + hash13(vec3(cell, 5.0)));
                float r = length(local);
                float ring = sin((r - phase * 1.2) * 40.0) * smoothstep(0.0, 0.05, phase) * (1.0 - phase) * (1.0 - phase) * smoothstep(0.45, 0.0, r);
                N = normalize(N + vec3(local.x, 0.0, local.y) / max(r, 1e-3) * ring * 0.25 * (1.0 - open));
            }
            // Raindrop ripples: tiny animated rings across the surface.
            if (uRain > 0.0) {
                vec2 cell = floor(q * 2.5);
                vec2 local = fract(q * 2.5) - 0.5;
                float phase = fract(uTime * 1.3 + hash13(vec3(cell, 4.0)));
                float ring = sin((length(local) - phase * 0.5) * 60.0) * (1.0 - phase) * smoothstep(0.5, 0.0, length(local));
                N = normalize(N + vec3(local.x, 0.0, local.y) * ring * 0.8 * uRain);
            }
            vec2 uv = gl_FragCoord.xy / uScreenSize;

            // How much water the view ray crosses before hitting the bottom (from the opaque depth buffer).
            float surfaceLin = linearDepth(gl_FragCoord.z);
            float rayScale = distance(uCamPos, vPos) / max(surfaceLin, 1e-3);
            float bottomLin = linearDepth(texture(uSceneDepth, uv).r);
            float thickness = max(bottomLin - surfaceLin, 0.0) * rayScale;

            // Refraction: offset the lookup by the wave normal, unless that lands on something in front of the water.
            vec2 refrUv = uv + N.xz * 0.035 * clamp(thickness * 0.4, 0.0, 1.0);
            float refrLin = linearDepth(texture(uSceneDepth, refrUv).r);
            if (refrLin < surfaceLin) refrUv = uv;
            else thickness = max(refrLin - surfaceLin, 0.0) * rayScale;

            // Beer-Lambert absorption (red goes first) plus light scattered inside the water.
            vec3 transmittance = exp(-thickness * vec3(0.42, 0.11, 0.075));
            // Torch and lava light shining into a cave pool lights up the water itself, a deep turquoise glow.
            vec3 inner = pointLights(vPos - vec3(0.0, 0.6, 0.0), vec3(0, 1, 0), vec3(1.0)) + caveFill(vec3(0, 1, 0), vPos);
            vec3 scatterCol = vec3(0.012, 0.055, 0.065) * (uSunColor * max(L.y, 0.0) * 0.7 * shadow + skyColor(vec3(0, 1, 0)) * 0.6 * open)
                            + vec3(0.03, 0.16, 0.17) * inner + vec3(0.01, 0.06, 0.16) * (1.0 - open) * uUnderground;
            vec3 below = texture(uSceneColor, refrUv).rgb * transmittance + scatterCol * (1.0 - transmittance.g);

            float fresnel = 0.02 + 0.98 * pow(1.0 - max(dot(N, V), 0.0), 5.0);
            vec3 R = reflect(-V, N);
            R.y = abs(R.y);
            float hit;
            vec3 ssr = traceReflection(vPos, R, hit);
            vec3 reflection = mix(mix(caveAir, skyColor(R), open), ssr, hit);

            // Waterline: blend the very edge so the surface meets the shore without a hard seam.
            float edge = smoothstep(0.0, 0.25, thickness);
            vec3 col = mix(below, reflection, fresnel * edge);
            col += uSunColor * pow(max(dot(R, L), 0.0), 500.0) * 6.0 * shadow * edge;
            col += pointSpecular(vPos, N, V, 400.0) * 0.25 * edge; // torch and lava glints dancing on the ripples
            col += uSunColor * pow(max(dot(R, L), 0.0), 60.0) * 2.5 * uNight * shadow * edge; // a glittering path of moonlight

            // White water where the stream rushes down a cascade.
            float rush = max(vData0.z, smoothstep(0.55, 1.0, speed) * 0.6);
            if (rush > 0.01) {
                // White water rides the current: foam streaks stretched along the flow and carried with it.
                vec2 fd = speed > 0.01 ? flow / speed : vec2(1.0, 0.0);
                mat2 toFlow = mat2(fd.x, -fd.y, fd.y, fd.x);
                vec2 s0 = toFlow * (q0 * 2.5), s1 = toFlow * (q1 * 2.5);
                float churn = mix(fbm(vec3(s0.x * 0.6, s0.y * 1.6, uTime * 0.6), 4), fbm(vec3(s1.x * 0.6, s1.y * 1.6, uTime * 0.6 + 5.0), 4), mixW);
                float foam = smoothstep(0.3, 0.6, churn) * rush;
                vec3 foamCol = vec3(0.85, 0.9, 0.92) * (uSunColor * max(L.y, 0.0) * shadow * 0.5 + skyColor(vec3(0, 1, 0)) * 0.9 * open + inner * 0.8);
                col = mix(col, foamCol, foam * 0.85);
            }
            col = mix(col, fall, fallW);
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    /// <summary>
    /// Cave dust: motes drifting in a box that travels with the camera (wrapped, so they never pop), lit by the
    /// torches and lava around them. Points, no vertex buffer: everything comes from gl_VertexID.
    /// </summary>
    public const string DustVertex = Header + Common + """
        uniform mat4 uViewProj;
        uniform float uViewportHeight;
        uniform int uFireflies; // 1: fireflies over the ground on a clear night instead of cave dust
        uniform sampler2D uGround;   // ground / water surface height around the camera (one texel per block)
        uniform vec2 uGroundOrigin;  // world xz of the height map's corner
        uniform float uGroundSize;   // blocks covered (0: no height map, hover around the camera instead)
        out vec3 vLight;
        out float vFade;
        void main() {
            float id = float(gl_VertexID);
            vec3 seed = vec3(hash13(vec3(id, 1.3, 7.1)), hash13(vec3(id, 5.7, 2.9)), hash13(vec3(id, 9.1, 4.3)));
            if (uFireflies == 1) {
                // Fireflies: hover 0.3..2.5 m above the ground around the camera, drift on slow loops and blink.
                const float FBox = 36.0;
                vec2 wander = vec2(sin(uTime * 0.23 + id * 1.7), cos(uTime * 0.19 + id * 2.3)) * 1.5;
                vec2 xz = uCamPos.xz + mod(seed.xy * FBox + wander - uCamPos.xz + FBox * 0.5, FBox) - FBox * 0.5;
                // Anchored to the ground below them (not the camera): they stay put as you move, jump or bob.
                float ground = uGroundSize > 0.0 ? texture(uGround, (xz - uGroundOrigin) / uGroundSize).r : uCamPos.y - 1.6;
                float y = ground + 0.3 + seed.z * 2.2 + sin(uTime * 0.7 + id) * 0.25;
                vec3 p = vec3(xz.x, y, xz.y);
                float dist = distance(p, uCamPos);
                // Each one glows for about a second, then rests for a few.
                float phase = fract(uTime / (3.0 + 3.0 * seed.x) + seed.y);
                float blink = smoothstep(0.0, 0.08, phase) * smoothstep(0.5, 0.3, phase);
                vFade = blink * smoothstep(FBox * 0.5, FBox * 0.3, dist) * uNight;
                vLight = vec3(0.5, 1.0, 0.15) * 4.0;
                gl_Position = uViewProj * vec4(p, 1.0);
                gl_PointSize = clamp(uViewportHeight * 0.06 / max(gl_Position.w, 0.1), 3.0, 16.0); // core plus soft glow
                return;
            }
            const float Box = 22.0;
            // Slow, lazy drift: each mote wanders on its own looping path and sinks very slowly.
            vec3 drift = vec3(sin(uTime * 0.11 + id), -uTime * 0.04, cos(uTime * 0.09 + id * 1.7)) * (0.6 + seed.yzx);
            vec3 p = uCamPos + mod(seed * Box + drift - uCamPos + Box * 0.5, Box) - Box * 0.5;
            p += vec3(sin(uTime * 0.7 + id * 3.1), sin(uTime * 0.5 + id * 1.3), cos(uTime * 0.6 + id * 2.3)) * 0.08;
            float dist = distance(p, uCamPos);
            vFade = smoothstep(Box * 0.5, Box * 0.3, dist) * smoothstep(0.3, 1.2, dist) * uUnderground;
            vLight = caveGlowAt(p) * 0.6 + vec3(0.03, 0.06, 0.12);
            gl_Position = uViewProj * vec4(p, 1.0);
            gl_PointSize = clamp(uViewportHeight * 0.012 / max(gl_Position.w, 0.1), 1.0, 6.0) * (0.6 + 0.8 * seed.x);
            if (mod(id, 40.0) < 1.0) {
                // Spirit wisps: a few larger glowing blue orbs drifting through the cave, softly pulsing.
                vLight = vec3(0.3, 0.65, 1.0) * (3.0 + 1.5 * sin(uTime * 1.3 + id));
                gl_PointSize = clamp(uViewportHeight * 0.05 / max(gl_Position.w, 0.1), 4.0, 22.0);
            }
        }
        """;

    public const string DustFragment = Header + """
        in vec3 vLight;
        in float vFade;
        out vec4 FragColor;
        void main() {
            float r = length(gl_PointCoord - 0.5) * 2.0;
            float a = (exp(-r * r * 14.0) + 0.25 * exp(-r * r * 3.0)) * smoothstep(1.0, 0.8, r) * vFade; // bright core, soft halo
            if (a < 0.01) discard;
            FragColor = vec4(vLight * a, 1.0); // additive
        }
        """;

    /// <summary>
    /// Water spray and mist where waterfalls land, and embers rising from lava: particles from emitter points
    /// (uEmitters, w &gt; 0 splash strength, w &lt; 0 embers), simulated in the shader from gl_VertexID and time.
    /// Premultiplied alpha: spray covers what is behind it, embers (alpha 0) add light.
    /// </summary>
    public const string ParticleVertex = Header + Common + """
        uniform mat4 uViewProj;
        uniform float uViewportHeight;
        uniform int uEmitterCount;
        uniform vec4 uEmitters[24];
        const int PerEmitter = 48;
        out vec4 vColor;
        void main() {
            int e = gl_VertexID / PerEmitter;
            float id = float(gl_VertexID);
            if (e >= uEmitterCount) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); gl_PointSize = 0.0; vColor = vec4(0.0); return; }
            vec4 em = uEmitters[e];
            vec3 r = vec3(hash13(vec3(id, 1.7, 3.1)), hash13(vec3(id, 5.3, 1.9)), hash13(vec3(id, 2.9, 7.7)));
            vec3 p;
            float size, alpha;
            if (em.w > 0.0) {
                float strength = clamp(sqrt(em.w) * 0.5, 0.6, 2.2);
                bool mist = mod(id, 5.0) < 1.0;
                float life = mist ? 2.5 + r.x : 0.7 + 0.5 * r.x;
                float t = fract(uTime / life + r.y) * life;
                float ang = r.z * 6.2831;
                vec2 dir = vec2(cos(ang), sin(ang));
                if (mist) {
                    // Mist: soft puffs drifting up and outward from the impact.
                    p = em.xyz + vec3(dir.x, 0.0, dir.y) * (0.3 + t * 0.5 * strength) + vec3(0.0, 0.2 + t * 0.45, 0.0);
                    size = (0.6 + t * 0.6) * strength;
                    alpha = 0.12 * smoothstep(0.0, 0.4, t) * (1.0 - t / life);
                } else {
                    // Spray: droplets thrown up and out, falling back under gravity.
                    vec3 v = vec3(dir.x * (0.6 + 1.2 * r.x), (1.8 + 2.4 * r.y) * strength, dir.y * (0.6 + 1.2 * r.x));
                    p = em.xyz + vec3(dir.x, 0.0, dir.y) * 0.35 * r.z + v * t + vec3(0.0, -4.9 * t * t, 0.0);
                    if (p.y < em.y - 0.05) alpha = 0.0; else alpha = 0.75 * (1.0 - t / life);
                    size = 0.05 + 0.05 * r.z;
                }
                vec3 light = uSunColor * max(uSunDir.y, 0.0) * 0.6 + skyColor(vec3(0, 1, 0)) * 0.8 * (1.0 - uUnderground)
                           + caveFill(vec3(0, 1, 0), em.xyz) + pointLights(em.xyz + vec3(0, 1, 0), vec3(0, 1, 0), vec3(1.0));
                vColor = vec4(vec3(0.85, 0.9, 0.95) * light * alpha, alpha);
            } else {
                // Embers: sparks rising from the lava, wavering and cooling from yellow to red as they fade.
                float life = 2.0 + 2.0 * r.x;
                float t = fract(uTime / life + r.y) * life;
                p = em.xyz + vec3((r.z - 0.5) * 1.6, 0.0, (r.x - 0.5) * 1.6)
                  + vec3(sin(t * 3.0 + id) * 0.15 * t, t * (0.6 + 0.6 * r.y), cos(t * 2.3 + id) * 0.15 * t);
                float heat = 1.0 - t / life;
                vec3 c = mix(vec3(1.0, 0.18, 0.02), vec3(1.0, 0.7, 0.25), heat) * (2.0 + 4.0 * heat) * (0.6 + 0.4 * sin(t * 25.0 + id));
                alpha = heat * step(0.55, r.z);
                size = 0.03 + 0.03 * r.x;
                vColor = vec4(c * alpha, 0.0); // additive
            }
            gl_Position = uViewProj * vec4(p, 1.0);
            gl_PointSize = clamp(uViewportHeight * size / max(gl_Position.w, 0.05), 1.0, 96.0);
        }
        """;

    public const string ParticleFragment = Header + """
        in vec4 vColor;
        out vec4 FragColor;
        void main() {
            float r = length(gl_PointCoord - 0.5) * 2.0;
            float a = smoothstep(1.0, 0.0, r);
            FragColor = vColor * a;
        }
        """;

    /// <summary>Window glass: mostly clear, reflective at grazing angles, faint green-blue tint and smudges.</summary>
    public const string GlassFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec4 vData0;
        in vec4 vData1;
        out vec4 FragColor;
        void main() {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            if (dot(N, V) < 0.0) N = -N;
            float fresnel = 0.04 + 0.96 * pow(1.0 - max(dot(N, V), 0.0), 5.0);
            vec3 R = reflect(-V, N);
            float shadow = shadowFactor(vPos, N);
            vec3 col = skyColor(vec3(R.x, abs(R.y), R.z)) * fresnel;
            col += uSunColor * pow(max(dot(R, uSunDir), 0.0), 400.0) * 4.0 * shadow;
            float smudge = fbm(vPos * 3.0, 3);
            col += vec3(0.02, 0.03, 0.03) * smudge;
            float alpha = clamp(0.12 + fresnel * 0.8 + smudge * 0.06, 0.0, 0.95);
            FragColor = vec4(outputColor(applyFog(col / max(alpha, 0.05), vPos)), alpha);
        }
        """;

    /// <summary>
    /// Rain streaks: no vertex buffers. Each instance is one drop at a hashed position inside a box that wraps
    /// around the camera; it falls with a slight wind slant and is drawn as a thin camera-facing quad.
    /// </summary>
    public const string RainVertex = Header + """
        uniform mat4 uViewProj;
        uniform vec3 uCamPos;
        uniform float uTime;
        uniform float uRain;
        out float vAlpha;
        out float vSide;
        float h(float n) { return fract(sin(n) * 43758.5453); }
        void main() {
            float id = float(gl_InstanceID);
            const vec3 box = vec3(40.0, 26.0, 40.0);
            const vec3 dir = normalize(vec3(0.12, -1.0, 0.06));
            float speed = 10.0 + 3.0 * h(id * 1.7);
            vec3 p = vec3(h(id), h(id + 0.37), h(id + 0.71)) * box + dir * uTime * speed;
            p = mod(p - uCamPos + box * 0.5, box) + uCamPos - box * 0.5; // wrap around the camera

            int v = gl_VertexID;
            vec2 q = vec2((v == 1 || v == 2 || v == 4) ? 1.0 : -1.0, (v == 2 || v == 4 || v == 5) ? 1.0 : 0.0);
            vec3 side = normalize(cross(dir, normalize(uCamPos - p))) * 0.01;
            vec3 pos = p + side * q.x + dir * q.y * 0.7;

            float dist = distance(p, uCamPos);
            vAlpha = uRain * 0.28 * smoothstep(0.5, 2.0, dist) * (1.0 - smoothstep(14.0, 20.0, dist));
            vSide = q.x;
            gl_Position = uViewProj * vec4(pos, 1.0);
        }
        """;

    public const string RainFragment = Header + Common + """
        in float vAlpha;
        in float vSide;
        out vec4 FragColor;
        void main() {
            vec3 light = skyColor(vec3(0, 1, 0)) * 0.9 + uSunColor * 0.1;
            FragColor = vec4(light, vAlpha * (1.0 - abs(vSide)));
        }
        """;

    public const string SkyVertex = Header + """
        out vec2 vNdc;
        void main() {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
            vNdc = p;
            gl_Position = vec4(p, 1.0, 1.0);
        }
        """;

    public const string SkyFragment = Header + Common + """
        in vec2 vNdc;
        uniform mat4 uInvViewProj;
        out vec4 FragColor;
        void main() {
            vec4 w = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 rd = normalize(w.xyz / w.w - uCamPos);
            // Below the horizon, use the same haze colour the fog fades terrain into, so the edge of the
            // loaded world dissolves into distance instead of ending at a darker void.
            vec3 col = skyColor(vec3(rd.x, max(rd.y, 0.03), rd.z));
            if (rd.y < 0.03) col = skyColor(normalize(vec3(rd.x, 0.03, rd.z)));

            float cover = 0.0;
            // Cloud layer: fbm projected onto a plane high above the world.
            if (rd.y > 0.0) {
                vec2 uv = rd.xz / rd.y * 0.9 + vec2(uTime * 0.012, uTime * 0.004);
                float c = fbm(vec3(uv, uTime * 0.01), 6);
                cover = smoothstep(0.48 - 0.45 * uOvercast, 0.72 - 0.3 * uOvercast, c) * smoothstep(0.0, 0.18, rd.y);
                float lit = 0.75 + 0.25 * fbm(vec3(uv * 2.0 + uSunDir.xz * 0.05, 3.0), 3);
                vec3 cloud = (uSunColor * 0.35 * lit + skyColor(vec3(0, 1, 0)) * 0.8) * mix(1.0, 0.55 + 0.3 * lit, uOvercast);
                cloud = mix(cloud, uSunColor * vec3(1.0, 0.6, 0.4) * 0.4, (1.0 - smoothstep(0.0, 0.3, uTrueSunDir.y)) * smoothstep(-0.1, 0.02, uTrueSunDir.y) * 0.5);
                // Night: clouds are dark shapes against the stars, edged in silver toward the moon.
                float toMoon = max(dot(rd, uSunDir), 0.0);
                vec3 nightCloud = skyColor(vec3(0, 1, 0)) * (0.5 + 0.3 * lit)
                                + uSunColor * (0.25 + 1.6 * pow(toMoon, 12.0)) * (1.2 - lit) * 0.6;
                cloud = mix(cloud, nightCloud, uNight);
                col = mix(col, cloud, cover * 0.9);
            }

            // Night: stars (hashed direction cells, twinkling), a faint Milky Way band, and the moon.
            if (uNight > 0.0) {
                float clear = uNight * (1.0 - uOvercast);
                if (rd.y > 0.0) {
                    vec3 sd = rd * 420.0;
                    float h = hash13(floor(sd));
                    float core = smoothstep(0.38, 0.0, length(fract(sd) - 0.5));
                    float star = step(0.9972, h) * core * (0.6 + 0.4 * sin(uTime * 2.5 + h * 300.0));
                    // A second, denser layer of faint stars.
                    vec3 sd2 = rd * 900.0;
                    float h2 = hash13(floor(sd2));
                    float faint = step(0.996, h2) * smoothstep(0.4, 0.0, length(fract(sd2) - 0.5)) * 0.35;
                    vec3 starCol = mix(vec3(0.7, 0.8, 1.0), vec3(1.0, 0.85, 0.65), fract(h * 37.0));
                    // The Milky Way: a dusty band of light with dark lanes, bluish with warmer knots.
                    float across = dot(rd, normalize(vec3(0.35, 0.45, 0.82)));
                    float band = exp(-pow(across * 3.0, 2.0));
                    float dust = fbm(rd * 9.0, 5);
                    float lanes = smoothstep(0.35, 0.65, fbm(rd * 16.0 + 4.0, 4));
                    vec3 milky = mix(vec3(0.05, 0.06, 0.11), vec3(0.11, 0.08, 0.07), smoothstep(0.5, 0.8, dust)) * band * dust * (0.45 + 0.55 * lanes);
                    col += (starCol * star * (0.6 + 2.6 * fract(h * 91.0)) + vec3(0.8, 0.85, 1.0) * faint + milky * 1.4)
                         * clear * smoothstep(0.0, 0.2, rd.y) * (1.0 - cover);
                }
                float m = dot(rd, uSunDir); // the key light is the moon at night
                col += vec3(0.30, 0.42, 0.75) * (0.05 * pow(max(m, 0.0), 300.0) + 0.012 * pow(max(m, 0.0), 24.0)) * uNight * (1.0 - uOvercast * 0.7); // moon glow
                if (m > 0.9990) {
                    vec3 up = abs(uSunDir.y) > 0.99 ? vec3(1, 0, 0) : vec3(0, 1, 0);
                    vec3 mx = normalize(cross(up, uSunDir)), my = cross(uSunDir, mx);
                    vec2 q = vec2(dot(rd, mx), dot(rd, my)) / 0.0105;   // moon-disk coordinates, radius 1
                    float r = length(q);
                    float maria = smoothstep(0.45, 0.65, fbm(vec3(q * 1.6, 7.0), 4));
                    float craters = fbm(vec3(q * 6.0, 3.0), 3);
                    float limb = sqrt(max(1.0 - r * r, 0.0));
                    vec3 moon = vec3(0.95, 0.93, 0.88) * (0.75 - 0.3 * maria) * (0.85 + 0.3 * craters) * (0.55 + 0.45 * limb);
                    float disk = smoothstep(1.0, 0.96, r) * uNight * (1.0 - uOvercast * 0.85);
                    col = mix(col, moon * 1.6, disk);
                }
            }
            FragColor = vec4(outputColor(col), 1.0);
        }
        """;

    public const string GrassVertex = Header + """
        layout(location = 0) in vec3 aPos;     // tuft-local; y = 0..1 along the blade, > 1.001 = flower head
        layout(location = 1) in vec3 aNormal;
        layout(location = 4) in vec4 iA;       // root position, seed
        layout(location = 5) in vec4 iB;       // height, kind, colour seed, AO
        uniform mat4 uViewProj;
        uniform vec3 uCamPos;
        uniform float uTime;
        uniform float uGrassFar;
        out vec3 vPos;
        out vec3 vNormal;
        out vec3 vRoot;
        out float vHeight;
        out vec3 vInfo;
        void main() {
            float kind = iB.y;
            bool head = aPos.y > 1.001;
            float a = iA.w * 6.2831;
            mat2 R = mat2(cos(a), sin(a), -sin(a), cos(a));
            float fade = 1.0 - smoothstep(uGrassFar * 0.5, uGrassFar, distance(uCamPos, iA.xyz));
            float h = iB.x * fade;
            if (head && kind != 2.0) h = 0.0;
            float spread = (kind == 1.0 ? 1.4 : kind == 3.0 ? 0.45 : 1.0) * fade;

            vec2 xz = R * aPos.xz * spread;
            float t = head ? 1.0 : aPos.y;
            vec3 p = iA.xyz + vec3(xz.x, head ? h + (aPos.y - 1.0) : aPos.y * h, xz.y);

            // Wind: rolling gusts across the field, bending blades more toward the tip.
            float gust = sin(uTime * 1.4 + iA.x * 0.21 + iA.z * 0.13) * 0.5 + 0.5;
            float flutter = sin(uTime * 4.1 + iA.x * 1.7 + iA.z * 2.3 + iA.w * 9.0) * 0.25;
            vec2 wind = normalize(vec2(1.0, 0.45)) * (gust * 0.8 + flutter + 0.2);
            float bend = t * t * h * 0.45;
            if (kind == 3.0) {
                // Hanging moss: no wind underground, just a slow sway growing toward the tip.
                p.xz += vec2(sin(uTime * 0.6 + iA.w * 20.0), cos(uTime * 0.5 + iA.w * 13.0)) * t * t * 0.06 * abs(h);
            } else {
                p.xz += wind * bend;
                p.y -= dot(wind, wind) * bend * bend * 0.4;
            }

            vPos = p;
            vNormal = vec3(R * aNormal.xz, aNormal.y);
            vRoot = iA.xyz;
            vHeight = head ? 2.0 : t;
            vInfo = iB.yzw;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    public const string GrassFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec3 vRoot;
        in float vHeight;
        in vec3 vInfo;
        out vec4 FragColor;
        void main() {
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            float kind = vInfo.x, seed = vInfo.y, ao = vInfo.z;
            vec3 albedo;
            if (vHeight > 1.5) {
                vec3 petals[5] = vec3[5](vec3(0.80, 0.62, 0.04), vec3(0.62, 0.05, 0.03), vec3(0.80, 0.80, 0.82),
                                         vec3(0.30, 0.15, 0.55), vec3(0.80, 0.35, 0.50));
                albedo = petals[int(seed * 4.999)];
            } else if (kind == 3.0) {
                albedo = mix(vec3(0.025, 0.06, 0.045), vec3(0.05, 0.11, 0.09), seed) * mix(0.6, 1.1, vHeight);
            } else {
                albedo = grassAlbedo(vRoot) * 1.1 * (1.0 - 0.25 * uRain);
                if (kind == 1.0) albedo = mix(albedo, vec3(0.15, 0.17, 0.06), 0.25);
                albedo *= mix(0.35, 1.05, vHeight); // dark, self-shadowed roots; lighter tips
                albedo = max(mix(vec3(dot(albedo, vec3(0.3, 0.6, 0.1))), albedo, 1.25), 0.0); // living grass is saturated
            }
            vec3 bladeN = normalize(vNormal);
            if (dot(bladeN, V) < 0.0) bladeN = -bladeN;
            vec3 N = normalize(mix(kind == 3.0 ? vec3(0, -1, 0) : vec3(0, 1, 0), bladeN, 0.4));

            float shadow = shadowFactor(vPos, vec3(0, 1, 0));
            if (kind == 3.0) shadow = 0.0; // hanging cave moss: no sun reaches it
            float diffuse = max(dot(N, L), 0.0) * 0.75 + 0.25 * max(L.y, 0.0);
            vec3 col = albedo * (uSunColor * diffuse * shadow + ambientLight(albedo, N) * ao * mix(0.55, 1.0, min(vHeight, 1.0)) * (kind == 3.0 ? 0.1 : 1.0));
            // Backlit blades glow as light passes through them.
            col += albedo * uSunColor * shadow * pow(max(dot(-V, L), 0.0), 4.0) * 0.4 * min(vHeight, 1.0);
            col += pointLights(vPos, N, albedo) + albedo * caveFill(N, vPos);
            // Some hanging strands carry a soft blue glow toward their tips.
            if (kind == 3.0) col += vec3(0.15, 0.55, 1.0) * 1.6 * smoothstep(0.7, 1.0, vHeight) * step(0.55, fract(seed * 7.13));
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    public const string LeafCardVertex = Header + LeafMaskHeader + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;   // uv
        layout(location = 4) in vec4 iA;       // centre, species + seed
        layout(location = 5) in vec4 iB;       // canopy normal, AO
        uniform mat4 uViewProj;
        uniform float uTime;
        out vec3 vPos;
        out vec3 vCardNormal;
        out vec3 vCanopyNormal;
        out vec2 vUv;
        out float vSeed;
        flat out int vSpecies;
        out float vAo;
        out float vSnow;
        void main() {
            int code = int(iA.w);
            int species = code % 10;
            // Spruce clusters are squashed into flat horizontal tiers, like real conifer branches.
            vec3 local = species == 2 ? aPos * vec3(1.0, 0.5, 1.0) : aPos;
            float seed = fract(iA.w);
            mat3 R = cardRotation(seed);
            vec3 p = iA.xyz + R * local * cardSize(species);
            p += sway(p, uTime);
            vPos = p;
            vCardNormal = R * normalize(species == 2 ? aNormal * vec3(0.5, 1.0, 0.5) : aNormal);
            vSnow = code >= 10 ? 1.0 : 0.0;
            vCanopyNormal = iB.xyz;
            vUv = aData0.xy;
            vSeed = seed;
            vSpecies = species;
            vAo = iB.w;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    public const string LeafCardFragment = Header + Common + ShadowLookup + LeafMask + """
        in vec3 vPos;
        in vec3 vCardNormal;
        in vec3 vCanopyNormal;
        in vec2 vUv;
        in float vSeed;
        flat in int vSpecies;
        in float vAo;
        in float vSnow;
        out vec4 FragColor;
        void main() {
            float leafId;
            float mask = leafMask(vUv, vSeed, vSpecies, 1.0, leafId);
            if (mask < 0.02) discard;
            // Leaves right at the camera fade out (dithered), so a canopy never fills the screen.
            float camD = distance(vPos, uCamPos);
            if (camD < 2.2 && hash13(floor(gl_FragCoord.xyy * vec3(1.0, 1.0, 0.0)) + 0.5) > smoothstep(0.8, 2.2, camD)) discard;

            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            vec3 cardN = normalize(vCardNormal);
            if (dot(cardN, V) < 0.0) cardN = -cardN;
            // Light the cluster as part of the crown volume, with a little per-leaf variation.
            vec3 N = normalize(normalize(vCanopyNormal) * 0.7 + cardN * 0.3 + (vec3(leafId, fract(leafId * 7.3), fract(leafId * 3.1)) - 0.5) * 0.5);

            vec3 albedo = leafColor(vSpecies, fbm(vPos * 0.1, 3));
            albedo *= 0.65 + 0.7 * leafId;
            if (vSpecies == 7 && leafId > 0.86) albedo = vec3(0.55, 0.22, 0.45); // flowering azalea
            // Snow lying on the upper side of each branch.
            float snowCover = vSnow * 0.85 * smoothstep(0.45, 0.8, N.y + (leafId - 0.5) * 0.5);
            albedo = mix(albedo, vec3(0.80, 0.83, 0.88), snowCover);

            float shadow = shadowFactor(vPos, N);
            float NdotL = max(dot(N, L), 0.0);
            vec3 col = albedo * (uSunColor * NdotL * shadow + ambientLight(albedo, N) * vAo);
            col += albedo * uSunColor * shadow * pow(max(dot(-V, L), 0.0), 3.0) * 0.9;     // light through leaves
            vec3 H = normalize(L + V);
            col += uSunColor * 0.04 * pow(max(dot(cardN, H), 0.0), 30.0) * shadow;        // waxy sheen
            col += (pointLights(vPos, N, albedo) + albedo * caveFill(N, vPos)) * vAo;
            FragColor = vec4(outputColor(applyFog(col, vPos)), smoothstep(0.02, 0.06, mask));
        }
        """;

    public const string LeafCardShadowVertex = Header + LeafMaskHeader + """
        layout(location = 0) in vec3 aPos;
        layout(location = 2) in vec4 aData0;
        layout(location = 4) in vec4 iA;
        uniform mat4 uLightViewProj;
        uniform float uTime;
        out vec2 vUv;
        out float vSeed;
        flat out int vSpecies;
        void main() {
            int code = int(iA.w);
            int species = code % 10;
            // Spruce clusters are squashed into flat horizontal tiers, like real conifer branches.
            vec3 local = species == 2 ? aPos * vec3(1.0, 0.5, 1.0) : aPos;
            float seed = fract(iA.w);
            vec3 p = iA.xyz + cardRotation(seed) * local * cardSize(species);
            p += sway(p, uTime);
            vUv = aData0.xy;
            vSeed = seed;
            vSpecies = species;
            gl_Position = uLightViewProj * vec4(p, 1.0);
        }
        """;

    public const string LeafCardShadowFragment = Header + "float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }\n" + LeafMask + """
        in vec2 vUv;
        in float vSeed;
        flat in int vSpecies;
        void main() {
            float leafId;
            if (leafMask(vUv, vSeed, vSpecies, 1.6, leafId) < 0.04) discard;
        }
        """;

    /// <summary>Instanced rock transform: random yaw and tilt, non-uniform scale (shared by colour and shadow passes).</summary>
    const string RockTransform = """
        mat3 rockRotation(float seed) {
            float a = seed * 6.2831, b = (fract(seed * 7.31) - 0.5) * 0.6, c = (fract(seed * 3.17) - 0.5) * 0.6;
            mat3 ry = mat3(cos(a), 0.0, -sin(a), 0.0, 1.0, 0.0, sin(a), 0.0, cos(a));
            mat3 rx = mat3(1.0, 0.0, 0.0, 0.0, cos(b), sin(b), 0.0, -sin(b), cos(b));
            mat3 rz = mat3(cos(c), sin(c), 0.0, -sin(c), cos(c), 0.0, 0.0, 0.0, 1.0);
            return ry * rx * rz;
        }
        """;

    public const string RockVertex = Header + RockTransform + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 4) in vec4 iA;   // position, seed
        layout(location = 5) in vec4 iB;   // scale xyz, AO
        uniform mat4 uViewProj;
        uniform vec3 uCamPos;
        uniform float uFadeFar;            // pebbles shrink away beyond this distance (0 = never)
        out vec3 vPos;
        out vec3 vNormal;
        out vec3 vLocal;
        out float vAo;
        void main() {
            mat3 R = rockRotation(iA.w);
            vec3 scale = iB.xyz;
            if (uFadeFar > 0.0) scale *= 1.0 - smoothstep(uFadeFar * 0.7, uFadeFar, distance(uCamPos, iA.xyz));
            vec3 p = iA.xyz + R * (aPos * scale);
            vPos = p;
            vNormal = normalize(R * (aNormal / max(iB.xyz, vec3(1e-3))));
            vLocal = aPos * iB.xyz + iA.w * 97.0; // texture space that travels with the rock
            vAo = iB.w;
            gl_Position = uViewProj * vec4(p, 1.0);
        }
        """;

    public const string RockFragment = Header + Common + ShadowLookup + Materials + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec3 vLocal;
        in float vAo;
        out vec4 FragColor;
        void main() {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            N = bump(N, vLocal, 2.2, 0.55);
            N = bump(N, vLocal, 9.0, 0.25);
            vec3 albedo = stoneAlbedo(vLocal * 1.6) * 0.95 * (1.0 - 0.35 * uRain);
            // Moss and dirt collect on upward faces; the buried base is darker and damp.
            float moss = smoothstep(0.55, 0.9, N.y + (fbm(vLocal * 2.0, 3) - 0.5) * 0.6);
            albedo = mix(albedo, vec3(0.05, 0.08, 0.02) * (0.7 + 0.6 * vnoise(vLocal * 12.0)), moss * 0.7);
            float base = 1.0 - smoothstep(-0.05, 0.25, normalize(vNormal).y + 0.2);
            albedo *= 1.0 - 0.35 * base;

            float shadow = shadowFactor(vPos, normalize(vNormal));
            float NdotL = max(dot(N, L), 0.0);
            vec3 col = albedo * (uSunColor * NdotL * shadow + ambientLight(albedo, N) * vAo * mix(0.55, 1.0, N.y * 0.5 + 0.5));
            vec3 H = normalize(L + V);
            col += uSunColor * 0.05 * pow(max(dot(N, H), 0.0), 32.0) * shadow * (1.0 - moss);
            col += wetSheen(N, V, uRain * 0.6) * vAo;
            col += (pointLights(vPos, N, albedo) + albedo * caveFill(N, vPos)) * vAo;
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    public const string RockShadowVertex = Header + RockTransform + """
        layout(location = 0) in vec3 aPos;
        layout(location = 4) in vec4 iA;
        layout(location = 5) in vec4 iB;
        uniform mat4 uLightViewProj;
        void main() {
            gl_Position = uLightViewProj * vec4(iA.xyz + rockRotation(iA.w) * (aPos * iB.xyz), 1.0);
        }
        """;

    /// <summary>The player model: per-vertex colours (clothes, skin, hair), lit like the world.</summary>
    public const string ActorVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0; // rgb colour
        uniform mat4 uViewProj;
        uniform mat4 uModel;
        out vec3 vPos;
        out vec3 vNormal;
        out vec3 vLocal;
        out vec3 vColor;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            vPos = w.xyz;
            vNormal = normalize(mat3(uModel) * aNormal);
            vLocal = aPos;
            vColor = aData0.rgb;
            gl_Position = uViewProj * w;
        }
        """;

    public const string ActorFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec3 vLocal;
        in vec3 vColor;
        out vec4 FragColor;
        void main() {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            // Fine fabric weave and slight colour variation so clothes don't look like plastic.
            float weave = 0.88 + 0.12 * vnoise(vLocal * 90.0) + 0.06 * (fbm(vLocal * 12.0, 2) - 0.5);
            vec3 albedo = vColor * vColor * weave; // vertex colours are stored gamma-ish; square to linear
            float shadow = shadowFactor(vPos, N);
            float wrap = max(dot(N, L) * 0.8 + 0.2, 0.0);
            vec3 col = albedo * (uSunColor * wrap * shadow + ambientLight(albedo, N));
            col += albedo * (pointLights(vPos, N, vec3(1.0)) + caveFill(N, vPos));
            // Soft rim light keeps the silhouette readable against dark ground.
            col += albedo * skyColor(vec3(0, 1, 0)) * pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.4;
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    /// <summary>The Minecraft character: skin texture (pixel-exact) with the outer layer alpha-tested, lit like the world.</summary>
    public const string SkinFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec3 vLocal;
        in vec3 vColor; // xy: skin pixel / 255, z: 1 for the outer layer
        uniform sampler2D uSkin;
        out vec4 FragColor;
        void main() {
            vec2 px = vColor.xy * 255.0;
            vec4 texel = texelFetch(uSkin, clamp(ivec2(floor(px)), ivec2(0), textureSize(uSkin, 0) - 1), 0);
            if (vColor.z > 0.5 && texel.a < 0.5) discard; // transparent parts of hat / jacket / sleeves
            vec3 albedo = pow(texel.rgb, vec3(2.2));
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            float shadow = shadowFactor(vPos, N);
            float wrap = max(dot(N, L) * 0.8 + 0.2, 0.0);
            vec3 col = albedo * (uSunColor * wrap * shadow + ambientLight(albedo, N));
            col += albedo * (pointLights(vPos, N, vec3(1.0)) + caveFill(N, vPos));
            col += albedo * skyColor(vec3(0, 1, 0)) * pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.3;
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    /// <summary>Minecraft's own UI (premultiplied RGBA from the mod), composited over the finished frame.</summary>
    public const string GuiFragment = Header + """
        in vec2 vUv;
        uniform sampler2D uGui;
        out vec4 FragColor;
        void main() { FragColor = texture(uGui, vUv); }
        """;

    /// <summary>Mobs and dropped items: Minecraft's own textures (16-bit UVs packed in aData0), lit like the world.</summary>
    public const string EntityVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;
        layout(location = 3) in vec4 aData1; // vertex colour (layers: dyed wool, leather armour, ...)
        uniform mat4 uViewProj;
        uniform mat4 uModel;
        out vec3 vPos;
        out vec3 vNormal;
        out vec2 vUv;
        out vec4 vColor;
        void main() {
            vColor = aData1;
            vec4 w = uModel * vec4(aPos, 1.0);
            vPos = w.xyz;
            vNormal = normalize(mat3(uModel) * aNormal);
            vec4 b = floor(aData0 * 255.0 + 0.5);
            vUv = vec2(b.x + b.y * 256.0, b.z + b.w * 256.0) / 65535.0;
            gl_Position = uViewProj * w;
        }
        """;

    public const string EntityFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uTex;
        out vec4 FragColor;
        void main() {
            vec4 texel = texture(uTex, vUv) * vColor;
            if (texel.a < 0.1) discard; // cut-out parts (wings, hair, item outlines)
            vec3 albedo = pow(texel.rgb, vec3(2.2));
            vec3 N = normalize(vNormal);
            if (!gl_FrontFacing) N = -N;
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            float shadow = shadowFactor(vPos, N);
            float wrap = max(dot(N, L) * 0.8 + 0.2, 0.0);
            vec3 col = albedo * (uSunColor * wrap * shadow + ambientLight(albedo, N));
            col += albedo * (pointLights(vPos, N, vec3(1.0)) + caveFill(N, vPos));
            col += albedo * skyColor(vec3(0, 1, 0)) * pow(1.0 - max(dot(N, V), 0.0), 3.0) * 0.25;
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    /// <summary>
    /// Minecraft's own block models with its texture atlas (torches, placed and building blocks, rails, ...), lit by
    /// the same sun, shadows, sky light, torches and cave light as the regenerated landscape around them.
    /// Data0: atlas UV (2 x 16 bit). Data1: tint RGB565, flags (bit 0 shade, bits 1-4 light emission, bit 5 nether
    /// portal), sky light. A nether portal's surface is drawn here as swirling violet light (Minecraft's texture is animated;
    /// the atlas we get is a still).
    /// </summary>
    public const string BlockVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;
        layout(location = 3) in vec4 aData1;
        uniform mat4 uViewProj;
        out vec3 vPos;
        out vec3 vNormal;
        out vec2 vUv;
        out vec3 vTint;
        out float vSky;
        flat out float vShade;
        flat out float vLum;
        flat out float vPortal;
        flat out float vFire;
        void main() {
            vPos = aPos;
            vNormal = aNormal;
            vec4 b = floor(aData0 * 255.0 + 0.5);
            vUv = vec2(b.x + b.y * 256.0, b.z + b.w * 256.0) / 65535.0;
            vec4 d = floor(aData1 * 255.0 + 0.5);
            float t = d.x + d.y * 256.0;
            vTint = vec3(floor(t / 2048.0) / 31.0, mod(floor(t / 32.0), 64.0) / 63.0, mod(t, 32.0) / 31.0);
            vShade = mod(d.z, 2.0);
            vLum = mod(floor(d.z / 2.0), 16.0);
            vPortal = mod(floor(d.z / 32.0), 2.0);
            vFire = floor(d.z / 64.0); // 1 fire, 3 soul fire
            vSky = d.w / 255.0;
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    public const string BlockFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec2 vUv;
        in vec3 vTint;
        in float vSky;
        flat in float vShade;
        flat in float vLum;
        flat in float vPortal;
        flat in float vFire;
        uniform sampler2D uAtlas;
        uniform int uTranslucent;
        out vec4 FragColor;

        // Nether portal: violet energy swirling slowly in the portal's plane, bright filaments and drifting sparks.
        vec4 portalColor() {
            vec3 N = normalize(vNormal);
            vec2 uv = abs(N.x) > 0.5 ? vPos.zy : vPos.xy;
            float t = uTime * 0.3;
            vec2 p = uv * 0.7;
            // Domain warping: the pattern flows around itself, like Minecraft's portal swirl.
            vec2 w = vec2(fbm(vec3(p * 1.4, t), 3), fbm(vec3(p * 1.4 + 7.3, t + 3.1), 3));
            vec2 c = p + (w - 0.5) * 2.2;
            float swirl = fbm(vec3(c * 2.0, t * 1.6), 4);
            float filaments = pow(1.0 - abs(sin(swirl * 14.0 + t * 5.0)), 10.0);
            // Deep, saturated violet (kept dark enough that exposure never washes it out to pink-white).
            vec3 col = mix(vec3(0.035, 0.0, 0.11), vec3(0.24, 0.02, 0.62), smoothstep(0.2, 0.8, swirl));
            col += vec3(0.55, 0.12, 0.85) * filaments * 0.6;
            // Sparks: small soft points drifting up through it.
            vec2 q = uv * 14.0 + vec2(0.0, uTime * 0.5);
            vec2 cell = floor(q);
            vec2 at = vec2(hash13(vec3(cell, 4.0)), hash13(vec3(cell, 5.0)));
            float spark = step(0.9, hash13(vec3(cell, 6.0))) * smoothstep(0.12, 0.0, length(fract(q) - at))
                        * (0.5 + 0.5 * sin(uTime * 5.0 + hash13(vec3(cell, 9.0)) * 30.0));
            col += vec3(0.8, 0.45, 1.0) * spark;
            vec3 V = normalize(uCamPos - vPos);
            float edgeOn = 1.0 - abs(dot(N, V));
            // Glowing; a little dimmer at night, when the eyes have adjusted to the dark (else it washes out).
            return vec4(col * (1.6 - 0.6 * uNight), mix(0.82, 0.96, edgeOn));
        }

        // Fire: flames rising and flickering on crossed sheets (vUv: u across the sheet, v up). Turbulent noise
        // scrolls upward; the flame narrows and breaks into tongues toward the top; white-yellow core, orange body,
        // red tips (soul fire: cyan to deep blue). Each sheet has its own seed so the crossing sheets differ.
        vec4 fireColor(bool soul) {
            vec2 uv = vUv;
            float h = uv.y, t = uTime;
            float seed = hash13(floor(vPos + 0.5) + vNormal * 3.1);
            float x = (uv.x - 0.5) * 2.0;
            float rise = h * 2.2 - t * 1.9;
            float turb = fbm(vec3(uv.x * 3.2 + seed * 7.0, rise, seed * 13.0 + t * 0.35), 4);
            float lick = vnoise(vec3(uv.x * 7.5 + seed * 3.0, h * 5.5 - t * 4.2, seed * 5.0));
            float sway = (vnoise(vec3(h * 1.5, t * 0.9, seed * 9.0)) - 0.5) * 0.5 * h;
            float width = mix(0.95, 0.15, h);                    // wide at the base, narrowing upward
            float body = width - abs(x + sway) * (0.85 + 0.5 * lick);
            float flame = body + (turb - 0.5) * 0.9 - h * 0.45;
            float a = smoothstep(0.0, 0.22, flame) * smoothstep(0.0, 0.08, h); // soft edges, faded into the ground
            if (a < 0.01) discard;
            float heat = clamp(flame * 1.7 + (1.0 - h) * 0.35, 0.0, 1.0);
            vec3 col;
            if (soul) col = mix(mix(vec3(0.0, 0.12, 0.45), vec3(0.1, 0.65, 1.0), smoothstep(0.1, 0.5, heat)), vec3(0.75, 1.0, 1.0), smoothstep(0.55, 0.95, heat));
            else col = mix(mix(vec3(0.75, 0.08, 0.01), vec3(1.0, 0.45, 0.05), smoothstep(0.1, 0.5, heat)), vec3(1.0, 0.9, 0.55), smoothstep(0.55, 0.95, heat));
            // Flicker of the whole fire.
            float flicker = 0.85 + 0.15 * vnoise(vec3(t * 6.0, seed * 17.0, 0.0));
            return vec4(col * (2.2 + 2.8 * heat) * flicker, a * mix(0.75, 1.0, heat));
        }

        void main() {
            if (vFire > 0.5) {
                vec4 fc = fireColor(vFire > 2.5);
                FragColor = vec4(outputColor(applyFog(fc.rgb, vPos)), fc.a);
                return;
            }
            if (vPortal > 0.5) {
                vec4 pc = portalColor();
                FragColor = vec4(outputColor(applyFog(pc.rgb, vPos)), pc.a);
                return;
            }
            vec4 texel = texture(uAtlas, vUv);
            if (texel.a < (uTranslucent == 1 ? 0.02 : 0.5)) discard; // cut-out pixels (torch, rails, flowers, ...)
            vec3 srgb = texel.rgb * vTint;
            vec3 albedo = pow(srgb, vec3(2.2));
            vec3 N = normalize(vNormal);
            if (!gl_FrontFacing) N = -N;
            vec3 L = uSunDir;
            // Covered spots (caves, under roofs) get no sun or sky, like the landscape around them.
            float sunVis = smoothstep(0.05, 0.3, vSky);
            float shadow = shadowFactor(vPos, N) * sunVis;
            // Unshaded faces (Minecraft's plants, torches) are lit evenly rather than by their direction.
            float NdotL = vShade > 0.5 ? max(dot(N, L), 0.0) : 0.55;
            vec3 An = vShade > 0.5 ? N : vec3(0.0, 1.0, 0.0);
            vec3 col = albedo * (uSunColor * NdotL * shadow + ambientLight(albedo, An) * (0.1 + 0.9 * vSky));
            col += pointLights(vPos, An, albedo) + albedo * caveFill(An, vPos);
            // Light-emitting blocks: their bright pixels glow (torch flames, glowstone, lanterns).
            float bright = dot(srgb, vec3(0.299, 0.587, 0.114));
            col += albedo * (vLum / 15.0) * 5.0 * smoothstep(0.45, 0.85, bright);
            FragColor = vec4(outputColor(applyFog(col, vPos)), uTranslucent == 1 ? texel.a : 1.0);
        }
        """;

    /// <summary>
    /// Ores on the smooth rock: Minecraft's own ore texture (from the block atlas) projected onto the surface around an
    /// exposed ore, one texture tile per block like Minecraft. Only the ore's pixels are drawn (where the ore texture
    /// differs from its plain rock's), so the realistic rock shows between them, and only inside the ore blocks.
    /// Drawn over the finished, lit terrain with a multiplying blend: the output is ore colour / rock colour, so the ore
    /// takes exactly the light, shade and darkness of the rock it's in.
    /// Data0: atlas corner of the ore texture. Data1: the ore blocks' box (corner relative to the vertex's block, 3 x 3 bit
    /// biased by 4; size - 1, 3 x 2 bit), sky light (6 bit) and rock (2 bit) in the top byte.
    /// </summary>
    public const string OreVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;
        layout(location = 3) in vec4 aData1;
        uniform mat4 uViewProj;
        out vec3 vPos;
        out vec3 vNormal;
        flat out vec3 vBoxMin;
        flat out vec3 vBoxMax;
        flat out vec2 vCorner;
        flat out int vRock;
        void main() {
            vPos = aPos;
            vNormal = aNormal;
            vec4 b = floor(aData0 * 255.0 + 0.5);
            vCorner = vec2(b.x + b.y * 256.0, b.z + b.w * 256.0) / 65535.0;
            vec4 d = floor(aData1 * 255.0 + 0.5);
            int box = int(d.x + d.y * 256.0);
            vBoxMin = floor(aPos) + vec3(box & 7, (box >> 3) & 7, (box >> 6) & 7) - 4.0;
            vBoxMax = vBoxMin + vec3((box >> 9) & 3, (box >> 11) & 3, (box >> 13) & 3) + 1.0;
            vRock = int(mod(d.w, 4.0));
            gl_Position = uViewProj * vec4(aPos, 1.0);
        }
        """;

    public const string OreFragment = Header + Common + Materials + """
        in vec3 vPos;
        in vec3 vNormal;
        flat in vec3 vBoxMin;
        flat in vec3 vBoxMax;
        flat in vec2 vCorner;
        flat in int vRock;
        uniform sampler2D uAtlas;
        uniform vec2 uAtlasSize;
        uniform float uTile; // width of a block texture in the atlas (0..1)
        uniform vec2 uRock0, uRock1, uRock2; // atlas corners of stone, deepslate, netherrack (0: not known yet)
        out vec4 FragColor;

        // The ore's pixel at a block-face position f (0..1): its colour, and whether it's ore (not the rock around it).
        vec4 orePixel(vec2 f, vec2 rock, float lod) {
            vec2 tile = vec2(uTile, uTile * uAtlasSize.x / uAtlasSize.y);
            float px = max(floor(uTile * uAtlasSize.x + 0.5), 1.0);
            vec2 pix = (floor(clamp(f, 0.0, 0.9999) * px) + 0.5) / px; // crisp texels, like Minecraft
            vec3 ore = textureLod(uAtlas, vCorner + pix * tile, lod).rgb;
            float isOre = 1.0;
            if (rock != vec2(0.0)) {
                vec3 plain = textureLod(uAtlas, rock + pix * tile, lod).rgb;
                isOre = smoothstep(0.03, 0.09, length(ore - plain));
            }
            return vec4(ore, 1.0) * isOre;
        }

        void main() {
            vec3 N = normalize(vNormal);
            if (!gl_FrontFacing) N = -N;
            vec2 rock = vRock == 0 ? uRock0 : vRock == 1 ? uRock1 : uRock2;
            // Only on the ore blocks: the surface inside their box, plus a margin for the smoothing.
            vec3 outside = max(vBoxMin - vPos, vPos - vBoxMax);
            float inBox = 1.0 - smoothstep(0.2, 0.3, max(outside.x, max(outside.y, outside.z)));
            if (inBox <= 0.0) discard;
            float px = max(uTile * uAtlasSize.x, 1.0);
            float lod = clamp(log2(max(length(dFdx(vPos)), length(dFdy(vPos))) * px), 0.0, 4.0);
            // Triplanar: the face directions blended by the surface normal, sharply (mostly one face at a time).
            vec3 w = pow(abs(N), vec3(8.0));
            w /= w.x + w.y + w.z;
            vec4 sum = vec4(0.0);
            if (w.x > 0.01) sum += w.x * orePixel(vec2(fract(N.x > 0.0 ? -vPos.z : vPos.z), 1.0 - fract(vPos.y)), rock, lod);
            if (w.y > 0.01) sum += w.y * orePixel(fract(vPos.xz), rock, lod);
            if (w.z > 0.01) sum += w.z * orePixel(vec2(fract(N.z > 0.0 ? vPos.x : -vPos.x), 1.0 - fract(vPos.y)), rock, lod);
            // How much of the pixel is ore, less where fog hides the rock (the fog part of the colour isn't rock).
            float keep = clamp(applyFog(vec3(1.0), vPos).g - applyFog(vec3(0.0), vPos).g, 0.0, 1.0);
            float a = sum.a * inBox * keep;
            if (a < 0.01) discard;
            vec3 ore = pow(sum.rgb / max(sum.a, 1e-3), vec3(2.2));
            vec3 ratio = clamp(ore / max(stoneAlbedo(vPos), vec3(0.02)), 0.0, 8.0);
            FragColor = vec4(mix(vec3(1.0), ratio, a), 1.0);
        }
        """;

    /// <summary>
    /// Realistic animals (see OrganicMobs): smooth bodies coloured from the mob's own texture (softened, not pixels),
    /// with a material per species: short hair with a soft sheen, pink skin with warm subsurface glow, fluffy wool,
    /// feathers, horn and hooves. vLocal is the position on the body (the fur pattern moves with the animal).
    /// </summary>
    public const string OrganicVertex = Header + """
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aData0;
        layout(location = 3) in vec4 aData1;
        uniform mat4 uViewProj;
        uniform mat4 uModel;
        out vec3 vPos;
        out vec3 vNormal;
        out vec3 vLocal;
        out vec2 vUv;
        flat out float vMaterial;
        out float vAlong;
        flat out float vHoofed;
        void main() {
            vec4 w = uModel * vec4(aPos, 1.0);
            vPos = w.xyz;
            vLocal = aPos;
            vNormal = normalize(mat3(uModel) * aNormal);
            vec4 b = floor(aData0 * 255.0 + 0.5);
            vUv = vec2(b.x + b.y * 256.0, b.z + b.w * 256.0) / 65535.0;
            vec4 d = floor(aData1 * 255.0 + 0.5);
            vMaterial = d.x;
            vAlong = d.y / 255.0;
            vHoofed = d.z;
            gl_Position = uViewProj * w;
        }
        """;

    public const string OrganicFragment = Header + Common + ShadowLookup + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec3 vLocal;
        in vec2 vUv;
        flat in float vMaterial;
        in float vAlong;
        flat in float vHoofed;
        uniform sampler2D uTex;
        uniform vec4 uWool; // fleece colour (sheep), alpha 0 when drawing the body
        uniform vec3 uCoatMain, uCoatSecond; // the animal's two main colours (from its texture)
        uniform float uPatches;              // 1: patched coat (cows), 0: plain
        uniform int uDesigned;               // 1: designed anatomy (palette colours), 0: shaped from Minecraft's boxes
        out vec4 FragColor;

        // The texture's colour without its pixels: bilinear (the texture itself is set up for Minecraft's sharp
        // pixels), ignoring transparent texels, averaged over a small footprint.
        vec4 bilinear(vec2 uv) {
            ivec2 size = textureSize(uTex, 0);
            vec2 p = uv * vec2(size) - 0.5;
            ivec2 i = ivec2(floor(p));
            vec2 f = fract(p);
            vec4 sum = vec4(0.0);
            for (int y = 0; y <= 1; y++)
            for (int x = 0; x <= 1; x++) {
                vec4 t = texelFetch(uTex, clamp(i + ivec2(x, y), ivec2(0), size - 1), 0);
                float w = (x == 1 ? f.x : 1.0 - f.x) * (y == 1 ? f.y : 1.0 - f.y) * step(0.1, t.a);
                sum += vec4(t.rgb * w, w);
            }
            return sum;
        }
        vec3 softColor(vec2 uv) {
            vec2 texel = 1.0 / vec2(textureSize(uTex, 0));
            vec4 sum = bilinear(uv + vec2(-0.3, -0.3) * texel) + bilinear(uv + vec2(0.3, -0.3) * texel)
                     + bilinear(uv + vec2(-0.3, 0.3) * texel) + bilinear(uv + vec2(0.3, 0.3) * texel);
            return sum.a > 0.0 ? sum.rgb / sum.a : texture(uTex, uv).rgb;
        }

        void main() {
            vec3 N = normalize(vNormal);
            vec3 V = normalize(uCamPos - vPos);
            vec3 L = uSunDir;
            int mat = int(vMaterial + 0.5);
            vec3 srgb = mat == 2 ? uWool.rgb : softColor(vUv);
            if (uDesigned == 1 && mat != 2) {
                // Palette colours: patched or plain coat (lighter toward the belly), and fixed tones for the rest.
                float pm = smoothstep(0.47, 0.53, fbm(vLocal * 2.6 + 3.0, 4));
                vec3 coat = mix(uCoatMain, uCoatSecond, pm * uPatches);
                coat = mix(coat, uCoatSecond, 0.18 * (1.0 - uPatches) * smoothstep(0.2, -0.8, normalize(vNormal).y));
                if (mat == 0 || mat == 3) srgb = coat;
                else if (mat == 1) srgb = vec3(0.78, 0.55, 0.48);
                else if (mat == 10) srgb = mix(uCoatMain, vec3(0.9, 0.55, 0.55), 0.35);  // pig skin, snout, udder
                else if (mat == 4) srgb = vec3(0.16, 0.13, 0.11);                         // hoof, horn
                else if (mat == 6) srgb = vec3(0.12, 0.09, 0.09);                         // nose
                else if (mat == 7) srgb = vec3(0.86, 0.62, 0.18);                         // beak, chicken legs
                else if (mat == 8) srgb = vec3(0.72, 0.06, 0.05);                         // comb, wattle
                else if (mat == 9) srgb = uCoatMain * 0.45;                               // mane, tail tuft
                if (mat == 10) mat = 1;  // shaded as skin
                if (mat == 9) mat = 0;   // shaded as hair
                if (mat == 6 || mat == 8) mat = 1;
                if (mat == 7) mat = 4;
            }
            float spec = 0.0, specPow = 16.0, sheen = 0.0, sss = 0.0;

            if (mat == 0) {
                // Short hair: fine strands break up the colour and the normal, with a soft sheen at grazing angles.
                float strands = vnoise(vLocal * vec3(180.0, 40.0, 180.0));
                srgb *= 0.86 + 0.24 * strands;
                srgb *= 0.92 + 0.16 * vnoise(vLocal * 9.0);
                N = normalize(N + (vec3(vnoise(vLocal * 120.0), vnoise(vLocal * 120.0 + 7.0), vnoise(vLocal * 120.0 + 13.0)) - 0.5) * 0.35);
                sheen = 0.18;
            } else if (mat == 1) {
                // Skin: smooth, slightly glossy, light scattering through it gives a warm glow.
                srgb *= 0.94 + 0.1 * vnoise(vLocal * 30.0);
                spec = 0.12; specPow = 24.0; sss = 0.35;
            } else if (mat == 2) {
                // Wool: dense curls, darker in the crevices between them.
                float curls = fbm(vLocal * 38.0, 3);
                srgb = srgb * (0.62 + 0.5 * curls) * vec3(1.0, 0.98, 0.94);
                N = normalize(N + (vec3(vnoise(vLocal * 60.0), vnoise(vLocal * 60.0 + 3.0), vnoise(vLocal * 60.0 + 9.0)) - 0.5) * 0.9);
                sheen = 0.08;
            } else if (mat == 3) {
                // Feathers: overlapping rows with soft edges.
                vec2 f = vec2(vLocal.x * 40.0 + vLocal.z * 40.0, vLocal.y * 55.0);
                float row = fract(f.y + 0.5 * floor(f.x));
                srgb *= 0.82 + 0.25 * smoothstep(0.0, 0.8, row);
                sheen = 0.12;
            } else if (mat == 5) {
                // Eyes: dark and wet, with a sharp highlight.
                srgb = vec3(0.05, 0.035, 0.025);
                spec = 1.2; specPow = 160.0;
            } else {
                // Horn, beaks: hard and glossy.
                spec = 0.35; specPow = 40.0;
            }
            // Hooves at the foot of hoofed legs.
            if (vHoofed > 0.5) srgb = mix(srgb, vec3(0.13, 0.11, 0.09), smoothstep(0.86, 0.93, vAlong));

            vec3 albedo = pow(clamp(srgb, 0.0, 1.0), vec3(2.2));
            float shadow = shadowFactor(vPos, N);
            float wrap = max(dot(N, L) * 0.75 + 0.25, 0.0);
            vec3 col = albedo * (uSunColor * wrap * shadow + ambientLight(albedo, N));
            col += albedo * (pointLights(vPos, N, vec3(1.0)) + caveFill(N, vPos));
            col += albedo * vec3(1.0, 0.35, 0.25) * uSunColor * sss * shadow * pow(max(dot(-V, L), 0.0), 2.0);
            vec3 H = normalize(L + V);
            col += uSunColor * spec * pow(max(dot(N, H), 0.0), specPow) * shadow;
            col += albedo * (skyColor(vec3(0, 1, 0)) * (1.0 - 0.9 * uUnderground) + uSunColor * shadow * 0.3) * sheen * pow(1.0 - max(dot(N, V), 0.0), 3.0);
            FragColor = vec4(outputColor(applyFog(col, vPos)), 1.0);
        }
        """;

    /// <summary>The targeted-block outline and mining overlay: flat colour, fogged like the world.</summary>
    public const string OutlineFragment = Header + Common + """
        in vec3 vPos;
        in vec3 vNormal;
        in vec2 vUv;
        uniform vec4 uColor;
        out vec4 FragColor;
        void main() {
            // Cracks for the mining overlay: a cellular pattern that fills in as the stage rises.
            float a = uColor.a;
            if (vUv.x > 0.5) {
                vec3 q = floor(vPos * 8.0);
                a *= step(hash13(q), uColor.a * 1.6);
            }
            FragColor = vec4(outputColor(applyFog(uColor.rgb, vPos)), a);
        }
        """;

    public const string ShadowVertex = Header + """
        layout(location = 0) in vec3 aPos;
        uniform mat4 uLightViewProj;
        uniform mat4 uModel; // identity for world geometry; set per part for the player
        void main() { gl_Position = uLightViewProj * uModel * vec4(aPos, 1.0); }
        """;

    public const string ShadowFragment = Header + "void main() { }\n";
}
