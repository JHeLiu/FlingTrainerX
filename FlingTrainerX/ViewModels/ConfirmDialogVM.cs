using System.Collections.ObjectModel;
using FlingTrainerX.Models;
using FlingTrainerX.Services;

namespace FlingTrainerX.ViewModels;

/// <summary>
/// 确认框（ConfirmLayer）的视图模型：标题 / 正文 / 确定按钮文案 / 可选项 / 显隐。
/// <para>
/// 以前这些字是窗口在 <c>ShowConfirmAsync</c> 里一个个 <c>Text = ...</c> 赋上去的，
/// 现在改成绑定：窗口调 <see cref="Show"/> 填好状态，再等 <see cref="Answer"/>。
/// </para>
/// </summary>
public sealed partial class ConfirmDialogVM : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private string _okText = "确定";
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _optionVisible;
    [ObservableProperty] private bool _optionChecked;
    [ObservableProperty] private string _optionText = "";
    [ObservableProperty] private string _optionHint = "";
    [ObservableProperty] private bool _optionHintVisible;

    /// <summary>弹框的回答：(点没点确定, 可选项勾没勾)。</summary>
    public TaskCompletionSource<(bool Ok, bool Checked)>? Answer { get; private set; }

    /// <summary>摆好内容并开框；已经开着就返回 null（连点两下不叠第二个框）。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">正文。</param>
    /// <param name="okText">确定按钮文案。</param>
    /// <param name="primary">确定按钮用主色还是警示色。</param>
    /// <param name="option">可选项（文案, 小字）；null = 不要可选项。</param>
    public Task<(bool Ok, bool Checked)>? Show(
        string title, string body, string okText, bool primary, (string Text, string Hint)? option)
    {
        if (Answer is not null) return null;

        Title = title;
        Body = body;
        OkText = okText;
        IsPrimary = primary;

        // 可选项：有就摆出来并清零，没有就整块收起（上一个框勾过的状态不会带过来）
        OptionVisible = option is not null;
        OptionChecked = false;
        OptionText = option?.Text ?? "";
        OptionHint = option?.Hint ?? "";
        OptionHintVisible = OptionHint.Length > 0;

        Answer = new TaskCompletionSource<(bool Ok, bool Checked)>();
        IsOpen = true;

        return Answer.Task;
    }

    /// <summary>收框并交回答案。</summary>
    /// <param name="ok">true = 点了确定。</param>
    public void Close(bool ok)
    {
        IsOpen = false;

        var pending = Answer;
        Answer = null;
        pending?.TrySetResult((ok, OptionChecked));
    }

    /// <summary>「确定」按钮：XAML 里 <c>Command="{Binding Confirm.OkCommand}"</c>。</summary>
    [RelayCommand]
    private void Ok() => Close(true);

    /// <summary>「取消」按钮 / 右上角叉 / 遮罩点击。</summary>
    [RelayCommand]
    private void Cancel() => Close(false);
}
