using CommunityToolkit.Mvvm.ComponentModel;

namespace DNSManagerPro.App.ViewModels;

/// <summary>One option of a radio-style choice list. Selecting it reports back to its owner.</summary>
public sealed partial class ChoiceOption(string label, string? value, Action<ChoiceOption> onSelected) : ObservableObject
{
    public string Label { get; } = label;

    public string? Value { get; } = value;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            onSelected(this);
        }
    }
}
