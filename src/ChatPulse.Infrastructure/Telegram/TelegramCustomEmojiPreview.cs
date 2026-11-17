using System.IO.Compression;
using System.Text;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>Converts Telegram's gzipped TGS/Lottie document into a still PNG preview.</summary>
internal static class TelegramCustomEmojiPreview
{
    private const int MAX_COMPRESSED_BYTES = 1024 * 1024;
    private const int MAX_JSON_BYTES = 4 * 1024 * 1024;
    private const int PREVIEW_SIZE = 128;

    /// <summary>Decode a WebP still thumbnail, which WPF cannot always open directly.</summary>
    public static bool TryConvertWebp(string sourcePath, string destinationPath)
    {
        if (new FileInfo(sourcePath).Length > MAX_COMPRESSED_BYTES) return false;
        try
        {
            using var bitmap = SKBitmap.Decode(sourcePath);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0 ||
                bitmap.Width > 1024 || bitmap.Height > 1024) return false;
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null) return false;
            using var output = File.Create(destinationPath);
            encoded.SaveTo(output);
            return true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool TryRenderTgs(string sourcePath, string destinationPath)
    {
        if (new FileInfo(sourcePath).Length > MAX_COMPRESSED_BYTES) return false;

        try
        {
            using var compressed = File.OpenRead(sourcePath);
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var jsonBuffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = gzip.Read(bytes, 0, bytes.Length)) > 0)
            {
                if (jsonBuffer.Length + read > MAX_JSON_BYTES) return false;
                jsonBuffer.Write(bytes, 0, read);
            }

            var json = Encoding.UTF8.GetString(jsonBuffer.ToArray());
            using var animation = Animation.Create(json);
            if (animation is null) return false;

            using var surface = SKSurface.Create(new SKImageInfo(PREVIEW_SIZE, PREVIEW_SIZE,
                SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return false;
            surface.Canvas.Clear(SKColors.Transparent);
            animation.SeekFrame(0);
            animation.Render(surface.Canvas, new SKRect(0, 0, PREVIEW_SIZE, PREVIEW_SIZE));
            surface.Canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null) return false;

            // The caller owns the temp file and atomically promotes it to the cache.
            using var output = File.Create(destinationPath);
            encoded.SaveTo(output);
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
                                        ArgumentException or InvalidOperationException or
                                        System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
