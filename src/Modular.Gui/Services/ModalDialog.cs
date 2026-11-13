using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Modular.Gui.Services;

/// <summary>
/// A modal dialog that is its own window on a desktop, and a panel inside the owner
/// window under gamescope.
/// </summary>
/// <remarks>
/// Gamescope stretches whichever window has focus over the whole display, so a small
/// dialog window would be magnified several times and hide the window behind it.
/// </remarks>
public sealed class ModalDialog
{
    // Gap kept between an in-window dialog and the edge of the owner window.
    private const double EdgeMargin = 24;

    // In-window dialogs currently open, the topmost last. Only that one holds the focus.
    private static readonly List<Panel> OpenInWindow = [];

    private Action? _close;

    public string Title { get; init; } = string.Empty;
    public Control? Content { get; init; }
    public double Width { get; init; } = 400;

    /// <summary>Fixed height, or null to fit the content up to <see cref="MaxHeight"/>.</summary>
    public double? Height { get; init; }
    public double MaxHeight { get; init; } = double.PositiveInfinity;
    public double MinWidth { get; init; }
    public double MinHeight { get; init; }

    /// <summary>Control that takes keyboard focus when the dialog opens.</summary>
    public Control? InitialFocus { get; init; }

    public void Close() => _close?.Invoke();

    /// <summary>Shows the dialog and completes when it is closed.</summary>
    public Task ShowAsync(Window owner)
    {
        var layer = Gamescope.IsActive ? OverlayLayer.GetOverlayLayer(owner) : null;
        return layer != null ? ShowInWindowAsync(owner, layer) : ShowWindowAsync(owner);
    }

    private Task ShowWindowAsync(Window owner)
    {
        var window = new Window
        {
            Title = Title,
            Width = Width,
            MaxHeight = MaxHeight,
            MinWidth = MinWidth,
            MinHeight = MinHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = Content
        };
        if (Height is { } height)
            window.Height = height;
        else
            window.SizeToContent = SizeToContent.Height;

        if (InitialFocus is { } focus)
            window.Opened += (_, _) => focus.Focus();

        _close = window.Close;
        return window.ShowDialog(owner);
    }

    private Task ShowInWindowAsync(Window owner, OverlayLayer layer)
    {
        var closed = new TaskCompletionSource();

        var title = new TextBlock
        {
            Text = Title,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(20, 14, 20, 0)
        };
        DockPanel.SetDock(title, Dock.Top);

        var card = new Border
        {
            Width = Width,
            MinWidth = MinWidth,
            MinHeight = MinHeight,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Focusable = true,
            FocusAdorner = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new DockPanel { Children = { title, Content ?? new Panel() } }
        };
        if (Height is { } height)
            card.Height = height;
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("CanvasBrush"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("StrokeStrongBrush"));

        // Covers the owner window: dims it and takes the pointer input meant for it.
        var scrim = new Panel
        {
            Background = new SolidColorBrush(Colors.Black, 0.6),
            Children = { card }
        };
        KeyboardNavigation.SetTabNavigation(scrim, KeyboardNavigationMode.Cycle);

        void Arrange()
        {
            scrim.Width = layer.Bounds.Width;
            scrim.Height = layer.Bounds.Height;
            card.MaxWidth = Math.Max(0, layer.Bounds.Width - 2 * EdgeMargin);
            card.MaxHeight = Math.Max(0, Math.Min(MaxHeight, layer.Bounds.Height - 2 * EdgeMargin));
        }

        void OnLayerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty)
                Arrange();
        }

        // Keys the dialog did not use stop here, so the owner's shortcuts stay off. Tab is
        // let through: focus traversal is handled above this panel.
        scrim.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab)
                e.Handled = true;
        };

        // Focus has to stay inside the dialog: anywhere else, keys would reach the owner's
        // controls and shortcuts behind it.
        void OnOwnerGotFocus(object? sender, GotFocusEventArgs e)
        {
            if (OpenInWindow.Count > 0 && OpenInWindow[^1] == scrim &&
                e.Source is Visual target && !scrim.IsVisualAncestorOf(target))
            {
                card.Focus();
            }
        }

        var previousFocus = owner.FocusManager?.GetFocusedElement();
        var isOpen = true;

        _close = () =>
        {
            if (!isOpen)
                return;
            isOpen = false;

            owner.RemoveHandler(InputElement.GotFocusEvent, OnOwnerGotFocus);
            layer.PropertyChanged -= OnLayerChanged;
            layer.Children.Remove(scrim);
            OpenInWindow.Remove(scrim);
            previousFocus?.Focus();

            closed.SetResult();
        };

        Arrange();
        layer.PropertyChanged += OnLayerChanged;
        layer.Children.Add(scrim);
        OpenInWindow.Add(scrim);
        owner.AddHandler(InputElement.GotFocusEvent, OnOwnerGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        Dispatcher.UIThread.Post(() => (InitialFocus ?? card).Focus(), DispatcherPriority.Loaded);

        return closed.Task;
    }
}
