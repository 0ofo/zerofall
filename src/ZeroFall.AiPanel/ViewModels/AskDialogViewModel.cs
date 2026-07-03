using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Irihi.Avalonia.Shared.Contracts;
using Ursa.Controls;

namespace ZeroFall.AiPanel.ViewModels;

public partial class AskDialogViewModel : ObservableObject, IDialogContext
{
    [ObservableProperty]
    private string _question = string.Empty;

    [ObservableProperty]
    private bool _isMultiSelect;

    [ObservableProperty]
    private string _supplementaryInput = string.Empty;

    [ObservableProperty]
    private string? _selectedSingleOption;

    public ObservableCollection<AskOptionViewModel> Options { get; } = [];

    public event EventHandler<object?>? RequestClose;

    public void Close() => RequestClose?.Invoke(this, DialogResult.Cancel);

    private bool _isCancelled = true;
    private List<string> _selectedOptions = [];

    [RelayCommand]
    private void SelectOption(string option) => PickSingleOption(option);

    [RelayCommand]
    private void Submit()
    {
        if (IsMultiSelect)
        {
            _selectedOptions = Options.Where(o => o.IsChecked).Select(o => o.Label).ToList();
            _isCancelled = false;
            RequestClose?.Invoke(this, DialogResult.OK);
            return;
        }

        if (SelectedSingleOption != null)
        {
            _selectedOptions = [SelectedSingleOption];
            _isCancelled = false;
            RequestClose?.Invoke(this, DialogResult.OK);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _isCancelled = true;
        _selectedOptions = [];
        RequestClose?.Invoke(this, DialogResult.Cancel);
    }

    public AskResult ToResult(DialogResult? dialogResult)
    {
        if (dialogResult != DialogResult.OK || _isCancelled)
            return new AskResult([], string.Empty, true);

        return new AskResult(_selectedOptions, SupplementaryInput, false);
    }

    public void SetupSingleSelect(string question, List<string> options)
    {
        Question = question;
        IsMultiSelect = false;
        Options.Clear();
        foreach (var opt in options)
            Options.Add(new AskOptionViewModel { Label = opt, Owner = this });
    }

    public void SetupMultiSelect(string question, List<string> options)
    {
        Question = question;
        IsMultiSelect = true;
        Options.Clear();
        foreach (var opt in options)
            Options.Add(new AskOptionViewModel { Label = opt, Owner = this });
    }

    internal void PickSingleOption(string option)
    {
        SelectedSingleOption = option;
        Submit();
    }
}

public partial class AskOptionViewModel : ObservableObject
{
    internal AskDialogViewModel? Owner { get; init; }

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    [RelayCommand]
    private void Select() => Owner?.PickSingleOption(Label);
}

public class AskResult
{
    public List<string> SelectedOptions { get; }
    public string SupplementaryInput { get; }
    public bool IsCancelled { get; }

    public AskResult(List<string> selectedOptions, string supplementaryInput, bool isCancelled)
    {
        SelectedOptions = selectedOptions;
        SupplementaryInput = supplementaryInput;
        IsCancelled = isCancelled;
    }
}
