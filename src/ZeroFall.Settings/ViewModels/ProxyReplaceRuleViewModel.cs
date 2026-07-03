using CommunityToolkit.Mvvm.ComponentModel;
using ZeroFall.Platform.Models;

namespace ZeroFall.Settings.ViewModels;

public partial class ProxyReplaceRuleViewModel : ObservableObject
{
    [ObservableProperty]
    private string _remark = string.Empty;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _match = string.Empty;

    [ObservableProperty]
    private bool _isRegex;

    [ObservableProperty]
    private string _replacement = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    public string ListTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Remark))
                return Remark.Trim();
            var host = string.IsNullOrWhiteSpace(Host) ? "*" : Host.Trim();
            var matchPreview = Match.Length <= 24 ? Match : Match[..24] + "…";
            return $"{host} → {matchPreview}";
        }
    }

    partial void OnRemarkChanged(string value) => OnPropertyChanged(nameof(ListTitle));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(ListTitle));
    partial void OnMatchChanged(string value) => OnPropertyChanged(nameof(ListTitle));

    public static ProxyReplaceRuleViewModel FromConfig(ProxyReplaceRule rule) => new()
    {
        Remark = rule.Remark,
        Host = rule.Host,
        Match = rule.Match,
        IsRegex = rule.IsRegex,
        Replacement = rule.Replacement,
        Enabled = rule.Enabled
    };

    public ProxyReplaceRule ToConfig() => new()
    {
        Remark = Remark.Trim(),
        Host = Host.Trim(),
        Match = Match,
        IsRegex = IsRegex,
        Replacement = Replacement,
        Enabled = Enabled
    };
}
