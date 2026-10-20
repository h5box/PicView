using PicView.Core.Sizing;

namespace PicView.Core.Gallery;

public static class GalleryDefaults
{
    public const int DefaultExpandedGalleryHeight = 150;
    public const int DefaultDockedGalleryHeight = 76;
    
    public const int MaxExpandedGalleryItemHeight = 250;
    public const int MinExpandedGalleryItemHeight = 50;
    
    public const int MaxDockedGalleryItemHeight = 150;
    public const int MinDockedGalleryItemHeight = 40;
    
    public const double VeryFastAnimationSpeed = 0.25;
    public const double FastAnimationSpeed = 0.35;
    public const double MediumAnimationSpeed = 0.5;
    public const double SlowAnimationSpeed = 0.6;
    
    public const int BorderTopAndBottomThickness = 2;
    public const int BorderSideThickness = 1;
    public static double GetDockedGalleryWidth => Settings.Gallery.DockedGalleryItemSize + BorderTopAndBottomThickness + SizeDefaults.HorizontalScrollbarSize;
    public static double GetDockedGalleryHeight => Settings.Gallery.DockedGalleryItemSize + BorderSideThickness + SizeDefaults.VerticalScrollbarSize;

    /// <summary>
    /// The expanded gallery item size is the thumbnail height of <see cref="GalleryLayout.Columns"/>.
    /// Masonry columns and justified rows show fewer, larger thumbnails, so they scale it up.
    /// </summary>
    public const double MasonryColumnWidthFactor = 4.0 / 3;
    public const double RowHeightFactor = 2;

    /// <summary>
    /// The aspect ratios the masonry and row layouts size items by. Extreme panoramas and long screenshots
    /// are cropped to these, instead of turning into slivers.
    /// </summary>
    public const double MinFlowAspectRatio = 1.0 / 3;
    public const double MaxFlowAspectRatio = 4;

    /// <summary>
    /// Gets the minimum column width of <see cref="GalleryLayout.Masonry"/>, or the target row height of
    /// <see cref="GalleryLayout.Rows"/>, for the expanded gallery item size.
    /// </summary>
    public static double GetFlowItemSize(GalleryLayout layout, double expandedItemSize) => layout switch
    {
        GalleryLayout.Masonry => expandedItemSize * MasonryColumnWidthFactor,
        GalleryLayout.Rows => expandedItemSize * RowHeightFactor,
        _ => expandedItemSize
    };
}
