// FullscreenQuadVS.hlsl — D3D11 全屏三角形（无顶点缓冲，SV_VertexID 生成）

struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD; };

VSOut main(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
