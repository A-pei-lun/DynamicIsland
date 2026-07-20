// GaussianBlurV.hlsl — 可分离高斯模糊 · 垂直遍
// 9-tap 核，σ≈1.5。TexelSize.y = (radius / height)，调整偏移跨距=控模糊量。
// ps_3_0 编译：fxc /T ps_3_0 /O3 /Fo GaussianBlurV.ps GaussianBlurV.hlsl

sampler2D Input : register(s0);
float2 TexelSize : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 color = 0;

    float weight[9] = {
        0.016, 0.054, 0.122, 0.184, 0.248, 0.184, 0.122, 0.054, 0.016
    };
    float offset[9] = { -4, -3, -2, -1, 0, 1, 2, 3, 4 };

    for (int i = 0; i < 9; i++)
        color += tex2D(Input, uv + float2(0, offset[i] * TexelSize.y)) * weight[i];

    return color;
}
