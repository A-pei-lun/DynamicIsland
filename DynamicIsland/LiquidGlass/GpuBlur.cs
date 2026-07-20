using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace DynamicIsland.LiquidGlass
{
    /// <summary>
    /// D3D11 双遍可分离高斯模糊（GPU ps_4_0）。复用 HlslGlassBackend 同核同权值的着色器源
    /// （GaussianBlurH/V_D3D.hlsl，9-tap，已编译为 .cso）。
    ///
    /// 管线：input(SRV) --H-pass--> midTex(RTV/SRV) --V-pass--> output(RTV)
    /// 全屏三角形 VS（SV_VertexID 生成，无顶点缓冲）+ 两遍像素着色器。
    /// cbuffer TexelSize=float2(rH/W, rV/H)：H shader 读 .x，V shader 读 .y，一帧只更新一次。
    ///
    /// 注：CsWin32 生成的 D3D11 COM 对象非 IDisposable，用 Marshal.ReleaseComObject 释放。
    ///     CreateVertexShader/CreatePixelShader 无 friendly out 重载，走原始接口 + GetObjectForIUnknown。
    /// </summary>
    internal sealed class GpuBlur : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BlurCB
        {
            public float TexelSizeX, TexelSizeY, Pad0, Pad1; // 16 字节（cbuffer 最小对齐）
        }

        private readonly ID3D11Device _dev;
        private readonly ID3D11DeviceContext _ctx;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psH, _psV;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbTexel;

        // 中间纹理（H-pass 输出 / V-pass 输入）
        private ID3D11Texture2D? _midTex;
        private ID3D11ShaderResourceView? _midSrv;
        private ID3D11RenderTargetView? _midRtv;

        // 输入 SRV 缓存（输入纹理稳定时只建一次；换了则重建）
        private ID3D11Texture2D? _cachedInput;
        private ID3D11ShaderResourceView? _inputSrv;

        private uint _texW, _texH;          // midTex 实际尺寸（渲染线程）
        private uint _cfgW, _cfgH;          // 期望尺寸（UI 线程写，Blur 应用）
        private float _texelX, _texelY;     // 期望 texel（UI 线程 Volatile.Write，渲染线程 Volatile.Read）
        private bool _disposed;
        public int MapFailCount;            // 诊断：Map 失败次数（>0 说明 cbuffer 没更新）

        public GpuBlur(ID3D11Device dev, ID3D11DeviceContext ctx)
        {
            _dev = dev;
            _ctx = ctx;
            _vs = CreateVS(LoadResource("FullscreenQuadVS.cso"));
            _psH = CreatePS(LoadResource("GaussianBlurH_D3D.cso"));
            _psV = CreatePS(LoadResource("GaussianBlurV_D3D.cso"));
            _sampler = CreateSampler();
            _cbTexel = CreateConstantBuffer();
        }

        /// <summary>配置尺寸 + 半径（UI 线程调）。只记字段，不碰 immediate context--
        /// 实际 midTex 重建与 cbuffer 更新推迟到 Blur（渲染线程），避免跨线程 D3D11 竞态导致更新丢失。</summary>
        public void Configure(uint w, uint h, double radius)
        {
            if (w == 0 || h == 0) return;
            _cfgW = w; _cfgH = h;
            // 半径钳制：9-tap 核跨距过大产生条纹，按各自维度钳（同 HlslGlassBackend.UpdateTexelSize）
            double maxRH = w / 6.0, maxRV = h / 6.0;
            Volatile.Write(ref _texelX, (float)(Math.Min(radius, maxRH) / w));
            Volatile.Write(ref _texelY, (float)(Math.Min(radius, maxRV) / h));
        }

        /// <summary>模糊：input -> H-pass -> midTex -> V-pass -> outputRtv。在 FrameArrived 回调内调。</summary>
        public unsafe void Blur(ID3D11Texture2D input, ID3D11RenderTargetView outputRtv)
        {
            if (_disposed || _vs == null || _psH == null || _psV == null || _sampler == null
                || _cbTexel == null) return;

            // 应用配置变更（渲染线程）：midTex 重建 + cbuffer 更新。Configure 只设字段，此处落地，
            // 避免 UI 线程（UpdateSettings）与渲染线程（Blur）并发访问 immediate context。
            if (_cfgW != _texW || _cfgH != _texH)
            {
                EnsureMidTex(_cfgW, _cfgH);
                _texW = _cfgW; _texH = _cfgH;
            }
            if (_midRtv == null || _midSrv == null || _texW == 0) return;
            // 每帧更新 cbuffer（Volatile.Read 跨线程读最新半径），不依赖 dirty 标志触发，避免漏更新
            UpdateTexel(Volatile.Read(ref _texelX), Volatile.Read(ref _texelY));

            EnsureInputSrv(input);
            if (_inputSrv == null) return;

            var vp = new D3D11_VIEWPORT
            {
                TopLeftX = 0, TopLeftY = 0, Width = _texW, Height = _texH, MinDepth = 0, MaxDepth = 1,
            };

            // ── H-pass：input(SRV) -> midTex(RTV) ──
            _ctx.OMSetRenderTargets(new[] { _midRtv }, null);
            _ctx.RSSetViewports(new[] { vp });
            _ctx.IASetInputLayout(null);
            _ctx.IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            _ctx.VSSetShader(_vs, null);
            _ctx.PSSetShader(_psH, null);
            _ctx.PSSetShaderResources(0, new[] { _inputSrv });
            _ctx.PSSetSamplers(0, new[] { _sampler });
            _ctx.VSSetConstantBuffers(0, new[] { _cbTexel });
            _ctx.PSSetConstantBuffers(0, new[] { _cbTexel });
            _ctx.Draw(3, 0);

            // ── V-pass：midTex(SRV) -> output(RTV) ──
            _ctx.OMSetRenderTargets(new[] { outputRtv }, null);
            _ctx.PSSetShader(_psV, null);
            _ctx.PSSetShaderResources(0, new[] { _midSrv });
            _ctx.Draw(3, 0);

            // 解绑，避免后续把 output 当 SRV 读时 hazard
            _ctx.OMSetRenderTargets(null, null);
            _ctx.PSSetShaderResources(0, new ID3D11ShaderResourceView?[] { null });
        }

        // ── 资源创建 ──

        private void EnsureMidTex(uint w, uint h)
        {
            Rel(_midSrv); Rel(_midRtv); Rel(_midTex);

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
                CPUAccessFlags = 0, MiscFlags = 0,
            };
            _dev.CreateTexture2D(desc, null, out _midTex);
            _dev.CreateShaderResourceView(_midTex, null, out _midSrv);
            _dev.CreateRenderTargetView(_midTex, null, out _midRtv);
        }

        private void EnsureInputSrv(ID3D11Texture2D input)
        {
            if (_cachedInput == input && _inputSrv != null) return;
            Rel(_inputSrv);
            _cachedInput = input;
            _dev.CreateShaderResourceView(input, null, out _inputSrv);
        }

        private unsafe void UpdateTexel(float tx, float ty)
        {
            if (_cbTexel == null) return;
            // ⚠️ 诊断（保留一轮验证）：Map 写死 0.5（极大）。跑一次 GPU 模式--
            //   模糊变极强糊成一坨 = cbuffer 绑定通（根因已修：旧 .cso 无 cbuffer、texel 写死 0.15，
            //   新 .cso 读 cb0，与 C# slot 0 绑定匹配）；仍不变 = 绑定坏（查寄存器/slot）。
            //   验证通过后删这两行，恢复用 Configure 传入的真实 radius（Volatile.Read）。
            tx = 0.5f; ty = 0.5f;
            var cb = new BlurCB { TexelSizeX = tx, TexelSizeY = ty };
            // DYNAMIC cbuffer 用 Map(WRITE_DISCARD) 更新（正道）。UpdateSubresource 对被绑定的 cbuffer
            // 频繁更新不可靠。try-catch 防 Map 失败抛异常触发回退（失败则 cbuffer 保持旧值，不崩）。
            try
            {
                _ctx.Map(_cbTexel, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                *(BlurCB*)mapped.pData = cb;
                _ctx.Unmap(_cbTexel, 0);
            }
            catch { MapFailCount++; }
        }

        private unsafe ID3D11VertexShader CreateVS(byte[] code)
        {
            fixed (byte* p = code)
            {
                ID3D11VertexShader_unmanaged* pNative = null;
                _dev.CreateVertexShader(p, (nuint)code.Length, null, &pNative);
                return pNative != null ? (ID3D11VertexShader)Marshal.GetObjectForIUnknown((nint)pNative) : null!;
            }
        }

        private unsafe ID3D11PixelShader CreatePS(byte[] code)
        {
            fixed (byte* p = code)
            {
                ID3D11PixelShader_unmanaged* pNative = null;
                _dev.CreatePixelShader(p, (nuint)code.Length, null, &pNative);
                return pNative != null ? (ID3D11PixelShader)Marshal.GetObjectForIUnknown((nint)pNative) : null!;
            }
        }

        private ID3D11SamplerState CreateSampler()
        {
            var desc = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                MipLODBias = 0, MaxAnisotropy = 0,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                BorderColor = default,
                MinLOD = 0, MaxLOD = 0,
            };
            _dev.CreateSamplerState(desc, out ID3D11SamplerState s);
            return s;
        }

        private ID3D11Buffer CreateConstantBuffer()
        {
            var desc = new D3D11_BUFFER_DESC
            {
                ByteWidth = 16,
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                MiscFlags = 0,
                StructureByteStride = 0,
            };
            _dev.CreateBuffer(desc, null, out ID3D11Buffer buf);
            return buf;
        }

        private static byte[] LoadResource(string name)
        {
            var uri = new Uri($"pack://application:,,,/LiquidGlass/Shaders/{name}");
            var sri = Application.GetResourceStream(uri)
                ?? throw new FileNotFoundException($"着色器资源缺失: {name}", name);
            using var ms = new MemoryStream();
            sri.Stream.CopyTo(ms);
            return ms.ToArray();
        }

        private static void Rel(object? o)
        {
            if (o is not null) try { Marshal.ReleaseComObject(o); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Rel(_inputSrv); _inputSrv = null; _cachedInput = null;
            Rel(_midSrv); Rel(_midRtv); Rel(_midTex);
            _midSrv = null; _midRtv = null; _midTex = null;
            Rel(_cbTexel); Rel(_sampler); Rel(_psH); Rel(_psV); Rel(_vs);
            _cbTexel = null; _sampler = null; _psH = null; _psV = null; _vs = null;
        }
    }
}
