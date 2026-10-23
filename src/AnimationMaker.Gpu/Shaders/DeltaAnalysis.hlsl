cbuffer AnalysisSettings : register(b0) { uint Width; uint Height; uint Tolerance; uint Columns; };
Texture2D<float4> Current : register(t0);
Texture2D<float4> Previous : register(t1);
RWStructuredBuffer<uint> Result : register(u0);
groupshared uint Bits[8];
groupshared uint4 ChangedBounds[256];
groupshared uint4 DataBounds[256];
groupshared uint2 Errors[256];

uint VisibleError(uint4 a, uint4 b) {
    int error = abs((int)a.a - (int)b.a) * 255;
    [unroll] for (uint c = 0; c < 3; c++) {
        error = max(error, abs((int)(a[c] * a.a) - (int)(b[c] * b.a)));
        error = max(error, abs((int)((255 - a[c]) * a.a) - (int)((255 - b[c]) * b.a)));
    }
    return (error + 254) / 255;
}
[numthreads(16,16,1)]
void Analyze(uint3 position : SV_DispatchThreadID, uint3 group : SV_GroupID, uint index : SV_GroupIndex) {
    if (index < 8) Bits[index] = 0;
    uint4 empty = uint4(Width, Height, 0, 0);
    uint4 changed = empty, data = empty; uint error = 0; bool dirty = false;
    if (position.x < Width && position.y < Height) {
        // Load exact RGBA8 values from the same UNORM texture that is read back by the processor.
        uint4 a = (uint4)round(saturate(Current.Load(int3(position.xy,0))) * 255.0);
        uint4 b = (uint4)round(saturate(Previous.Load(int3(position.xy,0))) * 255.0);
        error = VisibleError(a,b);
        dirty = Tolerance == 0 ? any(a != b) : error > Tolerance || ((a.a == 0) != (b.a == 0));
        if (dirty) changed = uint4(position.xy, position.xy + 1);
        if (any(a != 0)) data = uint4(position.xy, position.xy + 1);
    }
    ChangedBounds[index] = changed; DataBounds[index] = data; Errors[index] = uint2(error,error);
    GroupMemoryBarrierWithGroupSync();
    if (dirty) InterlockedOr(Bits[index >> 5], 1u << (index & 31));
    [unroll] for (uint step = 128; step > 0; step >>= 1) {
        if (index < step) {
            ChangedBounds[index] = uint4(min(ChangedBounds[index].xy, ChangedBounds[index+step].xy), max(ChangedBounds[index].zw, ChangedBounds[index+step].zw));
            DataBounds[index] = uint4(min(DataBounds[index].xy, DataBounds[index+step].xy), max(DataBounds[index].zw, DataBounds[index+step].zw));
            Errors[index] = uint2(max(Errors[index].x,Errors[index+step].x),Errors[index].y+Errors[index+step].y);
        }
        GroupMemoryBarrierWithGroupSync();
    }
    if (index == 0) {
        uint offset = (group.y * Columns + group.x) * 20;
        [unroll] for (uint j = 0; j < 8; j++) Result[offset+j] = Bits[j];
        [unroll] for (uint c = 0; c < 4; c++) { Result[offset+8+c] = ChangedBounds[0][c]; Result[offset+14+c] = DataBounds[0][c]; }
        Result[offset+12] = Errors[0].x; Result[offset+13] = Errors[0].y;
        Result[offset+18] = 0; Result[offset+19] = 0;
    }
}
