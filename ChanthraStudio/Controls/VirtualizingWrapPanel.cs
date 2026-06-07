using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ChanthraStudio.Services;

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
        // Defensive: zero/negative ItemWidth or ItemHeight (e.g. design-time
        // mistakes) would divide-by-zero or loop forever below. Bail
        // gracefully with zero extent so the surrounding ScrollViewer
        // doesn't render junk and the user gets a working app instead of
        // a hang. (T59 / 7.22 hardening)
        if (ItemWidth <= 0 || ItemHeight <= 0)
        {
            _viewport = Finite(availableSize);
            _extent = new Size(0, 0);
            ScrollOwner?.InvalidateScrollInfo();
            return new Size(0, 0);
        }
        // Wrap the whole measure pass — virtualisation panels are a known
        // source of "WPF eats your stack trace" crashes when the
        // ItemContainerGenerator misbehaves (collection-changed mid-paint,
        // etc.). Fall back to a zero-extent return so the panel disappears
        // gracefully rather than tearing down the window.
        try
        {
            return MeasureImpl(availableSize);
        }
        catch (Exception ex)
        {
            ActivityLog.Warn("vwp", "measure failed: " + ex.Message);
            _viewport = Finite(availableSize);
            _extent = new Size(0, 0);
            ScrollOwner?.InvalidateScrollInfo();
            return new Size(0, 0);
        }
    }

    /// <summary>Replace any Infinity component with 0. A DesiredSize that
    /// carries Infinity makes WPF throw "should not return PositiveInfinity"
    /// and tears down the window — this guards every measure return path.</summary>
    private static Size Finite(Size s) => new Size(
        double.IsInfinity(s.Width) ? 0 : s.Width,
        double.IsInfinity(s.Height) ? 0 : s.Height);

    private Size MeasureImpl(Size availableSize)
    {
        // Touch InternalChildren to wire up the generator before we query it.
        var _ = InternalChildren;
        var generator = ItemContainerGenerator;
        var concreteGen = generator as ItemContainerGenerator;
        var itemsOwner = ItemsControl.GetItemsOwner(this);

        // Width is normally constrained (the Library disables horizontal
        // scrolling), but guard against an infinite width too so cols stays sane.
        var usableWidth = double.IsInfinity(availableSize.Width) ? ItemWidth : availableSize.Width;
        var cols = Math.Max(1, (int)Math.Floor(usableWidth / ItemWidth));
        var itemCount = itemsOwner?.Items.Count ?? 0;
        var rows = (int)Math.Ceiling((double)itemCount / cols);

        _extent = new Size(cols * ItemWidth, rows * ItemHeight);

        // When the parent measures us with an UNCONSTRAINED height, the
        // ScrollViewer is pixel-scrolling us (it is NOT driving us through
        // IScrollInfo). In that mode we must realise EVERY item and report our
        // full, FINITE height — otherwise scrolled rows would be blank, OR (the
        // original crash) we'd return Infinity as DesiredSize and WPF would tear
        // the window down. With a finite height we virtualise the visible band.
        var unconstrained = double.IsInfinity(availableSize.Height);
        _viewport = new Size(usableWidth, unconstrained ? _extent.Height : availableSize.Height);

        // Clamp Y offset in case the new extent shrank below the old offset.
        if (_offset.Y + _viewport.Height > _extent.Height)
            _offset.Y = Math.Max(0, _extent.Height - _viewport.Height);

        int firstItem, lastItem;
        if (unconstrained)
        {
            // Realise all items — full content, pixel-scrolled by the ScrollViewer.
            firstItem = 0;
            lastItem = itemCount - 1;
        }
        else
        {
            // Realise only the visible band (+1 row of cache above/below).
            var firstVisibleRow = (int)Math.Floor(_offset.Y / ItemHeight);
            var lastVisibleRow  = (int)Math.Floor((_offset.Y + _viewport.Height) / ItemHeight);
            var firstRow = Math.Max(0, firstVisibleRow - 1);
            var lastRow  = Math.Min(Math.Max(0, rows - 1), lastVisibleRow + 1);
            firstItem = itemCount == 0 ? 0 : firstRow * cols;
            lastItem  = itemCount == 0 ? -1 : Math.Min(itemCount - 1, (lastRow + 1) * cols - 1);
        }

        // ============================================================
        // Step 1: recycle out-of-range realised children FIRST (walk backwards
        // so removals don't shift unvisited indices). (7.20 fix — CRIT #1 + #2)
        // ============================================================
        if (concreteGen is not null)
        {
            for (int childIdx = InternalChildren.Count - 1; childIdx >= 0; childIdx--)
            {
                var child = InternalChildren[childIdx];
                var itemIdx = concreteGen.IndexFromContainer(child);
                if (itemIdx < firstItem || itemIdx > lastItem)
                {
                    var pos = new GeneratorPosition(childIdx, 0);
                    generator.Remove(pos, 1);
                    RemoveInternalChildRange(childIdx, 1);
                }
            }
        }

        // ============================================================
        // Step 2: generate any IN-range items that don't have a container yet.
        // ============================================================
        if (generator is not null && itemCount > 0 && lastItem >= firstItem)
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
                        AddInternalChild(child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(ItemWidth, ItemHeight));
                }
            }
        }

        ScrollOwner?.InvalidateScrollInfo();
        // DesiredSize MUST be finite. Width = content width; Height = the full
        // extent when unconstrained, else the (finite) viewport height we got.
        return new Size(_extent.Width, unconstrained ? _extent.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (ItemWidth <= 0 || ItemHeight <= 0) return finalSize;
        try
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
        catch (Exception ex)
        {
            // Same defensive guard as MeasureOverride — Arrange is a known
            // crash site under collection-changed-during-layout races.
            ActivityLog.Warn("vwp", "arrange failed: " + ex.Message);
            return finalSize;
        }
    }

    // CleanUpItems removed in the 7.20 fix — MeasureOverride now handles
    // recycle-first, generate-second inline so the index conventions stay
    // unambiguous (review CRIT #1 + #2).
}
