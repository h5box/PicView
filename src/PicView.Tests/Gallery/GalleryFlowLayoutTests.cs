using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using PicView.Avalonia.CustomControls;
using PicView.Core.Gallery;
using PicView.Core.ViewModels;

namespace PicView.Tests.Gallery;

/// <summary>
/// The masonry and justified row layouts of the expanded gallery.
/// </summary>
[Collection("Sequential")]
public class GalleryFlowLayoutTests
{
    private const double Tolerance = 0.01;

    public GalleryFlowLayoutTests()
    {
        SetDefaults();
    }

    private static void RunWithDispatcher(DispatcherOperation operation)
    {
        while (operation.Status is not DispatcherOperationStatus.Completed and not DispatcherOperationStatus.Aborted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        operation.GetAwaiter().GetResult();
    }

    private static GalleryItemViewModel Item(uint width, uint height) => new() { PixelWidth = width, PixelHeight = height };

    private static (VirtualizingGallery Gallery, NavigateAbleItemsViewer Viewer, Window Window) Show(
        GalleryLayout layout, IReadOnlyList<GalleryItemViewModel> items, double wrapWidth = double.NaN,
        double windowWidth = 500, double windowHeight = 400)
    {
        var gallery = new VirtualizingGallery
        {
            Layout = layout,
            IsExpanded = true,
            TargetItemSize = 100,
            ItemSpacing = 10,
            LineSpacing = 10,
            WrapWidthOverride = wrapWidth
        };

        var viewer = new NavigateAbleItemsViewer
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => gallery),
            ItemTemplate = new FuncDataTemplate<GalleryItemViewModel>((_, _) => new NavigateAbleItem()),
            ItemsSource = items
        };

        var window = new Window { Width = windowWidth, Height = windowHeight, Content = viewer };
        window.Show();
        viewer.SetVerticalScrolling();
        window.UpdateLayout();
        return (gallery, viewer, window);
    }

    private static Rect Bounds(VirtualizingGallery gallery, int index)
    {
        var bounds = gallery.GetItemBounds(index);
        Assert.NotNull(bounds);
        return bounds.Value;
    }

    [AvaloniaFact]
    public void Masonry_PlacesEachItemAtTheBottomOfTheShortestColumn()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            // 420 wide fits three columns of at least 100 with 10 between them
            var (gallery, _, window) = Show(GalleryLayout.Masonry,
                [Item(100, 100), Item(200, 100), Item(100, 200), Item(100, 100), Item(100, 100)], wrapWidth: 420);
            var columnWidth = (420 - 2 * 10) / 3.0;

            var square = Bounds(gallery, 0);
            var wide = Bounds(gallery, 1);
            var tall = Bounds(gallery, 2);
            Assert.Equal(new Rect(0, 0, columnWidth, columnWidth), square);
            Assert.Equal(new Rect(columnWidth + 10, 0, columnWidth, columnWidth / 2), wide);
            Assert.Equal(new Rect(2 * (columnWidth + 10), 0, columnWidth, columnWidth * 2), tall);

            // The second column is the shortest, then the first one
            var next = Bounds(gallery, 3);
            Assert.Equal(wide.X, next.X, Tolerance);
            Assert.Equal(wide.Bottom + 10, next.Y, Tolerance);
            var last = Bounds(gallery, 4);
            Assert.Equal(square.X, last.X, Tolerance);
            Assert.Equal(square.Bottom + 10, last.Y, Tolerance);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void Masonry_ItemTopsNeverDecrease_AndFillTheWidth()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            var random = new Random(7);
            var items = Enumerable.Range(0, 200)
                .Select(_ => Item((uint)random.Next(200, 4000), (uint)random.Next(200, 4000)))
                .ToList();
            var (gallery, _, window) = Show(GalleryLayout.Masonry, items);

            var bounds = Enumerable.Range(0, items.Count).Select(i => Bounds(gallery, i)).ToList();
            for (var i = 1; i < bounds.Count; i++)
            {
                // The panel stops looking for visible items at the first one below the viewport
                Assert.True(bounds[i].Y >= bounds[i - 1].Y - 0.5, $"Item {i} starts above item {i - 1}");
            }

            // Fitted to the width the vertical scroll bar leaves, nothing to scroll sideways
            Assert.Equal(gallery.Bounds.Width, bounds.Max(b => b.Right), Tolerance);
            Assert.Equal(0, bounds.Min(b => b.X), Tolerance);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void Masonry_ExtremeAspectRatiosAreClamped()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            var (gallery, _, window) = Show(GalleryLayout.Masonry, [Item(100, 2000), Item(4000, 100)], wrapWidth: 100);

            var screenshot = Bounds(gallery, 0);
            var panorama = Bounds(gallery, 1);
            Assert.Equal(100 / GalleryDefaults.MinFlowAspectRatio, screenshot.Height, Tolerance);
            Assert.Equal(100 / GalleryDefaults.MaxFlowAspectRatio, panorama.Height, Tolerance);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void Rows_ScaleFullRowsToTheWidth_AndKeepTheLastRowAtTheTargetHeight()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Three 3:2 items at height 100 are 470 wide with the spacing, a fourth overflows 500
            var (gallery, _, window) = Show(GalleryLayout.Rows,
                [Item(300, 200), Item(300, 200), Item(300, 200), Item(300, 200), Item(100, 100), Item(200, 100)],
                wrapWidth: 500);
            var rowHeight = (500 - 3 * 10) / (4 * 1.5);

            for (var i = 0; i < 4; i++)
            {
                var bounds = Bounds(gallery, i);
                Assert.Equal(0, bounds.Y, Tolerance);
                Assert.Equal(rowHeight, bounds.Height, Tolerance);
                Assert.Equal(i * (rowHeight * 1.5 + 10), bounds.X, Tolerance);
            }
            Assert.Equal(500, Bounds(gallery, 3).Right, Tolerance);

            var square = Bounds(gallery, 4);
            var wide = Bounds(gallery, 5);
            Assert.Equal(new Rect(0, rowHeight + 10, 100, 100), square);
            Assert.Equal(new Rect(110, rowHeight + 10, 200, 100), wide);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void FlowLayout_ScrollsVertically_AndRealizesOnlyTheItemsNearTheViewport()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            var items = Enumerable.Range(0, 300).Select(_ => Item(300, 200)).ToList();
            var (gallery, _, window) = Show(GalleryLayout.Masonry, items);

            Assert.True(gallery.IsFlowLayout);
            Assert.True(gallery.DesiredSize.Height > window.Height);
            Assert.NotEmpty(gallery.RealizedItems);
            Assert.True(gallery.RealizedItems.Count < items.Count / 3,
                $"Expected a fraction of the items to be realized, got {gallery.RealizedItems.Count}");

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void FlowLayout_RecordsTheSizeEachItemIsShownAt_ForItsThumbnail()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            // One column wider than the minimum column width, the thumbnails are decoded at the wider size
            List<GalleryItemViewModel> items = [Item(300, 200), Item(100, 300), Item(4000, 100)];
            var (gallery, _, window) = Show(GalleryLayout.Masonry, items, wrapWidth: 180);

            Assert.NotEmpty(gallery.RealizedItems);
            foreach (var realized in gallery.RealizedItems)
            {
                var bounds = Bounds(gallery, realized.Index);
                Assert.Equal(180, bounds.Width, Tolerance);
                Assert.Equal(bounds.Width, items[realized.Index].DisplayWidth, Tolerance);
                Assert.Equal(bounds.Height, items[realized.Index].DisplayHeight, Tolerance);
            }

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void SwitchingToColumns_LeavesTheFlowLayout()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            var (gallery, _, window) = Show(GalleryLayout.Rows, [Item(300, 200), Item(300, 200)], wrapWidth: 500);
            Assert.True(gallery.IsFlowLayout);
            Assert.Empty(gallery.GetIrregularSnapPoints(Orientation.Horizontal, SnapPointsAlignment.Near));

            gallery.Layout = GalleryLayout.Columns;
            Assert.False(gallery.IsFlowLayout);

            gallery.Layout = GalleryLayout.Masonry;
            gallery.IsExpanded = false;
            Assert.False(gallery.IsFlowLayout);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void Masonry_UpAndDownStayInTheColumn_LeftAndRightMoveToTheNeighbouringColumn()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Columns: [0, 4], [1, 3], [2]
            var (_, viewer, window) = Show(GalleryLayout.Masonry,
                [Item(100, 100), Item(200, 100), Item(100, 300), Item(100, 100), Item(100, 100)], wrapWidth: 420);

            viewer.SelectedItemIndex = 0;
            viewer.Navigate(NavigationDirection.Down);
            Assert.Equal(4, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Down);
            Assert.Equal(4, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Right);
            Assert.Equal(3, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Up);
            Assert.Equal(1, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Right);
            Assert.Equal(2, viewer.SelectedItemIndex);

            // The tall item spans both items of the second column, the one closest to its center wins
            viewer.Navigate(NavigationDirection.Left);
            Assert.Equal(3, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Left);
            Assert.Equal(4, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Up);
            Assert.Equal(0, viewer.SelectedItemIndex);

            window.Close();
        }));
    }

    [AvaloniaFact]
    public void Rows_LeftAndRightFollowTheReadingOrder_UpAndDownMoveBetweenRows()
    {
        RunWithDispatcher(Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Rows: [0, 1, 2, 3], [4, 5]
            var (_, viewer, window) = Show(GalleryLayout.Rows,
                [Item(300, 200), Item(300, 200), Item(300, 200), Item(300, 200), Item(100, 100), Item(200, 100)],
                wrapWidth: 500);

            viewer.SelectedItemIndex = 3;
            viewer.Navigate(NavigationDirection.Right);
            Assert.Equal(4, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Right);
            viewer.Navigate(NavigationDirection.Right);
            Assert.Equal(5, viewer.SelectedItemIndex);

            // Item 5 spans 110 to 310, under items 1 and 2: item 1 is closer to its center
            viewer.Navigate(NavigationDirection.Up);
            Assert.Equal(1, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Left);
            Assert.Equal(0, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Left);
            Assert.Equal(0, viewer.SelectedItemIndex);
            viewer.Navigate(NavigationDirection.Down);
            Assert.Equal(4, viewer.SelectedItemIndex);

            window.Close();
        }));
    }
}
