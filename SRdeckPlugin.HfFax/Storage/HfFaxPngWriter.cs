using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SRdeckPlugin.HfFax.Models;

namespace SRdeckPlugin.HfFax.Storage;

internal static class HfFaxPngWriter
{
    public static void Save(string path, HfFaxImageSnapshot image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 ||
            image.Pixels.Length != checked(image.Width * image.Height))
            throw new ArgumentException("The HF-FAX image dimensions are invalid.", nameof(image));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        BitmapSource bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96,
            PixelFormats.Gray8, null, image.Pixels, image.Width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
