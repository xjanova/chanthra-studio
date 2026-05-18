using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ChanthraStudio.Controls;

/// <summary>
/// Wrap-style panel that virtualises rows. Items are laid out left-to-right
/// at <see cref="ItemWidth"/> × <see cref="ItemHeight"/>; rows that fall
/// outside the visible <see cref="ScrollViewer"/> viewport never realise
/// their containers — saving the cost of N hundred frozen
/// <c>BitmapImage</c> instances when the Library hits the 1000-clip cap.
///
/// <para>
/// WPF's built-in <c>WrapPanel</c> doesn't virtualise; <c>VirtualizingStackPanel</c>
/// only does single-column/row. This minimal implementation derives from
/// <see cref="VirtualizingPanel"/> and implements <see cref="IScrollInfo"/>
/// so the containing <see cref="ScrollViewer"/> talks to us directly.
/// (T52 · 7.21)
/// </para>
///
/// <para>
/// Scope: vertical scroll, fixed item size. Pan / horizontal flow / unequal
/// row heights aren't supported — this exists to serve the Library's
/// uniform 280×220 thumbnail grid, not as a general WPF panel.
/// </para>
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(280.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    // ---------- IScrollInfo plumbing ----------

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    private const double LineDelta = 16;

    public void LineUp()    => SetVerticalOffset(_offset.Y - LineDelta);
    public void LineDown()  => SetVerticalOffset(_offset.Y + LineDelta);
    public void PageUp()    => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown()  => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void MouseWheelUp()   => SetVerticalOffset(_offset.Y - 3 * LineDelta);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 3 * LineDelta);
    public void LineLeft()  { }
    public void LineRight() { }
    public void PageLeft()  { }
    public void PageRight() { }
    public void MouseWheelLeft()  { }
    public void MouseWheelRight() { }

    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(_offset.Y - offset) < 0.001) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    // ---------- Measure / Arrange ----------

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;  // touch to wire up the generator
        var generator = ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);

        // Columns per row = how many ItemWidth fit in availableWidth.
        var cols = Math.Max(1, (int)Math.Floor(availableSize.Width / ItemWidth));
        var itemCount = itemsOwner?.Items.Count ?? 0;
        var rows = (int)Math.Ceiling((double)itemCount / cols);

        _viewport = availableSize;
        _extent = new Size(cols * ItemWidth, rows * ItemHeight);

        // Clamp Y offset in case the new extent shrank below the old offset.
        if (_offset.Y + _viewport.Height > _extent.Height)
            _offset.Y = Math.Max(0, _extent.Height - _viewport.Height);

        // Decide which items are in (or near) the visible band.
        var firstVisibleRow = (int)Math.Floor(_offset.Y / ItemHeight);
        var lastVisibleRow  = (int)Math.Floor((_offset.Y + _viewport.Height) / ItemHeight);
        // 1-row cache band above + below to keep scrolling smooth.
        var firstRow = Math.Max(0, firstVisibleRow - 1);
        var lastRow  = Math.Min(rows - 1, lastVisibleRow + 1);
        var firstItem = firstRow * cols;
        var lastItem  = Math.Min(itemCount - 1, (lastRow + 1) * cols - 1);

        if (itemCount == 0)
        {
            // Nothing to realise — clean up any leftover containers.
            CleanUpItems(0, -1, generator);
            ScrollOwner?.InvalidateScrollInfo();
            return availableSize;
        }

        // Realise the visible band; recycle items outside it.
        if (generator is not null && itemCount > 0)
        {
            var startPos = generator.GeneratorPositionFromIndex(firstItem);
            using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
            {
                for (int i = firstItem; i <= lastItem; i++)
                {
                    var child = generator.GenerateNext(out var isNew) as UIElement;
                    if (child is null) continue;
                    if (isNew)
                    {
                        if (i < Children.Count) InsertInternalChild(i, child);
                        else AddInternalChild(child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
            CleanUpItems(firstItem, lastItem, generator);
        }

        ScrollOwner?.InvalidateScrollInfo();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return finalSize;
        var cols = Math.Max(1, (int)Math.Floor(finalSize.Width / ItemWidth));

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            // The IItemContainerGenerator interface doesn't expose
            // IndexFromContainer; the concrete ItemContainerGenerator does.
            var index = (generator as ItemContainerGenerator)?.IndexFromContainer(child) ?? -1;
            if (index < 0) continue;
            var col = index % cols;
            var row = index / cols;
            var x = col * ItemWidth;
            var y = row * ItemHeight - _offset.Y;
            child.Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }
        return finalSize;
    }

    /// <summary>
    /// Recycle every realised container outside <c>[firstActive..lastActive]</c>.
    /// Uses <see cref="ItemContainerGenerator.Remove"/> + <see cref="VirtualizingPanel.RemoveInternalChildRange"/>
    /// which together let WPF reuse containers via the recycling strategy.
    /// </summary>
    private void CleanUpItems(int firstActive, int lastActive, IItemContainerGenerator? generator)
    {
        if (generator is null) return;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(pos);
            if (index < firstActive || index > lastActive)
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }
}
