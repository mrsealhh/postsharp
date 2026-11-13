using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;

namespace PostSharp.Controls;

/// <summary>
/// 响应体、请求体的 JSON 代码视图：按角色着色、可选行号，并内置查找。
/// 只读展示，选中与复制交给 <see cref="SelectableTextBlock"/>。
/// </summary>
public partial class JsonCodeView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<
        JsonCodeView,
        string?
    >(nameof(Text));

    public static readonly StyledProperty<string?> QueryProperty = AvaloniaProperty.Register<
        JsonCodeView,
        string?
    >(nameof(Query), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsSearchVisibleProperty = AvaloniaProperty.Register<
        JsonCodeView,
        bool
    >(nameof(IsSearchVisible), true);

    public static readonly StyledProperty<bool> ShowLineNumbersProperty = AvaloniaProperty.Register<
        JsonCodeView,
        bool
    >(nameof(ShowLineNumbers), true);

    public static readonly StyledProperty<IBrush?> KeyBrushProperty = AvaloniaProperty.Register<
        JsonCodeView,
        IBrush?
    >(nameof(KeyBrush));

    public static readonly StyledProperty<IBrush?> StringBrushProperty = AvaloniaProperty.Register<
        JsonCodeView,
        IBrush?
    >(nameof(StringBrush));

    public static readonly StyledProperty<IBrush?> NumberBrushProperty = AvaloniaProperty.Register<
        JsonCodeView,
        IBrush?
    >(nameof(NumberBrush));

    public static readonly StyledProperty<IBrush?> LiteralBrushProperty = AvaloniaProperty.Register<
        JsonCodeView,
        IBrush?
    >(nameof(LiteralBrush));

    public static readonly StyledProperty<IBrush?> PunctuationBrushProperty =
        AvaloniaProperty.Register<JsonCodeView, IBrush?>(nameof(PunctuationBrush));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<JsonCodeView, IBrush?>(nameof(HighlightBrush));

    private static readonly DirectProperty<JsonCodeView, int> MatchCountProperty =
        AvaloniaProperty.RegisterDirect<JsonCodeView, int>(
            nameof(MatchCount),
            view => view.MatchCount
        );

    private static readonly DirectProperty<JsonCodeView, int> CurrentMatchIndexProperty =
        AvaloniaProperty.RegisterDirect<JsonCodeView, int>(
            nameof(CurrentMatchIndex),
            view => view.CurrentMatchIndex
        );

    private const byte PlainKind = 0;
    private const byte KeyKind = 1;
    private const byte StringKind = 2;
    private const byte NumberKind = 3;
    private const byte LiteralKind = 4;
    private const byte PunctuationKind = 5;

    private readonly List<int> _matches = new();
    private int _currentMatch = -1;
    private int _matchCount;
    private int _currentMatchIndex = -1;
    private string? _lastQuery;
    private bool _syncingQuery;
    private bool _rebuildPosted;
    private double _lineHeight;

    public JsonCodeView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => OnSearchTextChanged();
        PreviousButton.Click += (_, _) => Move(-1);
        NextButton.Click += (_, _) => Move(1);
        ActualThemeVariantChanged += (_, _) => Rebuild();
        Code.PropertyChanged += (_, args) =>
        {
            if (args.Property == FontSizeProperty)
            {
                ApplyMetrics();
            }
        };
    }

    /// <summary>要展示的文本；通常已是缩进好的 JSON。</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>查找关键词，双向绑定后可由外部驱动。</summary>
    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public bool IsSearchVisible
    {
        get => GetValue(IsSearchVisibleProperty);
        set => SetValue(IsSearchVisibleProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public IBrush? KeyBrush
    {
        get => GetValue(KeyBrushProperty);
        set => SetValue(KeyBrushProperty, value);
    }

    public IBrush? StringBrush
    {
        get => GetValue(StringBrushProperty);
        set => SetValue(StringBrushProperty, value);
    }

    public IBrush? NumberBrush
    {
        get => GetValue(NumberBrushProperty);
        set => SetValue(NumberBrushProperty, value);
    }

    public IBrush? LiteralBrush
    {
        get => GetValue(LiteralBrushProperty);
        set => SetValue(LiteralBrushProperty, value);
    }

    public IBrush? PunctuationBrush
    {
        get => GetValue(PunctuationBrushProperty);
        set => SetValue(PunctuationBrushProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>当前关键词的匹配总数。</summary>
    public int MatchCount
    {
        get => _matchCount;
        private set => SetAndRaise(MatchCountProperty, ref _matchCount, value);
    }

    /// <summary>当前命中项的下标（0 开始），没有命中为 -1。</summary>
    public int CurrentMatchIndex
    {
        get => _currentMatchIndex;
        private set => SetAndRaise(CurrentMatchIndexProperty, ref _currentMatchIndex, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScheduleRebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (
            change.Property == TextProperty
            || change.Property == QueryProperty
            || change.Property == KeyBrushProperty
            || change.Property == StringBrushProperty
            || change.Property == NumberBrushProperty
            || change.Property == LiteralBrushProperty
            || change.Property == PunctuationBrushProperty
            || change.Property == HighlightBrushProperty
            || change.Property == ShowLineNumbersProperty
        )
        {
            if (change.Property == QueryProperty)
            {
                SyncSearchBox();
            }

            ScheduleRebuild();
        }
        else if (change.Property == IsSearchVisibleProperty)
        {
            SearchRow.IsVisible = IsSearchVisible;
        }
    }

    private void SyncSearchBox()
    {
        _syncingQuery = true;
        try
        {
            SearchBox.Text = Query ?? string.Empty;
        }
        finally
        {
            _syncingQuery = false;
        }
    }

    private void OnSearchTextChanged()
    {
        if (_syncingQuery)
        {
            return;
        }

        Query = SearchBox.Text ?? string.Empty;
    }

    private void ScheduleRebuild()
    {
        if (_rebuildPosted)
        {
            return;
        }

        _rebuildPosted = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _rebuildPosted = false;
                Rebuild();
            },
            DispatcherPriority.Background
        );
    }

    private void ApplyMetrics()
    {
        _lineHeight = Math.Max(1, Math.Round(Code.FontSize * 1.7));
        Code.LineHeight = _lineHeight;
        Gutter.LineHeight = _lineHeight;
    }

    private void Rebuild()
    {
        SearchRow.IsVisible = IsSearchVisible;
        ApplyMetrics();

        var text = Text ?? string.Empty;
        FindMatches(text);

        var kinds = new byte[text.Length];
        Classify(text, kinds);

        var matches = new bool[text.Length];
        var current = new bool[text.Length];
        for (var index = 0; index < _matches.Count; index++)
        {
            var start = _matches[index];
            var length = Math.Min((Query ?? string.Empty).Length, text.Length - start);
            if (length <= 0)
            {
                continue;
            }

            var target = index == _currentMatch ? current : matches;
            Array.Fill(target, true, start, length);
        }

        BuildInlines(text, kinds, matches, current);
        UpdateGutter(text);
        UpdateSearchState();
    }

    private void FindMatches(string text)
    {
        _matches.Clear();
        var query = Query;
        if (!string.IsNullOrEmpty(query) && text.Length > 0)
        {
            var start = 0;
            while (start <= text.Length - query.Length)
            {
                var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                _matches.Add(index);
                start = index + 1;
                if (_matches.Count >= 5000)
                {
                    break;
                }
            }
        }

        // 只有关键词变化或清空时才回到第一个命中；重建颜色时保留当前位置，否则“下一个/上一个”会被重置。
        if (!string.Equals(_lastQuery, query, StringComparison.Ordinal))
        {
            _lastQuery = query;
            _currentMatch = _matches.Count > 0 ? 0 : -1;
        }
        else if (_matches.Count == 0)
        {
            _currentMatch = -1;
        }
        else
        {
            _currentMatch = Math.Clamp(_currentMatch, 0, _matches.Count - 1);
        }

        MatchCount = _matches.Count;
        CurrentMatchIndex = _currentMatch;
    }

    private void UpdateSearchState()
    {
        var hasQuery = !string.IsNullOrEmpty(Query);
        MatchText.Text =
            !hasQuery ? string.Empty
            : _matches.Count == 0 ? "无匹配"
            : $"{_currentMatch + 1}/{_matches.Count}";
        PreviousButton.IsEnabled = _matches.Count > 0;
        NextButton.IsEnabled = _matches.Count > 0;
        PreviousButton.IsVisible = hasQuery;
        NextButton.IsVisible = hasQuery;
        if (_currentMatch >= 0)
        {
            ScrollToMatch(_matches[_currentMatch]);
        }
    }

    private void Move(int delta)
    {
        if (_matches.Count == 0)
        {
            return;
        }

        _currentMatch = (_currentMatch + delta + _matches.Count) % _matches.Count;
        CurrentMatchIndex = _currentMatch;
        Rebuild();
    }

    private void ScrollToMatch(int offset)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                var text = Text ?? string.Empty;
                var line = 0;
                var limit = Math.Min(offset, text.Length);
                for (var index = 0; index < limit; index++)
                {
                    if (text[index] == '\n')
                    {
                        line++;
                    }
                }

                var y = Math.Max(0, (line * _lineHeight) - 32);
                Scroller.Offset = new Vector(Scroller.Offset.X, y);
            },
            DispatcherPriority.Background
        );
    }

    private void UpdateGutter(string text)
    {
        var show = ShowLineNumbers && text.Length > 0;
        Gutter.IsVisible = show;
        if (!show)
        {
            Gutter.Text = null;
            return;
        }

        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        var builder = new StringBuilder(lines * 3);
        for (var index = 1; index <= lines; index++)
        {
            if (index > 1)
            {
                builder.Append('\n');
            }

            builder.Append(index);
        }

        Gutter.Text = builder.ToString();
    }

    private void BuildInlines(string text, byte[] kinds, bool[] matches, bool[] current)
    {
        var inlines = Code.Inlines ??= new InlineCollection();
        inlines.Clear();
        if (text.Length == 0)
        {
            return;
        }

        var start = 0;
        for (var index = 1; index <= text.Length; index++)
        {
            var boundary =
                index == text.Length
                || kinds[index] != kinds[start]
                || matches[index] != matches[start]
                || current[index] != current[start];
            if (!boundary)
            {
                continue;
            }

            var run = new Run(text.Substring(start, index - start))
            {
                Foreground = BrushFor(kinds[start]),
            };
            if (current[start])
            {
                run.Background = WithAlpha(HighlightBrush, 0.42);
            }
            else if (matches[start])
            {
                run.Background = WithAlpha(HighlightBrush, 0.18);
            }

            inlines.Add(run);
            start = index;
        }
    }

    private IBrush? BrushFor(byte kind) =>
        kind switch
        {
            KeyKind => KeyBrush,
            StringKind => StringBrush,
            NumberKind => NumberBrush,
            LiteralKind => LiteralBrush,
            PunctuationKind => PunctuationBrush,
            _ => Code.Foreground,
        };

    private static IBrush? WithAlpha(IBrush? brush, double alpha) =>
        brush is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, alpha) : brush;

    private static void Classify(string text, byte[] kinds)
    {
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character == '"')
            {
                var end = index + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\\')
                    {
                        end += 2;
                        continue;
                    }

                    if (text[end] == '"')
                    {
                        end++;
                        break;
                    }

                    end++;
                }

                end = Math.Min(end, text.Length);
                var kind = IsKey(text, end) ? KeyKind : StringKind;
                Array.Fill(kinds, kind, index, end - index);
                index = end;
                continue;
            }

            if (character == '-' || char.IsDigit(character))
            {
                var end = index + 1;
                while (end < text.Length && IsNumberCharacter(text[end]))
                {
                    end++;
                }

                Array.Fill(kinds, NumberKind, index, end - index);
                index = end;
                continue;
            }

            if (char.IsLetter(character))
            {
                var end = index + 1;
                while (end < text.Length && char.IsLetter(text[end]))
                {
                    end++;
                }

                var word = text.AsSpan(index, end - index);
                if (word is "true" or "false" or "null")
                {
                    Array.Fill(kinds, LiteralKind, index, end - index);
                }

                index = end;
                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                kinds[index] = PunctuationKind;
            }

            index++;
        }
    }

    private static bool IsKey(string text, int end)
    {
        var index = end;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == ':';
    }

    private static bool IsNumberCharacter(char character) =>
        char.IsDigit(character) || character is '.' or 'e' or 'E' or '+' or '-';
}
