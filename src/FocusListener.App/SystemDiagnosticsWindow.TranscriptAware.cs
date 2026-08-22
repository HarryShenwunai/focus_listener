using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FocusListener.App;

internal sealed class SystemDiagnosticsWindow : Window
{
    private static readonly Brush SurfaceBrush = CreateBrush(0xF7, 0xF9, 0xF7);
    private static readonly Brush PanelBrush = CreateBrush(0xFF, 0xFF, 0xFF);
    private static readonly Brush InkBrush = CreateBrush(0x17, 0x21, 0x1B);
    private static readonly Brush MutedBrush = CreateBrush(0x5B, 0x69, 0x60);
    private static readonly Brush BorderBrush = CreateBrush(0xDC, 0xE5, 0xDE);
    private static readonly Brush AccentBrush = CreateBrush(0x23, 0x7A, 0x57);
    private static readonly Brush InformationBrush = CreateBrush(0xE9, 0xF1, 0xF7);
    private static readonly Brush InformationTextBrush = CreateBrush(0x28, 0x57, 0x75);

    private readonly string? _apiKey;
    private readonly string _outputDirectory;
    private readonly Dictionary<FocusDiagnosticId, DiagnosticRowVisual> _rows = [];
    private readonly TextBlock _headline = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _route = new();
    private readonly ProgressBar _microphoneMeter = new();
    private readonly ProgressBar _systemMeter = new();
    private readonly TextBlock _microphoneDetail = new();
    private readonly TextBlock _systemDetail = new();
    private readonly TextBlock _transcript = new();
    private readonly TextBlock _questionStem = new();
    private readonly StackPanel _questionChoices = new();
    private readonly TextBlock _questionEvidence = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private CancellationTokenSource? _runLifetime;
    private bool _closed;

    public SystemDiagnosticsWindow(string? apiKey, string outputDirectory)
    {
        _apiKey = apiKey;
        _outputDirectory = outputDirectory;
        Title = "Focus Listener · 一键系统检测";
        Width = 720;
        Height = 780;
        MinWidth = 620;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Background = SurfaceBrush;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Foreground = InkBrush;
        Content = BuildContent();
        Closed += Window_Closed;
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel();
        _headline.Text = "一键系统检测";
        _headline.FontSize = 26;
        _headline.FontWeight = FontWeights.SemiBold;
        _headline.Foreground = InkBrush;
        header.Children.Add(_headline);
        header.Children.Add(new TextBlock
        {
            Text = "从音频采集一路检查到本地导出，每项都会给出可恢复的结果。",
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = MutedBrush,
            FontSize = 13
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var scroll = new ScrollViewer
        {
            Margin = new Thickness(0, 18, 0, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var body = new StackPanel();
        body.Children.Add(BuildInstruction());
        body.Children.Add(BuildAudioPanel());
        body.Children.Add(SectionTitle("逐项结果"));
        body.Children.Add(BuildResultsPanel());
        body.Children.Add(SectionTitle("实时转写预览"));
        body.Children.Add(BuildTranscriptPanel());
        body.Children.Add(SectionTitle("测试题生成结果"));
        body.Children.Add(BuildQuestionPanel());
        scroll.Content = body;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _summary.Text = "准备就绪 · 整个检测约需 20–35 秒";
        _summary.Foreground = MutedBrush;
        _summary.FontSize = 12;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.TextWrapping = TextWrapping.Wrap;
        footer.Children.Add(_summary);

        _stopButton.Content = "停止";
        _stopButton.IsEnabled = false;
        _stopButton.Margin = new Thickness(12, 0, 8, 0);
        _stopButton.Padding = new Thickness(18, 10, 18, 10);
        _stopButton.Background = CreateBrush(0xEE, 0xF2, 0xEF);
        _stopButton.Foreground = InkBrush;
        _stopButton.Click += Stop_Click;
        Grid.SetColumn(_stopButton, 1);
        footer.Children.Add(_stopButton);

        _startButton.Content = "开始一键检测";
        _startButton.Padding = new Thickness(20, 10, 20, 10);
        _startButton.Background = AccentBrush;
        _startButton.Foreground = Brushes.White;
        _startButton.FontWeight = FontWeights.SemiBold;
        _startButton.Click += Start_Click;
        Grid.SetColumn(_startButton, 2);
        footer.Children.Add(_startButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static Border BuildInstruction()
    {
        var text = new TextBlock
        {
            Text = "点击开始后，请在 10 秒音频检测期间清晰朗读一两句包含完整知识关系的课堂内容。\n测试题只会根据本次实时转写和正式课堂规则生成；没有合格转写时会显示具体原因，不会用固定素材代替。\n要测试系统声音，请同时让电脑播放相关的有声内容。",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Foreground = InformationTextBrush,
            FontSize = 13
        };
        return new Border
        {
            Background = InformationBrush,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 13, 16, 13),
            Child = text
        };
    }

    private Border BuildAudioPanel()
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var microphone = BuildMeter("麦克风", _microphoneMeter, _microphoneDetail);
        panel.Children.Add(microphone);
        var system = BuildMeter("系统声音", _systemMeter, _systemDetail);
        Grid.SetColumn(system, 2);
        panel.Children.Add(system);

        var routePanel = new StackPanel { Margin = new Thickness(0, 15, 0, 0) };
        routePanel.Children.Add(new TextBlock
        {
            Text = "当前采用",
            Foreground = MutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        _route.Text = "等待音频检测";
        _route.Margin = new Thickness(0, 4, 0, 0);
        _route.FontSize = 14;
        _route.FontWeight = FontWeights.SemiBold;
        _route.Foreground = InkBrush;
        _route.TextWrapping = TextWrapping.Wrap;
        routePanel.Children.Add(_route);
        Grid.SetRow(routePanel, 1);
        Grid.SetColumnSpan(routePanel, 3);
        panel.Children.Add(routePanel);

        return new Border
        {
            Margin = new Thickness(0, 14, 0, 0),
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = panel
        };
    }

    private static StackPanel BuildMeter(string title, ProgressBar meter, TextBlock detail)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush
        });
        meter.Minimum = 0;
        meter.Maximum = 100;
        meter.Height = 8;
        meter.Margin = new Thickness(0, 9, 0, 7);
        meter.BorderThickness = new Thickness(0);
        meter.Background = CreateBrush(0xE4, 0xE9, 0xE5);
        meter.Foreground = AccentBrush;
        panel.Children.Add(meter);
        detail.Text = "等待检测";
        detail.FontSize = 11;
        detail.Foreground = MutedBrush;
        detail.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(detail);
        return panel;
    }

    private Border BuildResultsPanel()
    {
        var stack = new StackPanel();
        var ids = Enum.GetValues<FocusDiagnosticId>();
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var row = BuildDiagnosticRow(id, index < ids.Length - 1);
            stack.Children.Add(row.Container);
            _rows.Add(id, row);
        }

        return new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = stack
        };
    }

    private static DiagnosticRowVisual BuildDiagnosticRow(FocusDiagnosticId id, bool divider)
    {
        var grid = new Grid { Margin = new Thickness(15, 11, 15, 11) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var statusText = new TextBlock
        {
            Text = "等待",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var status = new Border
        {
            Width = 44,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = CreateBrush(0xEE, 0xF2, 0xEF),
            Child = statusText,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        grid.Children.Add(status);

        var title = new TextBlock
        {
            Text = DiagnosticTitle(id),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = InkBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var detail = new TextBlock
        {
            Text = "等待检测",
            FontSize = 12,
            Foreground = MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var container = new Border
        {
            BorderBrush = BorderBrush,
            BorderThickness = divider ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            Child = grid
        };
        return new DiagnosticRowVisual(container, status, statusText, title, detail);
    }

    private Border BuildTranscriptPanel()
    {
        _transcript.Text = "检测开始后，这里会显示 Gemini Live 返回的文字。";
        _transcript.Foreground = MutedBrush;
        _transcript.FontSize = 13;
        _transcript.TextWrapping = TextWrapping.Wrap;
        _transcript.LineHeight = 21;
        return new Border
        {
            MinHeight = 74,
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 13, 16, 13),
            Child = _transcript
        };
    }

    private Border BuildQuestionPanel()
    {
        var panel = new StackPanel();
        _questionStem.Text = "检测开始后，这里会显示根据本次实时转写生成的无计算三选一题。";
        _questionStem.Foreground = MutedBrush;
        _questionStem.FontSize = 14;
        _questionStem.FontWeight = FontWeights.SemiBold;
        _questionStem.TextWrapping = TextWrapping.Wrap;
        _questionStem.LineHeight = 22;
        panel.Children.Add(_questionStem);
        _questionChoices.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_questionChoices);
        _questionEvidence.Margin = new Thickness(0, 10, 0, 0);
        _questionEvidence.Foreground = MutedBrush;
        _questionEvidence.FontSize = 12;
        _questionEvidence.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_questionEvidence);
        return new Border
        {
            MinHeight = 88,
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 13, 16, 13),
            Child = panel
        };
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 20, 0, 9),
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Foreground = InkBrush
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_runLifetime is not null)
        {
            return;
        }

        _runLifetime = new CancellationTokenSource();
        _startButton.IsEnabled = false;
        _startButton.Content = "检测中…";
        _stopButton.IsEnabled = true;
        _summary.Text = "检测进行中 · 请保持此窗口打开";
        ResetPreviews();

        var options = string.IsNullOrWhiteSpace(_apiKey) ? null : new GeminiFocusOptions(_apiKey);
        var diagnostics = TranscriptAwareFocusDiagnosticsFactory.Create(options, _outputDirectory);
        try
        {
            var result = await diagnostics.RunAsync(
                new Progress<FocusDiagnosticsView>(Render),
                _runLifetime.Token);
            _summary.Text = result.Failed > 0
                ? $"完成 · {result.Passed} 项正常，{result.Warnings} 项提醒，{result.Failed} 项失败"
                : $"完成 · {result.Passed} 项正常，{result.Warnings} 项提醒";
        }
        catch (Exception exception)
        {
            _summary.Text = $"检测窗口发生错误（{exception.GetType().Name}），请重新打开后再试";
        }
        finally
        {
            _runLifetime.Dispose();
            _runLifetime = null;
            if (!_closed)
            {
                _startButton.IsEnabled = true;
                _startButton.Content = "重新检测";
                _stopButton.IsEnabled = false;
            }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stopButton.IsEnabled = false;
        _summary.Text = "正在停止检测…";
        _runLifetime?.Cancel();
    }

    private void Render(FocusDiagnosticsView view)
    {
        if (_closed)
        {
            return;
        }

        _headline.Text = view.Headline;
        foreach (var item in view.Items)
        {
            if (!_rows.TryGetValue(item.Id, out var visual))
            {
                continue;
            }

            visual.Title.Text = item.Title;
            visual.Detail.Text = item.Preview is { Length: > 0 } &&
                                 item.Id is FocusDiagnosticId.SqliteWrite or FocusDiagnosticId.CsvExport
                ? $"{item.Detail}\n{item.Preview}"
                : item.Detail;
            ApplyState(visual, item.State);

            if (item.Id == FocusDiagnosticId.MicrophoneLevel)
            {
                _microphoneMeter.Value = (item.Level ?? 0) * 100;
                _microphoneDetail.Text = item.Detail;
            }
            else if (item.Id == FocusDiagnosticId.SystemSoundLevel)
            {
                _systemMeter.Value = (item.Level ?? 0) * 100;
                _systemDetail.Text = item.Detail;
            }
            else if (item.Id == FocusDiagnosticId.AudioRoute)
            {
                _route.Text = item.Detail;
            }
        }

        if (!string.IsNullOrWhiteSpace(view.TranscriptPreview))
        {
            _transcript.Text = view.TranscriptPreview;
            _transcript.Foreground = InkBrush;
        }

        if (view.Question is { } question)
        {
            _questionStem.Text = question.Stem;
            _questionStem.Foreground = InkBrush;
            _questionChoices.Children.Clear();
            foreach (var choice in question.Choices)
            {
                _questionChoices.Children.Add(new TextBlock
                {
                    Text = choice,
                    Margin = new Thickness(0, 3, 0, 0),
                    Foreground = InkBrush,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                });
            }

            _questionEvidence.Text = $"课堂证据：{question.Evidence}";
        }
        else
        {
            var questionItem = view.Items.Single(item => item.Id == FocusDiagnosticId.QuestionGeneration);
            if (questionItem.State is FocusDiagnosticState.Warning or
                FocusDiagnosticState.Failed or FocusDiagnosticState.Skipped)
            {
                _questionStem.Text = questionItem.Detail;
                _questionStem.Foreground = MutedBrush;
                _questionChoices.Children.Clear();
                _questionEvidence.Text = string.Empty;
            }
        }
    }

    private void ResetPreviews()
    {
        _transcript.Text = "正在等待 Gemini Live 返回文字…";
        _transcript.Foreground = MutedBrush;
        _questionStem.Text = "等待根据本次实时转写生成题目…";
        _questionStem.Foreground = MutedBrush;
        _questionChoices.Children.Clear();
        _questionEvidence.Text = string.Empty;
        _microphoneMeter.Value = 0;
        _systemMeter.Value = 0;
    }

    private static void ApplyState(DiagnosticRowVisual visual, FocusDiagnosticState state)
    {
        var (label, background, foreground) = state switch
        {
            FocusDiagnosticState.Running => ("检测", CreateBrush(0xE9, 0xF1, 0xF7), CreateBrush(0x28, 0x57, 0x75)),
            FocusDiagnosticState.Passed => ("正常", CreateBrush(0xE4, 0xF3, 0xEB), CreateBrush(0x1D, 0x6B, 0x4B)),
            FocusDiagnosticState.Warning => ("提醒", CreateBrush(0xFF, 0xF1, 0xCD), CreateBrush(0x76, 0x52, 0x0E)),
            FocusDiagnosticState.Failed => ("失败", CreateBrush(0xF9, 0xE7, 0xE5), CreateBrush(0x8B, 0x3D, 0x38)),
            FocusDiagnosticState.Skipped => ("跳过", CreateBrush(0xEE, 0xF0, 0xEF), CreateBrush(0x64, 0x6B, 0x67)),
            _ => ("等待", CreateBrush(0xEE, 0xF2, 0xEF), MutedBrush)
        };
        visual.StatusText.Text = label;
        visual.Status.Background = background;
        visual.StatusText.Foreground = foreground;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _runLifetime?.Cancel();
    }

    private static string DiagnosticTitle(FocusDiagnosticId id) => id switch
    {
        FocusDiagnosticId.MicrophoneLevel => "麦克风音量",
        FocusDiagnosticId.SystemSoundLevel => "系统声音量",
        FocusDiagnosticId.AudioRoute => "当前采用音频",
        FocusDiagnosticId.GeminiApiKey => "Gemini Key",
        FocusDiagnosticId.GeminiLive => "Gemini Live",
        FocusDiagnosticId.LiveTranscription => "实时转写",
        FocusDiagnosticId.QuestionGeneration => "测试题生成",
        FocusDiagnosticId.SqliteWrite => "SQLite 写入",
        FocusDiagnosticId.CsvExport => "CSV 导出",
        _ => id.ToString()
    };

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue) =>
        new(Color.FromRgb(red, green, blue));

    private sealed record DiagnosticRowVisual(
        Border Container,
        Border Status,
        TextBlock StatusText,
        TextBlock Title,
        TextBlock Detail);
}
