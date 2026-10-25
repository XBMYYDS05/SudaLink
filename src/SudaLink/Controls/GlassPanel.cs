using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace SudaLink.Controls;

/// <summary>
/// 液态玻璃面板。
/// <para>
/// 实现方式：把背景层渲染成一张位图，按面板自身在窗口中的位置裁出同样的区域，
/// 高斯模糊后贴回来 —— 所以面板里看到的是“被磨掉细节的真实背景”，
/// 而不是一层半透明色块。染色只保留极薄一层，靠边缘高光表达材质。
/// </para>
/// </summary>
public class GlassPanel : ContentControl
{
    private static readonly Brush DefaultFill = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly double[] GlowAlphas = { 0x18, 0x08, 0x00 };
    private static readonly double[] GlowOffsets = { 0.0, 0.45, 1.0 };

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel),
        new PropertyMetadata(new CornerRadius(22), (d, _) => ((GlassPanel)d).ApplyClip()));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GlassPanel), new PropertyMetadata(DefaultFill));

    public static readonly DependencyProperty SpecularProperty = DependencyProperty.Register(
        nameof(Specular), typeof(bool), typeof(GlassPanel), new PropertyMetadata(true));

    public static readonly DependencyProperty ShadowProperty = DependencyProperty.Register(
        nameof(Shadow), typeof(Effect), typeof(GlassPanel), new PropertyMetadata(null));

    /// <summary>被取样的背景层。</summary>
    public static readonly DependencyProperty BackdropProperty = DependencyProperty.Register(
        nameof(Backdrop), typeof(FrameworkElement), typeof(GlassPanel),
        new PropertyMetadata(null, (d, _) => ((GlassPanel)d).UpdateFrost(force: true)));

    /// <summary>高斯模糊半径。0 表示关闭（回落为纯半透明面板）。</summary>
    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(
        nameof(Blur), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(14.0, (d, _) =>
        {
            var panel = (GlassPanel)d;
            panel.ApplyBlur();
            panel.UpdateFrost(force: true);
        }));

    /// <summary>
    /// 边缘折射强度。略小于 1 的取样范围会让玻璃里的画面被轻微放大，
    /// 像厚玻璃的透镜效果 —— 这是 iOS 液态玻璃最容易被识别的一个特征。
    /// </summary>
    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register(
        nameof(Refraction), typeof(double), typeof(GlassPanel),
        new PropertyMetadata(1.0, (d, _) => ((GlassPanel)d).UpdateFrost(force: true)));

    /// <summary>面板内的语义辉光色；透明色表示不发光。</summary>
    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(
        nameof(Glow), typeof(Color), typeof(GlassPanel),
        new PropertyMetadata(Colors.Transparent, (d, e) => ((GlassPanel)d).ApplyGlow((Color)e.NewValue)));

    private Grid? _root;
    private Border? _frost;
    private ImageBrush? _frostBrush;
    private Rect? _frostViewbox;
    private FrameworkElement? _specular;
    private TranslateTransform? _shift;
    private Ellipse? _glow;
    private readonly GradientStop[] _glowStops = new GradientStop[3];
    private bool _placed;

    static GlassPanel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(GlassPanel),
            new FrameworkPropertyMetadata(typeof(GlassPanel)));
    }

    private static event Action? RefreshRequested;

    /// <summary>背景或主题变化后调用，让所有面板重新取背景。</summary>
    public static void InvalidateBackdrop() => RefreshRequested?.Invoke();

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool Specular
    {
        get => (bool)GetValue(SpecularProperty);
        set => SetValue(SpecularProperty, value);
    }

    public Effect? Shadow
    {
        get => (Effect?)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    public Color Glow
    {
        get => (Color)GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    public FrameworkElement? Backdrop
    {
        get => (FrameworkElement?)GetValue(BackdropProperty);
        set => SetValue(BackdropProperty, value);
    }

    public double Blur
    {
        get => (double)GetValue(BlurProperty);
        set => SetValue(BlurProperty, value);
    }

    public double Refraction
    {
        get => (double)GetValue(RefractionProperty);
        set => SetValue(RefractionProperty, value);
    }

    /// <summary>模糊会把边缘吃掉，所以采样区域要外扩这么多，再用圆角裁掉。</summary>
    private double Bleed => Math.Ceiling(Blur * 2.2) + 6;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _root = GetTemplateChild("PART_Root") as Grid;
        _frost = GetTemplateChild("PART_Frost") as Border;
        _specular = GetTemplateChild("PART_Specular") as FrameworkElement;
        _shift = GetTemplateChild("PART_SpecularShift") as TranslateTransform;
        _glow = GetTemplateChild("PART_Glow") as Ellipse;

        ApplyBlur();
        ApplyClip();
        UpdateFrost(force: true);
        BuildGlow();

        RefreshRequested -= OnBackdropInvalidated;
        RefreshRequested += OnBackdropInvalidated;
        LayoutUpdated -= OnLayoutUpdated;
        LayoutUpdated += OnLayoutUpdated;
        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RefreshRequested -= OnBackdropInvalidated;
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateFrost();

    /// <summary>背景换了：立刻试一次，再等一轮布局（壁纸尺寸/位置此时才确定）。</summary>
    private void OnBackdropInvalidated()
    {
        UpdateFrost(force: true);
        Dispatcher.BeginInvoke(new Action(() => UpdateFrost(force: true)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyBlur()
    {
        if (_frost is null)
            return;

        _frost.Effect = Blur <= 0
            ? null
            : new BlurEffect
            {
                Radius = Blur,
                KernelType = KernelType.Gaussian,
                RenderingBias = RenderingBias.Performance
            };
    }

    /// <summary>按自身在背景中的位置裁取背景位图，得到 1:1 的“透过玻璃”画面。</summary>
    private void UpdateFrost(bool force = false)
    {
        if (_frost is null || FrostDisabled())
            return;

        if (Backdrop is not Image { Source: { } source } || source.Width < 1)
            return;

        Point offset;
        try
        {
            offset = TransformToVisual(Backdrop).Transform(new Point(0, 0));
        }
        catch (InvalidOperationException)
        {
            return; // 还没挂到同一棵可视树
        }

        var bleed = Bleed;
        // 面板在“背景坐标系”里的矩形（外扩 bleed 抵消模糊吃边）
        var pane = new Rect(offset.X - bleed, offset.Y - bleed,
            ActualWidth + bleed * 2, ActualHeight + bleed * 2);

        // 把背景坐标系换算成壁纸的像素坐标：UniformToFill 居中裁切
        var area = new Size(Backdrop.ActualWidth, Backdrop.ActualHeight);
        if (area.Width < 1 || area.Height < 1)
            return;

        var scale = Math.Max(area.Width / source.Width, area.Height / source.Height);
        var originX = (area.Width - source.Width * scale) / 2;
        var originY = (area.Height - source.Height * scale) / 2;

        var rect = new Rect(
            (pane.X - originX) / scale,
            (pane.Y - originY) / scale,
            pane.Width / scale,
            pane.Height / scale);

        // 折射：取样范围收一点点，玻璃内的画面就会被轻微放大
        var refraction = Math.Clamp(Refraction, 0.9, 1.0);
        if (refraction < 0.999)
        {
            var insetX = rect.Width * (1 - refraction) / 2;
            var insetY = rect.Height * (1 - refraction) / 2;
            rect = new Rect(rect.X + insetX, rect.Y + insetY,
                rect.Width - insetX * 2, rect.Height - insetY * 2);
        }

        if (_frostBrush is null || !ReferenceEquals(_frostBrush.ImageSource, source))
        {
            _frostBrush = new ImageBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            _frost.Margin = new Thickness(-bleed);
            _frost.Background = _frostBrush;
            _frostViewbox = null;
        }

        if (!force && _frostViewbox is { } old &&
            Math.Abs(old.X - rect.X) < 0.35 && Math.Abs(old.Y - rect.Y) < 0.35 &&
            Math.Abs(old.Width - rect.Width) < 0.35)
            return;

        _frost.Margin = new Thickness(-bleed);
        _frostBrush.Viewbox = rect;
        _frostViewbox = rect;
    }

    /// <summary>
    /// 真正的圆角裁剪。Border.CornerRadius + ClipToBounds 只按矩形裁，
    /// 磨砂层会在圆角外露出方角，只剩一道圆角描边，所以必须用几何 Clip。
    /// </summary>
    private void ApplyClip()
    {
        if (_root is null || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(ActualWidth, ActualHeight) / 2);
        _root.Clip = new RectangleGeometry(
            new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
    }

    private bool FrostDisabled() =>
        Backdrop is null || Blur <= 0 || ActualWidth < 1 || ActualHeight < 1 || !IsVisible;

    private void BuildGlow()
    {
        if (_glow is null)
            return;

        var brush = new RadialGradientBrush();
        for (var i = 0; i < _glowStops.Length; i++)
        {
            _glowStops[i] = new GradientStop(Colors.Transparent, GlowOffsets[i]);
            brush.GradientStops.Add(_glowStops[i]);
        }

        _glow.Fill = brush;
        ApplyGlow(Glow);
    }

    private void ApplyGlow(Color color)
    {
        if (_glowStops[0] is null || _glow is null)
            return;

        for (var i = 0; i < _glowStops.Length; i++)
            _glowStops[i].Color = Color.FromArgb((byte)GlowAlphas[i], color.R, color.G, color.B);

        _glow.Opacity = color.A == 0 ? 0 : 1;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyClip();
        UpdateFrost(force: true);

        if (!_placed)
        {
            _placed = true;
            MoveTo(ActualWidth * 0.42, ActualHeight * 0.52, 0);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Specular)
            return;

        var point = e.GetPosition(this);
        MoveTo(point.X, point.Y, 240);
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (Specular)
            FadeSpecular(0.55, 260);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        MoveTo(ActualWidth * 0.42, ActualHeight * 0.52, 560);
        FadeSpecular(0, 560);
    }

    /// <summary>镜面只在指针悬停时浮现，静态时保持完全干净。</summary>
    private void FadeSpecular(double target, int milliseconds)
    {
        if (_specular is null)
            return;

        _specular.BeginAnimation(OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void MoveTo(double x, double y, int milliseconds)
    {
        if (_specular is null || _shift is null)
            return;

        var radius = _specular.Width / 2;
        var targetX = x - radius;
        var targetY = y - radius;

        if (milliseconds <= 0)
        {
            _shift.X = targetX;
            _shift.Y = targetY;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
