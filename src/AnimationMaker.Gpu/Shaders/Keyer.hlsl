cbuffer Settings : register(b0) {
    float4 Screen;
    float4 Key;       // mode, similarity, softness, despill
    float4 Matte;     // clip black, clip white, red weight, blue weight
    float4 Edge;      // choke, feather, enabled, clean plate
    float4 Sizes;     // input width/height, output width/height
    float4 Crop;      // crop rectangle in input pixels
    float4 Preview;   // background, zoom, viewport width/height
    float4 Backdrop;  // image width/height, fit mode (0 fill, 1 fit), available
};
Texture2D<float4> Image : register(t0);
Texture2D<float4> Mask : register(t1);
Texture2D<float4> Plate : register(t2);
Texture2D<float4> BackgroundImage : register(t3);
SamplerState LinearClamp : register(s0);

struct Vertex { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Vertex VS(uint id : SV_VertexID) {
    Vertex v;
    v.uv = float2((id << 1) & 2, id & 2);
    v.position = float4(v.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
float3 ToLinear(float3 c) { return lerp(c / 12.92, pow(max((c + .055) / 1.055, 0), 2.4), step(.04045, c)); }
float3 ToSrgb(float3 c) { c = max(c, 0); return lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - .055, step(.0031308, c)); }
float2 Chroma(float3 c) { return float2(dot(c, float3(-.114572, -.385428, .5)), dot(c, float3(.5, -.454153, -.045847))); }

float4 KeyPS(Vertex v) : SV_Target {
    float4 original = Image.SampleLevel(LinearClamp, v.uv, 0);
    if (Edge.z < .5) return float4(ToLinear(original.rgb) * original.a, original.a);
    float3 bg = Edge.w > .5 ? Plate.SampleLevel(LinearClamp, v.uv, 0).rgb : Screen.rgb;
    float2 masks = Mask.SampleLevel(LinearClamp, v.uv, 0).rg;
    float alpha;
    bool blue = bg.b > bg.g;
    float3 color = blue ? original.rbg : original.rgb;
    float3 screen = blue ? bg.rbg : bg;
    if (Key.x > .5) {
        float distance = length(Chroma(original.rgb) - Chroma(bg));
        alpha = smoothstep(Key.y, Key.y + max(Key.z, .0001), distance);
    } else {
        float denominator = max(screen.g - screen.r * Matte.z - screen.b * Matte.w, .0001);
        float dominance = color.g - color.r * Matte.z - color.b * Matte.w;
        alpha = saturate(1 - max(dominance, 0) / denominator);
        // Small residuals from MP4 compression are controlled with the matte clips.
    }
    float rawAlpha = alpha;
    alpha = saturate((alpha - Matte.x) / max(Matte.y - Matte.x, .0001));
    float3 foreground = lerp(original.rgb, max(original.rgb - (1 - rawAlpha) * bg, 0) / max(rawAlpha, .0001), Key.w);
    if (rawAlpha < .0001) foreground = 0;
    float3 spillColor = blue ? foreground.rbg : foreground;
    float excess = max(0, spillColor.g - max(spillColor.r, spillColor.b));
    float before = dot(spillColor, float3(.2126, .7152, .0722));
    spillColor.g -= excess * Key.w;
    float after = dot(spillColor, float3(.2126, .7152, .0722));
    spillColor += max(0, before - after);
    foreground = saturate(blue ? spillColor.rbg : spillColor);
    // A holdout preserves both alpha and the original green foreground colors.
    foreground = lerp(foreground, original.rgb, masks.r);
    alpha = max(alpha, masks.r) * (1 - masks.g) * original.a;
    return float4(ToLinear(foreground) * alpha, alpha);
}

float4 RefinePS(Vertex v) : SV_Target {
    float4 center = Image.SampleLevel(LinearClamp, v.uv, 0);
    if (abs(Edge.x) < .01 && Edge.y < .01) return center;
    float alpha = center.a;
    float4 color = center;
    int radius = min(4, (int)ceil(abs(Edge.x)));
    for (int y = -radius; y <= radius; y++) for (int x = -radius; x <= radius; x++) {
        float4 s = Image.SampleLevel(LinearClamp, v.uv + float2(x, y) / Sizes.xy, 0);
        if (Edge.x > 0) alpha = min(alpha, s.a);
        else if (s.a > alpha) { alpha = s.a; color = s; }
    }
    if (Edge.y > .01) {
        float4 sum = 0; float weight = 0;
        int blurRadius = min(5, (int)ceil(Edge.y * 2));
        for (int y = -blurRadius; y <= blurRadius; y++) for (int x = -blurRadius; x <= blurRadius; x++) {
            float w = exp(-float(x*x + y*y) / max(2 * Edge.y * Edge.y, .001));
            sum += Image.SampleLevel(LinearClamp, v.uv + float2(x, y) / Sizes.xy, 0) * w; weight += w;
        }
        float4 blurred = sum / max(weight, .001);
        alpha = lerp(alpha, blurred.a, saturate(Edge.y));
        color = lerp(color, blurred, saturate(Edge.y));
    }
    return float4(color.rgb / max(color.a, .0001) * alpha, alpha);
}
float Sinc(float x) { return abs(x) < .0001 ? 1 : sin(3.14159265359 * x) / (3.14159265359 * x); }
float Lanczos(float x) { return abs(x) < 3 ? Sinc(x) * Sinc(x / 3) : 0; }
float4 ScaleHorizontalPS(Vertex v) : SV_Target {
    float scale = max(1, Crop.z / Sizes.z);
    float position = Crop.x + v.position.x * Crop.z / Sizes.z - .5;
    int radius = min(192, (int)ceil(3 * scale));
    float4 result = 0; float weights = 0;
    for (int i = -radius; i <= radius; i++) {
        float x = floor(position) + i;
        float w = Lanczos((x - position) / scale);
        float2 uv = float2((clamp(x, Crop.x, Crop.x + Crop.z - 1) + .5) / Sizes.x, (Crop.y + v.position.y) / Sizes.y);
        result += Image.SampleLevel(LinearClamp, uv, 0) * w; weights += w;
    }
    return result / max(weights, .0001);
}
float4 EncodePS(Vertex v) : SV_Target {
    float scale = max(1, Crop.w / Sizes.w);
    float position = v.position.y * Crop.w / Sizes.w - .5;
    int radius = min(192, (int)ceil(3 * scale));
    float4 result = 0; float weights = 0;
    for (int i = -radius; i <= radius; i++) {
        float y = floor(position) + i;
        float w = Lanczos((y - position) / scale);
        float2 uv = float2(v.position.x / Sizes.z, (clamp(y, 0, Crop.w - 1) + .5) / Crop.w);
        result += Image.SampleLevel(LinearClamp, uv, 0) * w; weights += w;
    }
    result /= max(weights, .0001);
    result.a = saturate(result.a);
    if (result.a < .5 / 255) return 0;
    return float4(saturate(ToSrgb(result.rgb / max(result.a, .0001))), result.a);
}
float4 PremultipliedPixel(int2 p) {
    p = clamp(p, int2(0, 0), int2(Sizes.zw) - 1);
    float4 c = Image.Load(int3(p, 0)); return float4(ToLinear(c.rgb) * c.a, c.a);
}
float4 BackgroundPixel(int2 p) {
    p = clamp(p, int2(0, 0), int2(Backdrop.xy) - 1);
    float4 c = BackgroundImage.Load(int3(p, 0));
    return float4(ToLinear(c.rgb) * c.a, c.a);
}
float3 PreviewBackgroundColor(float2 uv, float2 viewportPosition) {
    float checker = fmod(floor(viewportPosition.x / 16) + floor(viewportPosition.y / 16), 2);
    float3 checkerColor = ToLinear(lerp(float3(.16, .18, .21), float3(.23, .25, .29), checker));
    if (Preview.x > 4.5 && Backdrop.w > .5) {
        float videoAspect = Sizes.z / Sizes.w;
        float imageAspect = Backdrop.x / Backdrop.y;
        float2 bgUv;
        if (Backdrop.z > .5) {
            float2 contentSize = imageAspect > videoAspect ? float2(1, videoAspect / imageAspect) : float2(imageAspect / videoAspect, 1);
            bgUv = (uv - .5) / contentSize + .5;
            if (any(bgUv < 0) || any(bgUv > 1)) return checkerColor;
        } else {
            float2 visible = imageAspect > videoAspect ? float2(videoAspect / imageAspect, 1) : float2(1, imageAspect / videoAspect);
            bgUv = (uv - .5) * visible + .5;
        }
        float2 position = bgUv * Backdrop.xy - .5;
        int2 p = (int2)floor(position); float2 f = frac(position);
        float4 bg = lerp(lerp(BackgroundPixel(p), BackgroundPixel(p + int2(1, 0)), f.x), lerp(BackgroundPixel(p + int2(0, 1)), BackgroundPixel(p + 1), f.x), f.y);
        return bg.rgb + checkerColor * (1 - bg.a);
    }
    if (Preview.x < .5 || Preview.x > 4.5) return checkerColor;
    if (Preview.x < 1.5) return float3(1, 1, 1);
    if (Preview.x < 2.5) return float3(0, 0, 0);
    return float3(1, 0, 1);
}
float4 PreviewPS(Vertex v) : SV_Target {
    float fit = min(Preview.z / Sizes.z, Preview.w / Sizes.w) * Preview.y;
    float2 displayed = Sizes.zw * fit;
    float2 uv = (v.position.xy - (Preview.zw - displayed) * .5) / displayed;
    if (any(uv < 0) || any(uv >= 1)) return float4(.047, .055, .071, 1);
    float2 pos = uv * Sizes.zw - .5, f = frac(pos);
    int2 p = (int2)floor(pos);
    float4 c = lerp(lerp(PremultipliedPixel(p), PremultipliedPixel(p + int2(1, 0)), f.x), lerp(PremultipliedPixel(p + int2(0, 1)), PremultipliedPixel(p + 1), f.x), f.y);
    if (Preview.x > 3.5 && Preview.x < 4.5) return float4(c.aaa, 1);
    float3 bg = PreviewBackgroundColor(uv, v.position.xy);
    return float4(ToSrgb(c.rgb + bg * (1 - c.a)), 1);
}
