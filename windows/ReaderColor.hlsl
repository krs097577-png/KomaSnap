sampler2D page : register(s0);
float4 gain : register(c0);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 pixel = tex2D(page, uv);
    return float4(pixel.rgb * gain.rgb, pixel.a);
}

