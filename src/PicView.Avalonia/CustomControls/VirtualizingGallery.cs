using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using PicView.Core.Gallery;
using PicView.Core.ViewModels;

namespace PicView.Avalonia.CustomControls;

/// <summary>
///     A virtualizing wrap panel for image galleries supporting variable item widths with fixed item height,
///     horizontal or vertical orientation, and container recycling.
///     When expanded, it can also lay the items out in vertically scrolling masonry columns or justified rows.
/// </summary>
public class VirtualizingGallery : VirtualizingPanel
{
    /// <inheritdoc cref="WrapPanel" />
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<WrapPanel, Orientation>(nameof(Orientation));

    /// <inheritdoc cref="WrapPanel" />
    public static readonly StyledProperty<double> ItemHeightProperty =
        AvaloniaProperty.Register<WrapPanel, double>(nameof(ItemHeight),
            GalleryDefaults.DefaultDockedGalleryHeight);
    
    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<VirtualizingGallery, double>(nameof(ItemWidth), double.NaN);

    /// <inheritdoc cref="WrapPanel" />
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<WrapPanel, double>(nameof(ItemSpacing));

    /// <inheritdoc cref="WrapPanel" />
    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<WrapPanel, double>(nameof(LineSpacing));

    public static readonly StyledProperty<double> WrapHeightOverrideProperty =
        AvaloniaProperty.Register<VirtualizingGallery, double>(nameof(WrapHeightOverride), double.NaN);

    public static readonly StyledProperty<double> WrapWidthOverrideProperty =
        AvaloniaProperty.Register<VirtualizingGallery, double>(nameof(WrapWidthOverride), double.NaN);

    public static readonly StyledProperty<GalleryLayout> LayoutProperty =
        AvaloniaProperty.Register<VirtualizingGallery, GalleryLayout>(nameof(Layout));

    public static readonly StyledProperty<double> TargetItemSizeProperty =
        AvaloniaProperty.Register<VirtualizingGallery, double>(nameof(TargetItemSize),
            GalleryDefaults.DefaultExpandedGalleryHeight);

    static VirtualizingGallery()
    {
        AffectsMeasure<VirtualizingGallery>(
            OrientationProperty,
            ItemHeightProperty,
            ItemWidthProperty,
            ItemSpacingProperty,
            LineSpacingProperty,
            WrapHeightOverrideProperty,
            WrapWidthOverrideProperty,
            LayoutProperty,
            TargetItemSizeProperty);
    }
    
    public readonly record struct RealizedGalleryItem(int Index, Control Element);

    private readonly List<Rect> _itemBounds = [];

    private readonly List<RealizedGalleryItem> _realizedItems = [];
    public IReadOnlyList<RealizedGalleryItem> RealizedItems => _realizedItems;
    private Rect _viewport;
    private NavigateAbleItemsViewer? _viewer;

    /// <inheritdoc cref="WrapPanel" />
    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }
    
    /// <summary>
    /// The width is either double.NaN or the same as ItemWidth when in a square ratio.
    /// </summary>
    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    /// <summary>
    /// The fixed item height of all items.
    /// <remarks>Must be greater than zero. The widths are calculated dynamically.</remarks>
    /// </summary>
    public double ItemHeight
    {
        get => GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <inheritdoc cref="WrapPanel" />
    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <inheritdoc cref="WrapPanel" />
    public double LineSpacing
    {
        get => GetValue(LineSpacingProperty);
        set => SetValue(LineSpacingProperty, value);
    }
    
    public double WrapHeightOverride
    {
        get => GetValue(WrapHeightOverrideProperty);
        set => SetValue(WrapHeightOverrideProperty, value);
    }

    /// <summary>
    /// The width the masonry columns and justified rows are fitted to, instead of the available width.
    /// <remarks>Used to keep the layout from reflowing while the gallery is animated.</remarks>
    /// </summary>
    public double WrapWidthOverride
    {
        get => GetValue(WrapWidthOverrideProperty);
        set => SetValue(WrapWidthOverrideProperty, value);
    }

    /// <summary>
    /// How the items are arranged when expanded.
    /// </summary>
    public GalleryLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    /// <summary>
    /// The minimum column width of <see cref="GalleryLayout.Masonry"/>,
    /// or the row height <see cref="GalleryLayout.Rows"/> starts from before a row is fitted to the width.
    /// </summary>
    public double TargetItemSize
    {
        get => GetValue(TargetItemSizeProperty);
        set => SetValue(TargetItemSizeProperty, value);
    }

    public bool IsExpanded
    {
        get;
        set
        {
            if (field == value) return;
            var wasFlowLayout = IsFlowLayout;
            field = value;
            OnFlowLayoutChanged(wasFlowLayout);
            InvalidateMeasure();
        }
    }

    /// <summary>
    /// Whether the items are laid out in masonry columns or justified rows, scrolling vertically.
    /// </summary>
    public bool IsFlowLayout => IsExpanded && Layout is not GalleryLayout.Columns;

    public Rect? GetItemBounds(int index)
    {
        if (index >= 0 && index < _itemBounds.Count)
        {
            return _itemBounds[index];
        }
        return null;
    }

    public IReadOnlyList<double> GetIrregularSnapPoints(Orientation orientation, SnapPointsAlignment snapPointsAlignment)
    {
        if (_itemBounds.Count is 0)
        {
            return [];
        }

        var snapPoints = new List<double>();

        if (IsFlowLayout)
        {
            // Rows and columns of different sizes have nothing sensible to snap to
            return [];
        }

        // Only provide snap points when the requested orientation matches our layout's scroll axis
        var isVerticalDocked = !IsExpanded && Orientation == Orientation.Vertical;
        if (isVerticalDocked && orientation is not Orientation.Vertical || !isVerticalDocked && orientation is not Orientation.Horizontal)
        {
            return snapPoints;
        }

        if (!IsExpanded)
        {
            // Docked mode: Snap to individual items
            foreach (var bounds in _itemBounds)
            {
                var point = isVerticalDocked
                    ? snapPointsAlignment switch
                    {
                        SnapPointsAlignment.Near => bounds.Top,
                        SnapPointsAlignment.Center => bounds.Center.Y,
                        SnapPointsAlignment.Far => bounds.Bottom,
                        _ => bounds.Top
                    }
                    : snapPointsAlignment switch
                    {
                        SnapPointsAlignment.Near => bounds.Left,
                        SnapPointsAlignment.Center => bounds.Center.X,
                        SnapPointsAlignment.Far => bounds.Right,
                        _ => bounds.Left
                    };
                snapPoints.Add(point);
            }
        }
        else
        {
            // Expanded mode: Snap to the start of each column
            var lastX = -1.0;
            foreach (var bounds in _itemBounds.Where(bounds => Math.Abs(bounds.X - lastX) > 1.0))
            {
                lastX = bounds.X;
                var point = snapPointsAlignment switch
                {
                    SnapPointsAlignment.Near => bounds.Left,
                    SnapPointsAlignment.Center => bounds.Center.X,
                    SnapPointsAlignment.Far => bounds.Right,
                    _ => bounds.Left
                };
                snapPoints.Add(point);
            }
        }

        return snapPoints;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == OrientationProperty)
        {
            // The old viewport belongs to the other scroll axis, wait for a fresh one
            _viewport = default;
            RecycleAllItems();
        }
        else if (change.Property == LayoutProperty && IsExpanded)
        {
            OnFlowLayoutChanged(change.GetOldValue<GalleryLayout>() is not GalleryLayout.Columns);
        }
    }

    private void OnFlowLayoutChanged(bool wasFlowLayout)
    {
        if (wasFlowLayout == IsFlowLayout)
        {
            return;
        }

        // The scroll axis changes, and the items are styled differently, so start over with fresh containers
        _viewport = default;
        RecycleAllItems();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewer = this.FindAncestorOfType<NavigateAbleItemsViewer>();
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        RecycleAllItems();
        _viewer = null;
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        var newViewport = e.EffectiveViewport;
        if (_viewport == newViewport)
        {
            // Avoid re-measuring on every viewport notification, it makes scrolling stutter
            return;
        }

        _viewport = newViewport;
        InvalidateMeasure();
    }

    private void RecycleAllItems()
    {
        for (var i = _realizedItems.Count - 1; i >= 0; i--)
        {
            var realized = _realizedItems[i];
            ItemContainerGenerator?.ClearItemContainer(realized.Element);
            RemoveInternalChild(realized.Element);
        }
        _realizedItems.Clear();
        _itemBounds.Clear();
    }

    private Size CalculateBounds(Size availableSize)
    {
        _itemBounds.Clear();

        if (Items is not IEnumerable<object> items)
        {
            return new Size();
        }

        if (IsFlowLayout)
        {
            return CalculateFlowBounds(items, availableSize);
        }

        double currentX = 0;
        double currentY = 0;
        double currentColumnMaxWidth = 0;
        double maxExtentY = 0;
        var currentColumnStartIndex = 0; // Track where the current column starts

        // Use the override if provided, otherwise use the actual available height
        var availableHeight = double.IsNaN(WrapHeightOverride) || WrapHeightOverride <= 0
            ? availableSize.Height
            : WrapHeightOverride;

        foreach (var item in items)
        {
            var itemWidth = ItemHeight; // Fallback for square

            // 1. Check if the user forced a specific width (e.g., Square Stretch Mode)
            if (!double.IsNaN(ItemWidth))
            {
                itemWidth = ItemWidth;
            }
            // 2. Otherwise, dynamically calculate based on aspect ratio
            else if (item is GalleryItemViewModel { PixelHeight: > 0 } vm)
            {
                var aspectRatio = (double)vm.PixelWidth / vm.PixelHeight;
                itemWidth = ItemHeight * aspectRatio;
            }

            if (IsExpanded)
            {
                // Vertical Wrapping: Use the locked availableHeight
                if (currentY + ItemHeight > availableHeight && currentY > 0)
                {
                    for (var j = currentColumnStartIndex; j < _itemBounds.Count; j++)
                    {
                        var rect = _itemBounds[j];
                        _itemBounds[j] = new Rect(rect.X, rect.Y, currentColumnMaxWidth, rect.Height);
                    }

                    currentY = 0;
                    currentX += currentColumnMaxWidth + LineSpacing;
                    currentColumnMaxWidth = 0; // Reset for the new column
                    currentColumnStartIndex = _itemBounds.Count;
                }

                _itemBounds.Add(new Rect(currentX, currentY, itemWidth, ItemHeight));
                currentY += ItemHeight + ItemSpacing;
                currentColumnMaxWidth = Math.Max(currentColumnMaxWidth, itemWidth);
                maxExtentY = Math.Max(maxExtentY, currentY - ItemSpacing);
            }
            else if (Orientation == Orientation.Vertical)
            {
                // Vertical Docked Mode (Single Column) - items fill available width
                _itemBounds.Add(new Rect(currentX, currentY, availableSize.Width, ItemHeight));
                currentY += ItemHeight;
            }
            else
            {
                // Horizontal Docked Mode (Single Row) - NO SPACING
                _itemBounds.Add(new Rect(currentX, currentY, itemWidth, ItemHeight));
                currentX += itemWidth;
            }
        }

        if (IsExpanded && currentColumnStartIndex < _itemBounds.Count)
        {
            // Apply max width to the very last column
            for (var j = currentColumnStartIndex; j < _itemBounds.Count; j++)
            {
                var rect = _itemBounds[j];
                _itemBounds[j] = new Rect(rect.X, rect.Y, currentColumnMaxWidth, rect.Height);
            }
        }

        if (!IsExpanded)
        {
            // Return exact extent without deducting spacing
            return Orientation == Orientation.Vertical
                ? new Size(availableSize.Width, currentY)
                : new Size(currentX, ItemHeight);
        }

        var totalWidth = currentX + currentColumnMaxWidth;
        // Guard against Infinity if height is temporarily unconstrained during layout passes
        var totalHeight = double.IsInfinity(availableSize.Height) ? maxExtentY : availableSize.Height;
        return new Size(totalWidth, totalHeight);
    }

    private Size CalculateFlowBounds(IEnumerable<object> items, Size availableSize)
    {
        var width = double.IsNaN(WrapWidthOverride) || WrapWidthOverride <= 0
            ? availableSize.Width
            : WrapWidthOverride;
        var targetSize = TargetItemSize > 0 ? TargetItemSize : GalleryDefaults.DefaultExpandedGalleryHeight;
        if (double.IsInfinity(width) || width <= 0)
        {
            // Not constrained yet, lay out a single column or a single item per row
            width = targetSize;
        }

        var height = Layout is GalleryLayout.Rows
            ? CalculateRowBounds(items, width, targetSize)
            : CalculateMasonryBounds(items, width, targetSize);
        return new Size(width, height);
    }

    /// <summary>
    /// Places each item at the bottom of the shortest column, so the tops of the items never decrease with their index.
    /// </summary>
    private double CalculateMasonryBounds(IEnumerable<object> items, double width, double minColumnWidth)
    {
        var columnSpacing = LineSpacing;
        var itemSpacing = ItemSpacing;
        var columnCount = Math.Max(1, (int)Math.Floor((width + columnSpacing) / (minColumnWidth + columnSpacing)));
        var columnWidth = Math.Max(1, (width - (columnCount - 1) * columnSpacing) / columnCount);
        var columnHeights = new double[columnCount];

        foreach (var item in items)
        {
            var column = 0;
            for (var i = 1; i < columnCount; i++)
            {
                if (columnHeights[i] < columnHeights[column] - 0.5)
                {
                    column = i;
                }
            }

            var itemHeight = columnWidth / GetAspectRatio(item);
            var x = column * (columnWidth + columnSpacing);
            _itemBounds.Add(new Rect(x, columnHeights[column], columnWidth, itemHeight));
            columnHeights[column] += itemHeight + itemSpacing;
        }

        return Math.Max(0, columnHeights.Max() - itemSpacing);
    }

    /// <summary>
    /// Fills rows at the target height until they are wider than the available width, then scales each row down to fit.
    /// The last row keeps the target height.
    /// </summary>
    private double CalculateRowBounds(IEnumerable<object> items, double width, double targetHeight)
    {
        var itemSpacing = ItemSpacing;
        var rowSpacing = LineSpacing;
        var y = 0.0;
        var rowStart = 0;
        var ratioSum = 0.0;
        var ratios = new List<double>();

        foreach (var item in items)
        {
            var ratio = GetAspectRatio(item);
            ratios.Add(ratio);
            ratioSum += ratio;

            var spacing = (ratios.Count - rowStart - 1) * itemSpacing;
            if (ratioSum * targetHeight + spacing <= width)
            {
                continue;
            }

            var rowHeight = Math.Max(1, (width - spacing) / ratioSum);
            AddRow(rowStart, ratios.Count, rowHeight);
            y += rowHeight + rowSpacing;
            rowStart = ratios.Count;
            ratioSum = 0;
        }

        if (rowStart == ratios.Count)
        {
            return Math.Max(0, y - rowSpacing);
        }

        AddRow(rowStart, ratios.Count, targetHeight);
        return y + targetHeight;

        void AddRow(int start, int end, double rowHeight)
        {
            var x = 0.0;
            for (var i = start; i < end; i++)
            {
                var itemWidth = rowHeight * ratios[i];
                _itemBounds.Add(new Rect(x, y, itemWidth, rowHeight));
                x += itemWidth + itemSpacing;
            }
        }
    }

    private static double GetAspectRatio(object item)
    {
        if (item is not GalleryItemViewModel { PixelWidth: > 0, PixelHeight: > 0 } vm)
        {
            return 1;
        }

        return Math.Clamp((double)vm.PixelWidth / vm.PixelHeight,
            GalleryDefaults.MinFlowAspectRatio, GalleryDefaults.MaxFlowAspectRatio);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Items is null || Items.Count is 0)
        {
            RecycleAllItems();
            return new Size();
        }

        // 1. Calculate all layout boundaries instantly in memory
        var extentSize = CalculateBounds(availableSize);

        _viewer ??= this.FindAncestorOfType<NavigateAbleItemsViewer>();
        
        // 2. Determine what is visible (inflate by 2x ItemHeight to buffer scrolling)
        var visibleRect = _viewport;
        if (visibleRect.Width <= 0 || visibleRect.Height <= 0)
        {
            // No usable viewport (not laid out yet, or collapsed to zero size).
            // Never fall back to an unbounded rect, inside a ScrollViewer that would realize every item.
            visibleRect = new Rect(0, 0,
                double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
        }
        visibleRect = visibleRect.Inflate(new Thickness((IsFlowLayout ? TargetItemSize : ItemHeight) * 2));
        var scrollsVertically = IsFlowLayout || Orientation == Orientation.Vertical && !IsExpanded;

        // 3. Find which indices fall inside the visible rect
        var startIndex = -1;
        var endIndex = -1;

        for (var i = 0; i < _itemBounds.Count; i++)
        {
            if (visibleRect.Intersects(_itemBounds[i]))
            {
                if (startIndex is -1) startIndex = i;
                {
                    endIndex = i;
                }
            }
            else if (startIndex is not -1)
            {
                // Early exit: We've completely passed the visible viewport
                var pastViewport = scrollsVertically
                    ? _itemBounds[i].Y > visibleRect.Bottom
                    : _itemBounds[i].X > visibleRect.Right;
                if (pastViewport)
                    break;
            }
        }

        if (startIndex is -1)
        {
            return extentSize;
        }

        // 4. Recycle items that are no longer visible
        for (var i = _realizedItems.Count - 1; i >= 0; i--)
        {
            var realized = _realizedItems[i];
            if (realized.Index >= startIndex && realized.Index <= endIndex)
            {
                continue;
            }

            ItemContainerGenerator?.ClearItemContainer(realized.Element);
            RemoveInternalChild(realized.Element);
            _realizedItems.RemoveAt(i);
        }

        // 5. Realize and measure items that ARE visible
        var itemsList = Items as IList;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var item = itemsList?[i];
            var container = ContainerFromIndex(i);

            // If the realized container's DataContext does not match the current item, recycle it
            if (container is not null && !ReferenceEquals(container.DataContext, item))
            {
                ItemContainerGenerator?.ClearItemContainer(container);
                RemoveInternalChild(container);
                _realizedItems.RemoveAll(r => r.Element == container);
                container = null;
            }

            if (IsFlowLayout && item is GalleryItemViewModel galleryItem)
            {
                // Read by the thumbnail loader when the container is prepared
                galleryItem.DisplayWidth = _itemBounds[i].Width;
                galleryItem.DisplayHeight = _itemBounds[i].Height;
            }

            if (container is null)
            {
                // Generate and add container using Avalonia's generator
                if (ItemContainerGenerator.NeedsContainer(item, i, out var recycleKey))
                {
                    container = ItemContainerGenerator.CreateContainer(item, i, recycleKey);
                    ItemContainerGenerator.PrepareItemContainer(container, item, i);
                    AddInternalChild(container);
                    ItemContainerGenerator.ItemContainerPrepared(container, item, i);

                    // Keep the realized items list sorted by index
                    var insertIndex = _realizedItems.FindIndex(r => r.Index > i);
                    if (insertIndex == -1)
                    {
                        _realizedItems.Add(new RealizedGalleryItem(i, container));
                    }
                    else
                    {
                        _realizedItems.Insert(insertIndex, new RealizedGalleryItem(i, container));
                    }
                }
            }

            // Measure the container using the exact bounds we already calculated
            container?.Measure(_itemBounds[i].Size);
        }

        return extentSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Items is null)
        {
            return new Size();
        }

        foreach (var realized in _realizedItems.Where(realized => realized.Index >= 0 && realized.Index < _itemBounds.Count))
        {
            realized.Element.Arrange(_itemBounds[realized.Index]);
        }

        return finalSize;
    }
    
    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                RecycleAllItems();
                break;

            case NotifyCollectionChangedAction.Add:
                if (e.NewStartingIndex >= 0)
                {
                    var count = e.NewItems?.Count ?? 1;
                    for (var i = 0; i < _realizedItems.Count; i++)
                    {
                        var realized = _realizedItems[i];
                        if (realized.Index >= e.NewStartingIndex)
                        {
                            _realizedItems[i] = realized with { Index = realized.Index + count };
                        }
                    }
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldStartingIndex >= 0)
                {
                    var count = e.OldItems?.Count ?? 1;
                    var oldEndIndex = e.OldStartingIndex + count;
                    for (var i = _realizedItems.Count - 1; i >= 0; i--)
                    {
                        var realized = _realizedItems[i];
                        if (realized.Index >= e.OldStartingIndex && realized.Index < oldEndIndex)
                        {
                            ItemContainerGenerator?.ClearItemContainer(realized.Element);
                            RemoveInternalChild(realized.Element);
                            _realizedItems.RemoveAt(i);
                        }
                        else if (realized.Index >= oldEndIndex)
                        {
                            _realizedItems[i] = realized with { Index = realized.Index - count };
                        }
                    }
                }
                else
                {
                    RecycleAllItems();
                }
                break;

            case NotifyCollectionChangedAction.Replace:
                if (e.NewStartingIndex >= 0)
                {
                    var count = e.NewItems?.Count ?? 1;
                    var replaceEndIndex = e.NewStartingIndex + count;
                    for (var i = _realizedItems.Count - 1; i >= 0; i--)
                    {
                        var realized = _realizedItems[i];
                        if (realized.Index < e.NewStartingIndex || realized.Index >= replaceEndIndex)
                        {
                            continue;
                        }

                        ItemContainerGenerator?.ClearItemContainer(realized.Element);
                        RemoveInternalChild(realized.Element);
                        _realizedItems.RemoveAt(i);
                    }
                }
                else
                {
                    RecycleAllItems();
                }
                break;

            case NotifyCollectionChangedAction.Move:
                RecycleAllItems();
                break;
        }

        InvalidateMeasure();
    }


    protected override Control? ScrollIntoView(int index)
    {
        // Return the container if it happens to be realized, otherwise null. 
        // We handle the actual scrolling externally now.
        return ContainerFromIndex(index);
    }

    protected override Control? ContainerFromIndex(int index)
    {
        foreach (var realizedItem in _realizedItems)
        {
            if (realizedItem.Index == index)
            {
                return realizedItem.Element;
            }
        }

        return null;
    }

    protected override int IndexFromContainer(Control container)
    {
        foreach (var realizedItem in _realizedItems)
        {
            if (realizedItem.Element == container)
            {
                return realizedItem.Index;
            }
        }

        return -1;
    }

    protected override IEnumerable<Control> GetRealizedContainers() =>
        _realizedItems.Select(realizedItem => realizedItem.Element);

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap) 
        => null; // Let the NavigateAbleItemsViewer handle spatial navigation
}
