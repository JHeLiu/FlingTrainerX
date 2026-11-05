using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FlingTrainerX.Mvvm;

/// <summary>
/// 所有 ViewModel 的基类：只做一件事——属性变了通知界面。
/// 不引用任何控件类型，VM 因此可以脱离窗口单独构造（自检 / 单元测试直接 new 出来用）。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>一次通知一组属性（派生属性联动时用，避免写一长串 OnPropertyChanged）。</summary>
    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var name in names) OnPropertyChanged(name);
    }

    /// <summary>值真的变了才写字段并通知，避免无谓的界面刷新。</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
