using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Moondrop.Core.Devices;
namespace Moondrop.Wpf;
public sealed class BandViewModel : NotifyObject
{
    private int _frequency;
    private double _q;
    private double _gain;
    private PeqFilterType _filterType;
    private byte? _rawFilterCode;
    private bool _enabled;
    private bool _isSelected;
    private bool _isEditable = true;
    private Func<BandViewModel, Task>? _applyAsync;

    public BandViewModel(int index, int frequency, double q, double gain, PeqFilterType filterType, bool enabled, byte? rawFilterCode = null)
    {
        Index = index;
        _frequency = frequency;
        _q = q;
        _gain = gain;
        _filterType = filterType;
        _rawFilterCode = rawFilterCode;
        _enabled = enabled;
        ApplyCommand = new RelayCommand(
            () => _applyAsync?.Invoke(this) ?? Task.CompletedTask,
            () => _applyAsync is not null);
    }

    public int Index { get; }
    public int DisplayIndex => Index + 1;
    public ICommand ApplyCommand { get; }
    public bool IsEditable { get => _isEditable; internal set => SetField(ref _isEditable, value); }
    public int Frequency { get => _frequency; set { if (IsEditable && value is >= 20 and <= 20000) SetField(ref _frequency, value); } }
    public double Q { get => _q; set { if (IsEditable && double.IsFinite(value) && value >= 1.0 / 256 && value <= 127) SetField(ref _q, value); } }
    public double Gain { get => _gain; set { if (IsEditable && double.IsFinite(value) && value is >= -18 and <= 12) SetField(ref _gain, value); } }
    public PeqFilterType FilterType
    {
        get => _filterType;
        set
        {
            if (IsEditable && SetField(ref _filterType, value) && value != PeqFilterType.Unknown)
                _rawFilterCode = null;
        }
    }
    public bool Enabled { get => _enabled; set { if (IsEditable) SetField(ref _enabled, value); } }
    public bool IsSelected { get => _isSelected; internal set => SetField(ref _isSelected, value); }

    internal void SetApplyHandler(Func<BandViewModel, Task> applyAsync)
    {
        _applyAsync = applyAsync;
        ((RelayCommand)ApplyCommand).RaiseCanExecuteChanged();
    }

    public void Load(PeqBand band)
    {
        // Observed values are preserved exactly, including unsupported raw filters.
        _frequency = band.Frequency; _q = band.Q; _gain = band.Gain; _filterType = band.FilterType;
        _rawFilterCode = band.RawFilterCode;
        _enabled = band.Enabled;
        foreach (var property in new[] { nameof(Frequency), nameof(Q), nameof(Gain), nameof(FilterType), nameof(Enabled) }) OnPropertyChanged(property);
    }

    public PeqBand ToCoreBand() => new(Index, Frequency, Q, Gain, FilterType, Enabled, _rawFilterCode);
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
