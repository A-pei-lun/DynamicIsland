using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DynamicIslandPro.LiquidGlass
{
    /// <summary>可分离高斯模糊 · 水平遍 ShaderEffect。ps_3_0，9-tap 核。</summary>
    public class GaussianBlurHEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(GaussianBlurHEffect), 0);

        public Brush Input
        {
            get => (Brush)GetValue(InputProperty);
            set => SetValue(InputProperty, value);
        }

        /// <summary>偏移跨距。X = radius / pixelWidth。Y 不使用（水平遍用 X）。</summary>
        public static readonly DependencyProperty TexelSizeProperty =
            DependencyProperty.Register("TexelSize", typeof(Point), typeof(GaussianBlurHEffect),
                new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(0)));

        public Point TexelSize
        {
            get => (Point)GetValue(TexelSizeProperty);
            set => SetValue(TexelSizeProperty, value);
        }

        public GaussianBlurHEffect()
        {
            PixelShader = new PixelShader
            {
                UriSource = new Uri("pack://application:,,,/DynamicIslandPro;component/LiquidGlass/Shaders/GaussianBlurH.ps")
            };
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(TexelSizeProperty);
        }
    }
}
