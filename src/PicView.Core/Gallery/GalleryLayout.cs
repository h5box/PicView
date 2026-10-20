namespace PicView.Core.Gallery;

/// <summary>
/// How the expanded gallery arranges its thumbnails.
/// </summary>
public enum GalleryLayout
{
    /// <summary>
    /// Columns of thumbnails with the same height, scrolling horizontally.
    /// </summary>
    Columns = 0,
    /// <summary>
    /// Columns with the same width, each thumbnail keeping its aspect ratio, scrolling vertically.
    /// </summary>
    Masonry = 1,
    /// <summary>
    /// Rows that fill the width, each thumbnail keeping its aspect ratio, scrolling vertically.
    /// </summary>
    Rows = 2,
}
