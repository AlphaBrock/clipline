using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Clipline;

/// What a card shows: a small copy of the capture, never the capture itself.
/// A full screenshot, or the media stack that reads a recording, would stay
/// in memory for as long as the card hangs.
public static class Thumbnails
{
    /// One at a time: hanging the recent captures at launch would otherwise
    /// decode them all at once, each on its own thread, and the memory that
    /// takes is kept long after.
    private static readonly SemaphoreSlim OneAtATime = new(1);

    /// The image scaled down to `width` pixels, as a small PNG. The file is
    /// only open while it is read.
    public static async Task<IRandomAccessStream> ImageAsync(string path, int width)
    {
        await OneAtATime.WaitAsync();
        try { return await ScaledImageAsync(path, width); }
        finally { OneAtATime.Release(); }
    }

    private static async Task<IRandomAccessStream> ScaledImageAsync(string path, int width)
    {
        // Never in the way of Explorer moving or deleting the file.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        using var source = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(source);
        double scale = Math.Min(1, (double)width / Math.Max(1, decoder.OrientedPixelWidth));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return await EncodeAsync(pixels);
    }

    /// A frame from near the start of a recording, `width` pixels wide, and
    /// how long the recording is.
    ///
    /// Windows' own thumbnail and properties come first: they are usually
    /// cached already, and cost almost nothing to ask for. Reading the video
    /// here instead loads the whole media stack into Clipline for good.
    public static async Task<(IRandomAccessStream Frame, TimeSpan Length)> FirstFrameAsync(StorageFile file, int width)
    {
        await OneAtATime.WaitAsync();
        try { return await ShellFrameAsync(file, width); }
        finally { OneAtATime.Release(); }
    }

    private static async Task<(IRandomAccessStream Frame, TimeSpan Length)> ShellFrameAsync(StorageFile file, int width)
    {
        var length = (await file.Properties.GetVideoPropertiesAsync()).Duration;
        var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)width, ThumbnailOptions.ResizeThumbnail);
        if (thumbnail is { Type: ThumbnailType.Image } && length > TimeSpan.Zero) return (thumbnail, length);
        thumbnail?.Dispose();
        return await DecodedFrameAsync(file, width);
    }

    /// Taken a moment in, past the black frame some recordings open with.
    private static async Task<(IRandomAccessStream Frame, TimeSpan Length)> DecodedFrameAsync(StorageFile file, int width)
    {
        var clip = await MediaClip.CreateFromFileAsync(file);
        var video = clip.GetVideoEncodingProperties();
        int height = video.Width > 0 ? (int)Math.Round(width * (double)video.Height / video.Width) : width * 9 / 16;

        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var at = TimeSpan.FromMilliseconds(Math.Min(200, clip.OriginalDuration.TotalMilliseconds / 2));
        var frame = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
        return (frame, clip.OriginalDuration);
    }

    private static async Task<IRandomAccessStream> EncodeAsync(SoftwareBitmap pixels)
    {
        var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
        output.Seek(0);
        return output;
    }
}
