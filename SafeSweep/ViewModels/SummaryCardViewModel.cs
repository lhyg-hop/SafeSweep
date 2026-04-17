using SafeSweep.Utils;

namespace SafeSweep.ViewModels;

public sealed class SummaryCardViewModel : ObservableObject
{
    private string _title = string.Empty;
    private string _value = string.Empty;
    private string _subtitle = string.Empty;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }
}
