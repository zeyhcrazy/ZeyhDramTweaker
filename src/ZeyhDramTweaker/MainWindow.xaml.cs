using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ZeyhDramTweaker.Core.Localization;
using ZeyhDramTweaker.ViewModels;

namespace ZeyhDramTweaker;

public partial class MainWindow : Window
{
    MainViewModel Vm => (MainViewModel)DataContext;
    int _tab;

    public MainWindow()
    {
        InitializeComponent();
        Vm.SavePngRequested += (_, _) => SavePng();
        Vm.ShowNotesRequested += (_, _) => { if (Tabs.Items.Count > 0) Tabs.SelectedIndex = Tabs.Items.Count - 1; };   // Notes is always the last tab
        HeroGrid.SizeChanged += (_, _) => UpdateHeroLayout();
        SizeChanged += (_, _) => IsShort = ActualHeight < 800;
        // keep the same tab when the result is recalculated (every input change rebuilds the tabs)
        Tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == Tabs && Tabs.SelectedIndex >= 0) _tab = Tabs.SelectedIndex; };
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Result)) return;
            int keep = _tab;
            Dispatcher.InvokeAsync(() => { Tabs.SelectedIndex = Math.Min(keep, Tabs.Items.Count - 1); UpdateHeroLayout(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        SourceInitialized += (_, _) => Backdrop();
        // WindowChrome lets a maximized window overhang the screen by the frame width: pull the content back in
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
    }

    public static readonly DependencyProperty IsShortProperty = DependencyProperty.Register(nameof(IsShort), typeof(bool), typeof(MainWindow));
    /// <summary>Short window (laptop, half screen): page subtitles hide so the tables keep their rows.</summary>
    public bool IsShort { get => (bool)GetValue(IsShortProperty); set => SetValue(IsShortProperty, value); }

    /// <summary>
    /// The gauges sit beside the big timings while they fit; on a narrow column they move to their own row under
    /// the details (the action buttons already wrap on their own).
    /// </summary>
    void UpdateHeroLayout()
    {
        double inner = HeroGrid.ActualWidth;
        if (inner <= 0) return;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        HeroKit.Measure(inf);
        HeroGauges.Measure(inf);
        // beside the timings while the big numbers still fit next to them (the details line may wrap to two lines)
        bool narrow = HeroKit.DesiredSize.Width + 40 + HeroGauges.DesiredSize.Width > inner;
        Grid.SetRow(HeroGauges, narrow ? 2 : 1);
        Grid.SetColumn(HeroGauges, narrow ? 0 : 1);
        Grid.SetColumnSpan(HeroGauges, narrow ? 3 : 1);
        HeroGauges.Margin = narrow ? new Thickness(-22, 16, 0, 0) : new Thickness(24, 16, 0, 0);   // the first gauge's own 22 px indent
    }

    /// <summary>
    /// Windows 11 22H2+: dark Mica behind the whole window (title bar and nav rail sit on it, the content on a layer).
    /// Older systems keep the solid background and only get the dark caption.
    /// </summary>
    void Backdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);                 // DWMWA_USE_IMMERSIVE_DARK_MODE
        if (Environment.OSVersion.Version.Build < 22621) return;
        int mica = 2;                                               // DWMSBT_MAINWINDOW
        if (DwmSetWindowAttribute(hwnd, 38, ref mica, 4) != 0) return; // DWMWA_SYSTEMBACKDROP_TYPE
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } ct }) ct.BackgroundColor = Colors.Transparent;
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
        Background = Brushes.Transparent;
    }

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void SavePng()
    {
        var dlg = new SaveFileDialog { Filter = "PNG (*.png)|*.png", FileName = "zeyh-dram-tweaker.png" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllBytes(dlg.FileName, RenderReport(2.0));
        Vm.Status = Loc.Instance.Format("ui.saved", dlg.FileName);
    }

    /// <summary>The whole recommendation (every tab, not just the visible one) laid out as one sheet.</summary>
    byte[] RenderReport(double scale)
    {
        var r = Vm.Result!;
        var sheet = new StackPanel { Background = (Brush)FindResource("BgBrush"), Width = 1180 };
        var facts = Vm.ReportFacts().ToList();
        // header: app + date, the recommendation in one line, then who it was made for (CPU, board, memory, inputs)
        var top = new DockPanel { Margin = new Thickness(28, 24, 28, 0) };
        var date = MakeText(facts[0].Value, 12.5, FontWeights.Normal, new Thickness(0), true);
        DockPanel.SetDock(date, Dock.Right);
        top.Children.Add(date);
        top.Children.Add(MakeText("Zeyh DRAM Tweaker", 14, FontWeights.SemiBold, new Thickness(0)));
        sheet.Children.Add(top);
        var kit = MakeText($"{r.Speed}   {r.Kit}   {r.PresetLabel}", 26, FontWeights.SemiBold, new Thickness(28, 10, 28, 0));
        kit.FontFamily = (FontFamily)FindResource("DisplayFont");
        sheet.Children.Add(kit);
        AddText(sheet, string.Join("     ", r.Metrics.Skip(2).Select(m => $"{m.Label} {m.Value}{(m.Sub != null ? " (" + m.Sub + ")" : "")}")), 12.5, FontWeights.Normal, new Thickness(28, 4, 28, 14), true);
        var grid = new Grid { Margin = new Thickness(28, 0, 28, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (label, value) in facts.Skip(1))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = MakeText(label, 12.5, FontWeights.Normal, new Thickness(0, 2, 12, 2), true);
            var v = MakeText(value, 12.5, FontWeights.Normal, new Thickness(0, 2, 0, 2));
            Grid.SetRow(l, grid.RowDefinitions.Count - 1); Grid.SetRow(v, grid.RowDefinitions.Count - 1); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
        }
        sheet.Children.Add(grid);
        sheet.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("LineBrush"), Margin = new Thickness(28, 0, 28, 22) });
        foreach (var tab in r.Tabs.Where(t => t.Sections.Count > 0))
        {
            var wrap = new WrapPanel { Margin = new Thickness(18, 0, 0, 0) };
            foreach (var sec in tab.Sections)
                wrap.Children.Add(new ContentControl { Content = sec, ContentTemplate = (DataTemplate)FindResource("TableTemplate") });
            sheet.Children.Add(wrap);
        }
        var notes = new ItemsControl { ItemsSource = r.Notes, ItemTemplate = (DataTemplate)FindResource("NoticeTemplate"), Margin = new Thickness(28, 0, 28, 24) };
        sheet.Children.Add(notes);
        // render outside the window's tree, with its resources
        var host = new Border { Child = sheet, Resources = Resources };
        TextElement.SetForeground(host, (Brush)FindResource("TextBrush"));
        TextElement.SetFontFamily(host, (FontFamily)FindResource("UiFont"));
        TextElement.SetFontSize(host, 13);
        host.Measure(new Size(1180, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        return Render(host, scale);
    }

    void AddText(Panel p, string text, double size, FontWeight weight, Thickness margin, bool muted = false) =>
        p.Children.Add(MakeText(text, size, weight, margin, muted));

    TextBlock MakeText(string text, double size, FontWeight weight, Thickness margin, bool muted = false) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Margin = margin, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)FindResource(muted ? "MutedBrush" : "TextBrush"), FontFamily = (FontFamily)FindResource("UiFont"),
    };

    static byte[] Render(FrameworkElement el, double scale)
    {
        int w = (int)Math.Ceiling(el.ActualWidth * scale), h = (int)Math.Ceiling(el.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.Resources["BgBrush"], null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            // map the element's layout box 1:1 (the default viewbox is its ink bounds, which shifts/stretches it)
            var vb = new VisualBrush(el) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, el.ActualWidth, el.ActualHeight) };
            dc.DrawRectangle(vb, null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>QA capture: the window, or the full exported report when <paramref name="report"/>.</summary>
    public void CaptureTo(string path, bool report, bool config = false, double scale = 1.0)
    {
        UpdateLayout();
        if (config)
        {
            // the configuration column at its full height (it scrolls in the window)
            var sv = (ScrollViewer)ConfigPanel.Parent;
            sv.Content = null;
            var host = new Border { Child = ConfigPanel, Width = 300, Padding = new Thickness(20, 0, 20, 0), Background = (Brush)FindResource("SurfaceBrush"), Resources = Resources };
            TextElement.SetForeground(host, (Brush)FindResource("TextBrush"));
            TextElement.SetFontFamily(host, (FontFamily)FindResource("UiFont"));
            TextElement.SetFontSize(host, 13);
            host.DataContext = DataContext;
            host.Measure(new Size(300, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            File.WriteAllBytes(path, Render(host, 1.0));
            return;
        }
        File.WriteAllBytes(path, report ? RenderReport(scale) : Render(Root, scale));
    }
}
