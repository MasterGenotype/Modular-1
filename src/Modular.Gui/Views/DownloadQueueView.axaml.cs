using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Modular.Gui.Models;
using Modular.Gui.ViewModels;

namespace Modular.Gui.Views;

public partial class DownloadQueueView : UserControl
{
    private DownloadItemModel? _draggedItem;
    private int _dragStartIndex = -1;

    public DownloadQueueView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    public async void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control)
            return;

        if (control.DataContext is not DownloadItemModel item)
            return;

        // Only allow dragging queued items
        if (item.State != DownloadItemState.Queued)
            return;

        if (DataContext is not DownloadQueueViewModel vm)
            return;

        _draggedItem = item;
        _dragStartIndex = vm.GetItemIndex(item);

#pragma warning disable CS0618 // Type or member is obsolete
        var dragData = new DataObject();
        dragData.Set("DownloadItem", item);

        await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
#pragma warning restore CS0618
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Move;

#pragma warning disable CS0618 // Type or member is obsolete
        if (!e.Data.Contains("DownloadItem"))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_draggedItem == null || _dragStartIndex < 0)
            return;

        if (DataContext is not DownloadQueueViewModel vm)
            return;

        // Find the drop target
        var position = e.GetPosition(this);
        var target = this.GetVisualAt(position);

        // Walk up to find the Border containing the DownloadItemModel
        while (target != null)
        {
            if (target is Control { DataContext: DownloadItemModel targetItem } && targetItem != _draggedItem)
            {
                var targetIndex = vm.GetItemIndex(targetItem);
                if (targetIndex >= 0)
                {
                    vm.MoveItem(_dragStartIndex, targetIndex);
                    break;
                }
            }
            target = target.GetVisualParent();
        }

        _draggedItem = null;
        _dragStartIndex = -1;
    }
}
