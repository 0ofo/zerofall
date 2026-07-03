using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZeroFall.Base.Mvvm;
using ZeroFall.Platform.Models;
using ZeroFall.Platform.Services;

namespace ZeroFall.Settings.ViewModels;

public partial class AiSettingsViewModel : ViewModelBase, ISettingsSaveable
{
    private readonly ISettingsService _settingsService;
    private readonly IOutboundHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, string> _providerKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressProviderSideEffects;

    public string? LastSaveError => HasError ? ErrorMessage : null;

    public ObservableCollection<AiProviderItemViewModel> Providers { get; } = [];

    [ObservableProperty]
    private AiProviderItemViewModel? _selectedProvider;

    [ObservableProperty]
    private string _apiBaseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _customModelInput = string.Empty;

    [ObservableProperty]
    private int _customModelContextTokens;

    [ObservableProperty]
    private int _fetchDefaultContextTokens;

    [ObservableProperty]
    private string _fetchFilter = string.Empty;

    [ObservableProperty]
    private AiModelRowViewModel? _selectedCatalogModel;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _testResult = string.Empty;

    [ObservableProperty]
    private int _contextCompressionThresholdPercent = 50;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isFetchingModels;

    [ObservableProperty]
    private bool _hasFetchedModels;

    [ObservableProperty]
    private bool _isApiKeyVisible;

    public string ApiKeyVisibilityLabel => IsApiKeyVisible ? "隐藏" : "显示";

    [ObservableProperty]
    private bool _isCustomProvider;

    [ObservableProperty]
    private string _apiKeyPlaceholder = "sk-...";

    [ObservableProperty]
    private string _endpointHint = string.Empty;

    public ObservableCollection<AiModelRowViewModel> CatalogModels { get; } = [];

    public ObservableCollection<AiModelRowViewModel> FetchedModels { get; } = [];

    public ObservableCollection<AiModelRowViewModel> FilteredFetchedModels { get; } = [];

    public bool HasCatalogModels => CatalogModels.Count > 0;

    public AiSettingsViewModel(ISettingsService settingsService, IOutboundHttpClientFactory httpClientFactory)
    {
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;

        foreach (var preset in AiProviderPresets.BuiltIn)
            Providers.Add(AiProviderItemViewModel.FromPreset(preset));

        LoadConfig();
    }

    partial void OnSelectedProviderChanged(AiProviderItemViewModel? value)
    {
        if (value is null || _suppressProviderSideEffects)
            return;

        ApplySelectedProvider(value);
    }

    partial void OnApiBaseUrlChanged(string value)
    {
        if (_suppressProviderSideEffects || string.IsNullOrWhiteSpace(value))
            return;

        SyncSelectedProviderFromUrl(value);
        ApiKey = GetInMemoryProviderKey(value);
        ApplyCatalog(AiEndpointCatalog.GetCatalog(_settingsService.Load().Ai, value));
    }

    partial void OnApiKeyChanged(string value)
    {
        if (_suppressProviderSideEffects)
            return;

        PersistCurrentProviderKey(value);
        RefreshProviderConfiguredStates();
    }

    partial void OnSelectedCatalogModelChanged(AiModelRowViewModel? value)
    {
        if (value is not null)
            Model = value.Id;
    }

    partial void OnFetchFilterChanged(string value) => RefreshFilteredFetched();

    private void ApplySelectedProvider(AiProviderItemViewModel provider)
    {
        PersistCurrentProviderKey(ApiKey);

        _suppressProviderSideEffects = true;
        try
        {
            var preset = AiProviderPresets.FindById(provider.Id);
            IsCustomProvider = string.Equals(provider.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase);
            ApiKeyPlaceholder = preset?.ApiKeyPlaceholder ?? "sk-...";
            EndpointHint = preset?.EndpointHint ?? string.Empty;

            if (!IsCustomProvider)
                ApiBaseUrl = provider.BaseUrl;

            ApiKey = GetInMemoryProviderKey(ApiBaseUrl);
            ApplyCatalog(AiEndpointCatalog.GetCatalog(_settingsService.Load().Ai, ApiBaseUrl));
        }
        finally
        {
            _suppressProviderSideEffects = false;
        }

        RefreshProviderConfiguredStates();
    }

    private void SyncSelectedProviderFromUrl(string url)
    {
        var matched = AiProviderPresets.MatchByUrl(url);
        var item = Providers.FirstOrDefault(p =>
                       string.Equals(p.Id, matched.Id, StringComparison.OrdinalIgnoreCase))
                   ?? Providers.First(p => string.Equals(p.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase));

        if (!ReferenceEquals(SelectedProvider, item))
        {
            _suppressProviderSideEffects = true;
            try
            {
                SelectedProvider = item;
                IsCustomProvider = string.Equals(item.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase);
                ApiKeyPlaceholder = matched.ApiKeyPlaceholder;
                EndpointHint = matched.EndpointHint ?? string.Empty;
            }
            finally
            {
                _suppressProviderSideEffects = false;
            }
        }
    }

    private void ApplyCatalog(IReadOnlyList<AiModelEntry> catalog)
    {
        CatalogModels.Clear();
        FetchedModels.Clear();
        HasFetchedModels = false;

        foreach (var entry in catalog.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase))
        {
            CatalogModels.Add(new AiModelRowViewModel
            {
                Id = entry.Id,
                ApiContextTokens = entry.ContextTokens,
                ContextTokens = entry.ContextTokens is > 0 ? entry.ContextTokens.Value : 0
            });
        }

        SyncSelectedCatalogModel();
        NotifyCatalogChanged();
    }

    private void NotifyCatalogChanged() => OnPropertyChanged(nameof(HasCatalogModels));

    private void SyncSelectedCatalogModel()
    {
        if (CatalogModels.Count == 0)
        {
            SelectedCatalogModel = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            SelectedCatalogModel = CatalogModels[0];
            Model = SelectedCatalogModel.Id;
            return;
        }

        SelectedCatalogModel = CatalogModels.FirstOrDefault(m =>
            string.Equals(m.Id, Model, StringComparison.OrdinalIgnoreCase))
            ?? CatalogModels[0];
        Model = SelectedCatalogModel.Id;
    }

    private List<AiModelEntry> BuildCatalogEntries() =>
        CatalogModels
            .Select(m => new AiModelEntry
            {
                Id = m.Id,
                ContextTokens = m.ContextTokens > 0 ? m.ContextTokens : null
            })
            .ToList();

    private void LoadConfig()
    {
        var config = _settingsService.Load().Ai;
        AiProviderKeyStore.MigrateLegacyKey(config);

        _providerKeys.Clear();
        foreach (var pair in config.ProviderApiKeys)
        {
            var normalized = AiEndpointCatalog.NormalizeUrl(pair.Key);
            if (normalized.Length > 0)
                _providerKeys[normalized] = pair.Value;
        }

        _suppressProviderSideEffects = true;
        try
        {
            ApiBaseUrl = config.ApiBaseUrl;
            ApiKey = AiProviderKeyStore.GetKey(config, config.ApiBaseUrl);
            Model = config.Model;
            ContextCompressionThresholdPercent = Math.Clamp(config.ContextCompressionThresholdPercent, 40, 80);

            var matched = AiProviderPresets.MatchByUrl(config.ApiBaseUrl);
            SelectedProvider = Providers.FirstOrDefault(p =>
                                   string.Equals(p.Id, matched.Id, StringComparison.OrdinalIgnoreCase))
                               ?? Providers.First(p => string.Equals(p.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase));
            IsCustomProvider = string.Equals(matched.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase);
            ApiKeyPlaceholder = matched.ApiKeyPlaceholder;
            EndpointHint = matched.EndpointHint ?? string.Empty;

            ApplyCatalog(AiEndpointCatalog.GetCatalog(config, config.ApiBaseUrl));
        }
        finally
        {
            _suppressProviderSideEffects = false;
        }

        RefreshProviderConfiguredStates();
    }

    private void PersistCurrentProviderKey(string? apiKey = null)
    {
        var normalized = AiEndpointCatalog.NormalizeUrl(ApiBaseUrl);
        if (normalized.Length == 0)
            return;

        _providerKeys[normalized] = (apiKey ?? ApiKey).Trim();
    }

    private string GetInMemoryProviderKey(string? apiBaseUrl)
    {
        var normalized = AiEndpointCatalog.NormalizeUrl(apiBaseUrl);
        if (normalized.Length > 0 && _providerKeys.TryGetValue(normalized, out var key))
            return key;

        return AiProviderKeyStore.GetKey(_settingsService.Load().Ai, apiBaseUrl);
    }

    private void RefreshProviderConfiguredStates()
    {
        foreach (var provider in Providers)
        {
            if (string.Equals(provider.Id, AiProviderPresets.CustomId, StringComparison.OrdinalIgnoreCase))
            {
                provider.IsConfigured = !string.IsNullOrWhiteSpace(GetInMemoryProviderKey(ApiBaseUrl))
                                        && IsCustomProvider;
                continue;
            }

            var normalized = AiEndpointCatalog.NormalizeUrl(provider.BaseUrl);
            provider.IsConfigured = normalized.Length > 0
                                    && !string.IsNullOrWhiteSpace(_providerKeys.GetValueOrDefault(normalized));
        }
    }

    public bool TrySave() => SaveCore();

    private bool SaveCore()
    {
        PersistCurrentProviderKey();

        if (string.IsNullOrWhiteSpace(ApiBaseUrl)
         || string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "请填写 API 地址与 API Key";
            HasError = true;
            return false;
        }

        if (CatalogModels.Count == 0)
        {
            Model = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(Model)
                 || !CatalogModels.Any(m => string.Equals(m.Id, Model, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "请从模型列表中选择默认模型";
            HasError = true;
            return false;
        }

        try
        {
            var settings = _settingsService.Load();
            settings.Ai.ApiBaseUrl = ApiBaseUrl.Trim();
            settings.Ai.ApiKey = ApiKey.Trim();
            settings.Ai.Model = Model.Trim();
            settings.Ai.ContextCompressionThresholdPercent = Math.Clamp(ContextCompressionThresholdPercent, 40, 80);
            AiEndpointCatalog.SetCatalog(settings.Ai, ApiBaseUrl, BuildCatalogEntries());

            settings.Ai.ProviderApiKeys.Clear();
            foreach (var pair in _providerKeys)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    settings.Ai.ProviderApiKeys[pair.Key] = pair.Value.Trim();
            }

            AiProviderKeyStore.SetKey(settings.Ai, ApiBaseUrl, ApiKey);

            if (_settingsService.Save(settings))
            {
                HasError = false;
                ErrorMessage = string.Empty;
                TestResult = "保存成功";
                LoadConfig();
                return true;
            }

            ErrorMessage = _settingsService.LastError ?? "保存失败";
            HasError = true;
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"保存失败: {ex.Message}";
            HasError = true;
            return false;
        }
    }

    partial void OnIsApiKeyVisibleChanged(bool value) => OnPropertyChanged(nameof(ApiKeyVisibilityLabel));

    [RelayCommand]
    private void ToggleApiKeyVisibility() => IsApiKeyVisible = !IsApiKeyVisible;

    [RelayCommand]
    private void AddModelToList()
    {
        var id = CustomModelInput.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            ErrorMessage = "请输入模型名称";
            HasError = true;
            return;
        }

        HasError = false;
        ErrorMessage = string.Empty;

        if (CatalogModels.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            Model = CatalogModels.First(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)).Id;
            SyncSelectedCatalogModel();
            CustomModelInput = string.Empty;
            TestResult = $"模型「{id}」已在列表中";
            return;
        }

        var contextTokens = CustomModelContextTokens > 0
            ? CustomModelContextTokens
            : 0;
        var row = new AiModelRowViewModel
        {
            Id = id,
            ContextTokens = contextTokens
        };
        InsertCatalogRow(row);
        Model = id;
        SyncSelectedCatalogModel();
        CustomModelInput = string.Empty;
        var usedDefaultContext = CustomModelContextTokens <= 0;
        CustomModelContextTokens = 0;
        TestResult = usedDefaultContext
            ? $"已添加模型「{id}」（上下文将自动推断）"
            : $"已添加模型「{id}」（上下文 {contextTokens:N0} tokens）";
        RefreshFetchedAlreadyInCatalog();
        NotifyCatalogChanged();
    }

    [RelayCommand]
    private void ClearCatalogModels()
    {
        HasError = false;
        ErrorMessage = string.Empty;
        ClearCatalogModelsCore();
        RefreshFetchedAlreadyInCatalog();
        TestResult = "已清空模型列表（未保存；点设置窗口「保存」后生效）";
    }

    private void ClearCatalogModelsCore()
    {
        CatalogModels.Clear();
        Model = string.Empty;
        SelectedCatalogModel = null;
        NotifyCatalogChanged();
    }

    private void RefreshFetchedAlreadyInCatalog()
    {
        var catalogIds = new HashSet<string>(
            CatalogModels.Select(m => m.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in FetchedModels)
            row.AlreadyInCatalog = catalogIds.Contains(row.Id);
    }

    [RelayCommand]
    private void RemoveCatalogModel(AiModelRowViewModel? row)
    {
        if (row is null)
            return;

        HasError = false;
        CatalogModels.Remove(row);
        if (string.Equals(Model, row.Id, StringComparison.OrdinalIgnoreCase))
            Model = CatalogModels.Count > 0 ? CatalogModels[0].Id : string.Empty;
        SyncSelectedCatalogModel();
        RefreshFetchedAlreadyInCatalog();
        TestResult = $"已移除「{row.Id}」";
        NotifyCatalogChanged();
    }

    [RelayCommand]
    private void SelectAllFetched(bool select)
    {
        foreach (var row in VisibleFetchedModels())
        {
            if (row.AlreadyInCatalog)
                continue;
            row.IsSelected = select;
        }
    }

    [RelayCommand]
    private void AddSelectedFetchedModels()
    {
        var selected = FetchedModels.Where(m => m.IsSelected && !m.AlreadyInCatalog).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "请勾选要加入的模型";
            HasError = true;
            return;
        }

        HasError = false;
        var added = 0;
        var updated = 0;
        var defaultCtx = FetchDefaultContextTokens > 0 ? FetchDefaultContextTokens : 0;

        foreach (var item in selected)
        {
            var existing = CatalogModels.FirstOrDefault(m =>
                string.Equals(m.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.ContextTokens <= 0)
                {
                    var ctx = ResolveContextForFetchedRow(item, defaultCtx);
                    if (ctx > 0)
                    {
                        existing.ContextTokens = ctx;
                        updated++;
                    }
                }

                continue;
            }

            var row = new AiModelRowViewModel
            {
                Id = item.Id,
                ApiContextTokens = item.ApiContextTokens,
                ContextTokens = ResolveContextForFetchedRow(item, defaultCtx)
            };
            InsertCatalogRow(row);
            added++;
        }

        if (string.IsNullOrWhiteSpace(Model) && CatalogModels.Count > 0)
            Model = CatalogModels[0].Id;
        SyncSelectedCatalogModel();

        RefreshFetchedAlreadyInCatalog();
        TestResult = added > 0 || updated > 0
            ? $"已添加 {added} 个模型" + (updated > 0 ? $"，并更新了 {updated} 个上下文" : string.Empty)
            : "勾选的模型已在列表中";
        NotifyCatalogChanged();
    }

    private static int ResolveContextForFetchedRow(AiModelRowViewModel item, int defaultCtx)
    {
        if (item.ContextTokens > 0)
            return item.ContextTokens;
        if (item.ApiContextTokens is > 0)
            return item.ApiContextTokens.Value;
        if (defaultCtx > 0)
            return defaultCtx;
        return 0;
    }

    private IEnumerable<AiModelRowViewModel> VisibleFetchedModels()
    {
        var filter = FetchFilter.Trim();
        if (filter.Length == 0)
            return FetchedModels;

        return FetchedModels.Where(m =>
            m.Id.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFilteredFetched()
    {
        FilteredFetchedModels.Clear();
        foreach (var row in VisibleFetchedModels())
            FilteredFetchedModels.Add(row);
    }

    private void InsertCatalogRow(AiModelRowViewModel row)
    {
        var insertAt = CatalogModels.TakeWhile(m =>
                string.Compare(m.Id, row.Id, StringComparison.OrdinalIgnoreCase) < 0)
            .Count();
        CatalogModels.Insert(insertAt, row);
    }

    [RelayCommand]
    private async Task FetchModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "请先填写 API 地址与 API Key";
            HasError = true;
            return;
        }

        IsFetchingModels = true;
        TestResult = string.Empty;
        HasError = false;
        ErrorMessage = string.Empty;

        try
        {
            using var client = _httpClientFactory.CreateClient("ai-settings-models", TimeSpan.FromSeconds(30));
            var fetched = await AiModelsApiClient.FetchModelsAsync(client, ApiBaseUrl, ApiKey);

            ClearCatalogModelsCore();
            FetchedModels.Clear();

            foreach (var entry in fetched)
            {
                FetchedModels.Add(new AiModelRowViewModel
                {
                    Id = entry.Id,
                    ApiContextTokens = entry.ContextTokens,
                    ContextTokens = entry.ContextTokens is > 0 ? entry.ContextTokens.Value : 0,
                    IsSelected = false,
                    AlreadyInCatalog = false
                });
            }

            HasFetchedModels = FetchedModels.Count > 0;
            RefreshFilteredFetched();
            var withContext = fetched.Count(m => m.ContextTokens is > 0);
            TestResult = $"拉取到 {fetched.Count} 个模型；已清空当前模型列表，请勾选后添加。";
            if (withContext < fetched.Count)
                TestResult += " 部分模型未返回上下文大小，添加后可在列表里填写。";
        }
        catch (Exception ex)
        {
            TestResult = $"获取模型列表失败: {ex.Message}";
            HasFetchedModels = false;
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || string.IsNullOrWhiteSpace(ApiKey))
        {
            ErrorMessage = "请先填写 API 地址与 API Key";
            HasError = true;
            return;
        }

        IsTesting = true;
        TestResult = string.Empty;
        HasError = false;

        try
        {
            using var client = _httpClientFactory.CreateClient("ai-settings-test", TimeSpan.FromSeconds(30));
            var models = await AiModelsApiClient.FetchModelsAsync(client, ApiBaseUrl, ApiKey);
            var withContext = models.Count(m => m.ContextTokens is > 0);
            TestResult = withContext > 0
                ? $"连接成功！共 {models.Count} 个模型，{withContext} 个解析到上下文窗口。"
                : $"连接成功！API 可用，共 {models.Count} 个模型。";
        }
        catch (Exception ex)
        {
            TestResult = $"连接失败: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
