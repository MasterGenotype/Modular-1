using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Modular.Gui.ViewModels;

namespace Modular.Gui.Views;

public partial class LoadOrderView : UserControl
{
    private const string DragFormat = "OrderRow";

    private OrderRowModel? _draggedRow;

    public LoadOrderView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    public async void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OrderRowModel row })
            return;

        if (row.Item.Locked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggedRow = row;

        // Avalonia's DataObject is marked obsolete but no replacement API exists yet; the
        // download queue uses it the same way.
#pragma warning disable CS0618 // DataObject is obsolete - no alternative available
        var dragData = new DataObject();
        dragData.Set(DragFormat, row);

        await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
#pragma warning restore CS0618
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        e.DragEffects = e.Data.Contains(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var dragged = _draggedRow;
        _draggedRow = null;

        if (dragged == null || DataContext is not LoadOrderViewModel vm)
            return;

        // Walk up from what is under the pointer to the row it belongs to.
        var target = this.GetVisualAt(e.GetPosition(this));
        while (target != null)
        {
            if (target is Control { DataContext: OrderRowModel targetRow } && !ReferenceEquals(targetRow, dragged))
            {
                vm.DropOnto(dragged, targetRow);
                return;
            }

            target = target.GetVisualParent();
        }
    }
}
