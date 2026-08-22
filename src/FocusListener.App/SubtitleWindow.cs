using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class SubtitleWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long TransparentStyle = 0x00000020L;
    private const long NoActivateStyle = 0x08000000L;
    private const long ToolWindowStyle = 0x00000080L;

    private readonly Border _surface = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _committed = new();
    private readonly TextBlock _interim = new();
    private LiveTranscriptPreview? _latest;
    private bool _locked = true;
    private bool _sourceReady;
    private bool _highlightingEvidence;

    public SubtitleWindow()
    {
        Title = "Focus Listener 字幕";
        Width = 820;
        Height = 150;
        MinWidth = 360;
        MinHeight = 90;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Content = BuildContent();
        SourceInitialized += (_, _) =>
        {
            _sourceReady = true;
            ApplyInteractionStyle();
        };
        MouseLeftButtonDown += (_, eventArgs) =>
        {
            if (!_locked && eventArgs.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public bool IsLocked => _locked;

    public void ApplySettings(FocusInteractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Width = settings.SubtitleWidth;
        Height = settings.SubtitleHeight;
        _committed.FontSize = settings.SubtitleFontSize;
        _interim.FontSize = Math.Max(18, settings.SubtitleFontSize - 2);
        _committed.LineHeight = Math.Round(settings.SubtitleFontSize * 1.32);
        _interim.LineHeight = Math.Round(Math.Max(18, settings.SubtitleFontSize - 2) * 1.32);
        _surface.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(settings.SubtitleBackgroundOpacity * 255),
            0x0B,
            0x12,
            0x0E));
        SetLocked(settings.SubtitleClickThrough);
        Place(settings.SubtitleLeft, settings.SubtitleTop);
    }

    public FocusInteractionSettings CaptureSettings(FocusInteractionSettings settings) => settings with
    {
        SubtitleLeft = Left,
        SubtitleTop = Top,
        SubtitleWidth = ActualWidth > 0 ? ActualWidth : Width,
        SubtitleHeight = ActualHeight > 0 ? ActualHeight : Height,
        SubtitleClickThrough = _locked
    };

    public void Render(LiveTranscriptPreview preview)
    {
        _latest = preview;
        if (_highlightingEvidence)
        {
            return;
        }

        _status.Text = preview.Status;
        _committed.Text = Tail(preview.CommittedText, 360);
        _interim.Text = Tail(preview.InterimText, 160);
        _interim.Visibility = string.IsNullOrWhiteSpace(_interim.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void HighlightEvidence(string evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return;
        }

        _highlightingEvidence = true;
        _status.Text = "本题依据 · 后台仍在继续转写";
        _committed.Text = evidence.Trim();
        _interim.Text = string.Empty;
        _interim.Visibility = Visibility.Collapsed;
        _surface.BorderBrush = new SolidColorBrush(Color.FromRgb(0x72, 0xD0, 0x9F));
        _surface.BorderThickness = new Thickness(2);
    }

    public void ResumeLatest()
    {
        _highlightingEvidence = false;
        _surface.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 220, 229, 222));
        _surface.BorderThickness = new Thickness(1);
        if (_latest is not null)
        {
            Render(_latest);
        }
    }

    public void Clear()
    {
        _latest = null;
        _highlightingEvidence = false;
        _status.Text = "等待课堂声音…";
        _committed.Text = string.Empty;
        _interim.Text = string.Empty;
        _interim.Visibility = Visibility.Collapsed;
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
        ApplyInteractionStyle();
    }

    private UIElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _status.Text = "等待课堂声音…";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xC0, 0xB2));
        _status.FontSize = 11;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        root.Children.Add(_status);

        var text = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        _committed.Foreground = Brushes.White;
        _committed.FontSize = 28;
        _committed.FontWeight = FontWeights.SemiBold;
        _committed.TextWrapping = TextWrapping.Wrap;
        _committed.LineHeight = 37;
        _committed.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(_committed);
        _interim.Foreground = new SolidColorBrush(Color.FromArgb(185, 220, 229, 222));
        _interim.FontSize = 26;
        _interim.FontStyle = FontStyles.Italic;
        _interim.TextWrapping = TextWrapping.Wrap;
        _interim.Margin = new Thickness(0, 3, 0, 0);
        text.Children.Add(_interim);
        Grid.SetRow(text, 1);
        root.Children.Add(text);

        _surface.Padding = new Thickness(22, 13, 22, 15);
        _surface.CornerRadius = new CornerRadius(16);
        _surface.Background = new SolidColorBrush(Color.FromArgb(148, 0x0B, 0x12, 0x0E));
        _surface.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 220, 229, 222));
        _surface.BorderThickness = new Thickness(1);
        _surface.Child = root;
        return _surface;
    }

    private void Place(double? requestedLeft, double? requestedTop)
    {
        var left = requestedLeft ?? (SystemParameters.WorkArea.Left +
            Math.Max(12, (SystemParameters.WorkArea.Width - Width) / 2));
        var top = requestedTop ?? Math.Max(
            SystemParameters.WorkArea.Top + 12,
            SystemParameters.WorkArea.Bottom - Height - 42);
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        if (left + Width < virtualLeft || left > virtualRight || top + Height < virtualTop || top > virtualBottom)
        {
            left = SystemParameters.WorkArea.Left + Math.Max(12, (SystemParameters.WorkArea.Width - Width) / 2);
            top = Math.Max(SystemParameters.WorkArea.Top + 12, SystemParameters.WorkArea.Bottom - Height - 42);
        }

        Left = Math.Clamp(left, virtualLeft, Math.Max(virtualLeft, virtualRight - Width));
        Top = Math.Clamp(top, virtualTop, Math.Max(virtualTop, virtualBottom - Height));
    }

    private void ApplyInteractionStyle()
    {
        if (!_sourceReady)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        style |= ToolWindowStyle;
        if (_locked)
        {
            style |= TransparentStyle | NoActivateStyle;
        }
        else
        {
            style &= ~TransparentStyle;
            style &= ~NoActivateStyle;
        }

        SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(style));
    }

    private static string Tail(string text, int maximum)
    {
        var normalized = text.Trim();
        if (normalized.Length <= maximum)
        {
            return normalized;
        }

        var tail = normalized[^maximum..];
        var boundary = tail.IndexOfAny(['。', '！', '？', '.', '!', '?']);
        return boundary >= 0 && boundary + 1 < tail.Length ? tail[(boundary + 1)..].TrimStart() : tail;
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(handle, index)
        : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(handle, index, value)
        : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);
}
