using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace ZeyhDramTweaker.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public RelayCommand(Action run, Func<object?, bool>? can = null) : this(_ => run(), can) { }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

/// <summary>A choice in a combo/segmented list: the value plus its already-localized label.</summary>
public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
