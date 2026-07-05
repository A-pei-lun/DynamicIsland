using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIslandPro.LiquidGlass
{
    /// <summary>可分离高斯模糊 · 垂直遍 ShaderEffect。ps_3_0，9-tap 核。</summary>
    public class GaussianBlurVEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(GaussianBlurVEffect), 0);

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        /// <summary>偏移跨距。Y = radius / pixelHeight，X 不使用（垂直遍用 Y）。</summary>
        public static readonly DependencyProperty TexelSizeProperty =
            DependencyProperty.Register("TexelSize", typeof(Point), typeof(GaussianBlurVEffect),
                new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(0)));

        public Point TexelSize
        {
            get => (Point)GetValue(TexelSizeProperty);
            set => SetValue(TexelSizeProperty, value);
        }

        public GaussianBlurVEffect()
        {
            PixelShader = new PixelShader
            {
                UriSource = new Uri("pack://application:,,,/DynamicIslandPro;component/LiquidGlass/Shaders/GaussianBlurV.ps")
            };
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(TexelSizeProperty);
        }
    }
}
