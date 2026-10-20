using System.Collections.Concurrent;
using ImageMagick;
using PicView.Core.DebugTools;
using PicView.Core.MotionPhoto;
using PicView.Core.Navigation.Interfaces;
using PicView.Core.ViewModels;

namespace PicView.Core.Gallery;

public class GalleryCoordinator(TabViewModel tab, IThumbnailLoader thumbnailLoader, IThumbnailCache thumbnailCache)
    : IDisposable
{
    /// Larger thumbnails than this cost more memory than they add detail at gallery sizes
    private const uint MaxFlowThumbnailHeight = 1024;

    private CancellationTokenSource? _cts;

    // The height each cached thumbnail was decoded at, so a layout that shows it larger can replace it
    private readonly ConcurrentDictionary<string, uint> _thumbnailHeights = new(StringComparer.OrdinalIgnoreCase);

    public void LoadGallery(IReadOnlyList<FileInfo> files, CoreViewModel core)
    {
        if (tab.Gallery.LoadingState is GalleryLoadingState.Loading or GalleryLoadingState.Loaded)
        {
            return;
        }

        if (tab.Gallery.GalleryItems.Count > 0)
        {
            tab.Gallery.GalleryItems.Clear();
        }

        tab.Gallery.LoadingState = GalleryLoadingState.Loading;
        _cts = new CancellationTokenSource();

        var ct = _cts.Token;
        
        var dockedHeight = Settings.Gallery.DockedGalleryItemSize;
        var expandedHeight = Settings.Gallery.ExpandedGalleryItemSize;
        var maxHeight = Math.Max(dockedHeight, expandedHeight);
        if (maxHeight <= 0)
        {
            maxHeight = GalleryDefaults.DefaultDockedGalleryHeight;
        }

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        const int batchSize = 20;

        try
        {
            for (var i = 0; i < files.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                var currentBatchSize = Math.Min(batchSize, files.Count - i);
                var batchVms = new GalleryItemViewModel[currentBatchSize];

                // 1. Parallelize the metadata extraction (Massive speed boost)
                Parallel.For(0, currentBatchSize, parallelOptions, j =>
                {
                    var file = files[i + j];
                    var item = new GalleryItemViewModel { FileInfo = file };
                    string? xmpPacket = null;

                    try
                    {
                        using var magick = new MagickImage();
#pragma warning disable MA0042
                        // ReSharper disable once MethodHasAsyncOverloadWithCancellation
                        magick.Ping(file);
#pragma warning restore MA0042
                        item.PixelWidth = magick.Width;
                        item.PixelHeight = magick.Height;
                        if (IsRotatedByOrientation(magick))
                        {
                            // The thumbnails are rotated upright, lay them out by their rotated size
                            (item.PixelWidth, item.PixelHeight) = (item.PixelHeight, item.PixelWidth);
                        }

                        var profile = magick.GetProfile("xmp");
                        if (profile is not null)
                        {
                            var data = profile.ToByteArray();
                            if (data is not null)
                            {
                                xmpPacket = System.Text.Encoding.UTF8.GetString(data);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
#if DEBUG
                        DebugHelper.LogDebug(nameof(GalleryCoordinator), nameof(LoadGallery), ex);
#endif
                    }

                    var thumbData =
                        GalleryThumbInfo.GalleryThumbHolder.GetThumbData(file, item.PixelWidth, item.PixelHeight);
                    item.FileName.Value = thumbData.FileName;
                    item.FileSize.Value = thumbData.FileSize;
                    item.FileDate.Value = thumbData.FileDate;
                    item.FileLocation.Value = thumbData.FileLocation;
                    item.ImageSize.Value = thumbData.ImageSize;

                    // 2. Assign the lazy-loading logic, but don't execute it!
                    item.ThumbnailLoaderFunc = async cancellationToken =>
                    {
                        var height = GetThumbnailHeight(item, (uint)maxHeight, core);
                        if (thumbnailCache.TryGet(file.FullName, out var cached) && cached is not null &&
                            (!_thumbnailHeights.TryGetValue(file.FullName, out var cachedHeight) || height <= cachedHeight))
                        {
                            return cached;
                        }

                        return await Task.Run(async () =>
                        {
                            var thumb = await thumbnailLoader.GetThumbnailAsync(file, height, core)
                                .ConfigureAwait(false);
                            if (thumb is not null)
                            {
                                thumbnailCache.Add(tab.Id, file.FullName, thumb);
                                _thumbnailHeights[file.FullName] = height;
                            }

                            item.IsMotionPhoto.Value = MotionPhotoDetector.TryDetect(item.FileInfo, xmpPacket, fastMode: true) is not null;

                            return thumb;
                        }, cancellationToken).ConfigureAwait(false);
                    };

                    // Array assignment ensures perfect sorting order
                    batchVms[j] = item;
                });

                // 3. Add chunk directly to the UI
                tab.Gallery.GalleryItems.AddRange(batchVms);
            }
        }
        catch (OperationCanceledException)
        {
            if (tab.Gallery.LoadingState is GalleryLoadingState.Restarting && tab.Gallery.GalleryItems.Count > 0)
            {
                tab.Gallery.GalleryItems.Clear();
            }

            tab.Gallery.LoadingState = GalleryLoadingState.NotLoaded;
            return;
        }

        tab.Gallery.LoadingState = GalleryLoadingState.Loaded;
    }

    private static bool IsRotatedByOrientation(MagickImage magick)
    {
        var orientation = magick.Orientation;
        if (orientation is Orientation.Undefined)
        {
            var value = magick.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value;
            if (value is >= (ushort)Orientation.TopLeft and <= (ushort)Orientation.LeftBottom)
            {
                orientation = (Orientation)value.Value;
            }
        }

        return orientation is Orientation.LeftTop or Orientation.RightTop
            or Orientation.RightBottom or Orientation.LeftBottom;
    }

    /// <summary>
    /// Gets the height to decode a thumbnail at. The masonry and row layouts show thumbnails larger than the
    /// item size, so they get them at the size they are shown at, in physical pixels.
    /// </summary>
    private uint GetThumbnailHeight(GalleryItemViewModel item, uint defaultHeight, CoreViewModel core)
    {
        if (item.DisplayHeight <= 0 || Settings.Gallery.ExpandedGalleryLayout is GalleryLayout.Columns ||
            !tab.Gallery.IsGalleryExpanded.CurrentValue)
        {
            return defaultHeight;
        }

        // The thumbnail fills the item, cropped to its aspect ratio, so it has to cover both its width and height
        var height = item.DisplayHeight;
        if (item.PixelWidth > 0 && item.PixelHeight > 0)
        {
            height = Math.Max(height, item.DisplayWidth * item.PixelHeight / item.PixelWidth);
        }

        height *= Math.Max(1, core.GallerySettings.ThumbnailScaling);
        var maxHeight = item.PixelHeight > 0 ? Math.Min(item.PixelHeight, MaxFlowThumbnailHeight) : MaxFlowThumbnailHeight;
        return Math.Max(defaultHeight, Math.Min((uint)Math.Ceiling(height), maxHeight));
    }

    public void ReloadGallery(IReadOnlyList<FileInfo> files, CoreViewModel core)
    {
        tab.Gallery.LoadingState = GalleryLoadingState.Restarting;
        tab.Gallery.GalleryItems.Clear();
        Cancel();
        LoadGallery(files, core);
    }

    public async ValueTask ToggleGalleryAndLoadItem(int index)
    {
        var gallery = tab.Gallery;
        gallery.SelectedGalleryItemIndex.Value = -1;
        if (gallery.IsGalleryExpanded.Value)
        {
            GalleryManager.ToggleGallery(gallery);
        }

        await tab.ImageIterator!.SkipToIndexAsync(index, tab.GetTabCancellation())
            .ConfigureAwait(false);
    }

    public void SortLoadedGallery(IReadOnlyList<FileInfo> files)
    {
        if (tab.Gallery.GalleryItems is null || tab.Gallery.GalleryItems.Count <= 1 || files is null ||
            files.Count is 0)
        {
            return;
        }

        var orderMap = new Dictionary<string, int>(files.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            orderMap.TryAdd(files[i].FullName, i);
        }

        var comparer = Comparer<GalleryItemViewModel>.Create((x, y) =>
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return 1;
            }

            if (y is null)
            {
                return -1;
            }

            var xPath = x.FileInfo?.FullName;
            var yPath = y.FileInfo?.FullName;

            var indexX = -1;
            var indexY = -1;
            var hasX = xPath is not null && orderMap.TryGetValue(xPath, out indexX);
            var hasY = yPath is not null && orderMap.TryGetValue(yPath, out indexY);

            return hasX switch
            {
                true when hasY => indexX.CompareTo(indexY),
                true => -1,
                _ => hasY ? 1 : 0
            };
        });

        tab.Gallery.GalleryItems.Sort(comparer);
    }

    public void Cancel()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
