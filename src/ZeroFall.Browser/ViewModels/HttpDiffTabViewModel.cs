using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Base.Events;
using ZeroFall.Browser.Services;
using ZeroFall.Platform.Events;
using ZeroFall.Platform.Registries;

namespace ZeroFall.Browser.ViewModels;

public partial class HttpDiffTabViewModel : BrowserTabViewModelBase, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IDisposable _diffRequestedSub;

    [ObservableProperty]
    private string _leftLabel = "左侧";

    [ObservableProperty]
    private string _rightLabel = "右侧";

    [ObservableProperty]
    private string _leftText = string.Empty;

    [ObservableProperty]
    private string _rightText = string.Empty;

    [ObservableProperty]
    private string _summaryText = "从流量表右键「发送到 Diff」导入对比项。";

    public ObservableCollection<HttpDiffLineViewModel> Lines { get; } = new();

    public HttpDiffTabViewModel(IEventBus eventBus)
    {
        _eventBus = eventBus;
        Title = "Comparer";
        _diffRequestedSub = eventBus.SubscribeDisposable<HttpDiffRequestedEvent>(OnDiffRequested);
    }

    private void OnDiffRequested(HttpDiffRequestedEvent e)
    {
        LeftLabel = e.LeftLabel;
        RightLabel = e.RightLabel;
        LeftText = e.LeftText;
        RightText = e.RightText;
        RefreshDiff();
        _eventBus.Publish(new SwitchDockTabRequestedEvent(DockPosition.Content, "http-diff"));
    }

    partial void OnLeftTextChanged(string value) => RefreshDiff();

    partial void OnRightTextChanged(string value) => RefreshDiff();

    private void RefreshDiff()
    {
        Lines.Clear();
        var diff = HttpLineDiff.Compare(LeftText, RightText);
        var changed = 0;
        foreach (var line in diff)
        {
            if (line.Kind != HttpDiffLineKind.Same)
                changed++;

            Lines.Add(HttpDiffLineViewModel.From(line));
        }

        SummaryText = diff.Count == 0
            ? "无内容可对比"
            : $"共 {diff.Count} 行，{changed} 行有差异";
    }

    public void Dispose() => _diffRequestedSub.Dispose();
}

public sealed class HttpDiffLineViewModel
{
    public required string Left { get; init; }

    public required string Right { get; init; }

    public required IBrush LeftBackground { get; init; }

    public required IBrush RightBackground { get; init; }

    public static HttpDiffLineViewModel From(HttpDiffLine line)
    {
        var transparent = Brushes.Transparent;
        var diff = BrushFromResource("SemiColorWarningLight", "#33FFC107");
        var leftOnly = BrushFromResource("SemiColorDangerLight", "#33F44336");
        var rightOnly = BrushFromResource("SemiColorSuccessLight", "#334CAF50");

        return line.Kind switch
        {
            HttpDiffLineKind.Different => new HttpDiffLineViewModel
            {
                Left = line.Left,
                Right = line.Right,
                LeftBackground = diff,
                RightBackground = diff
            },
            HttpDiffLineKind.LeftOnly => new HttpDiffLineViewModel
            {
                Left = line.Left,
                Right = line.Right,
                LeftBackground = leftOnly,
                RightBackground = transparent
            },
            HttpDiffLineKind.RightOnly => new HttpDiffLineViewModel
            {
                Left = line.Left,
                Right = line.Right,
                LeftBackground = transparent,
                RightBackground = rightOnly
            },
            _ => new HttpDiffLineViewModel
            {
                Left = line.Left,
                Right = line.Right,
                LeftBackground = transparent,
                RightBackground = transparent
            }
        };
    }

    private static IBrush BrushFromResource(string key, string fallbackHex)
    {
        if (Application.Current?.TryFindResource(key, out var resource) == true
            && resource is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.Parse(fallbackHex));
    }
}
