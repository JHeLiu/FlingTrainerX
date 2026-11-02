using System.Collections.ObjectModel;

namespace FlingTrainerX.Models;

/// <summary>
/// 虚拟滚动用的一行卡片（固定 5 个）。把卡片按行切开后，
/// 列表项高度一致，VirtualizingStackPanel 才能正确回收容器。
/// </summary>
public sealed class CardRow
{
    public const int Capacity = 5;

    public ObservableCollection<TrainerItem> Items { get; } = new();

    public bool IsFull => Items.Count >= Capacity;

    public static IEnumerable<CardRow> Split(IEnumerable<TrainerItem> items)
    {
        CardRow? row = null;

        foreach (var item in items)
        {
            if (row is null || row.IsFull)
            {
                row = new CardRow();
                yield return row;
            }

            row.Items.Add(item);
        }
    }
}
