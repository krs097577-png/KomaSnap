using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

sealed class ReaderColorEffect : ShaderEffect
{
    static readonly PixelShader Shader = LoadShader();
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(
        "Input", typeof(ReaderColorEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty GainProperty = DependencyProperty.Register(
        "Gain", typeof(Point4D), typeof(ReaderColorEffect),
        new UIPropertyMetadata(new Point4D(1, 1, 1, 1), PixelShaderConstantCallback(0)));

    public ReaderColorEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(GainProperty);
    }

    public Brush Input
    {
        get { return (Brush)GetValue(InputProperty); }
        set { SetValue(InputProperty, value); }
    }

    public Point4D Gain
    {
        get { return (Point4D)GetValue(GainProperty); }
        set { SetValue(GainProperty, value); }
    }

    public static ReaderColorEffect FromPalette(Color multiply, double brightness)
    {
        if (multiply == Colors.White && brightness == 1) return null;
        double unit = brightness / 255;
        var effect = new ReaderColorEffect
        {
            Gain = new Point4D(multiply.R * unit, multiply.G * unit, multiply.B * unit, 1)
        };
        effect.Freeze();
        return effect;
    }

    static PixelShader LoadShader()
    {
        var shader = new PixelShader();
        using (var bytes = Assembly.GetExecutingAssembly().GetManifestResourceStream("ReaderColor.ps"))
            shader.SetStreamSource(bytes);
        shader.Freeze();
        return shader;
    }
}

