using System.Security.Cryptography;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FlexScreen.Companion;

/// <summary>A square RGB565 image ready for the deck. <c>Id</c> is a content hash.</summary>
record Art(string Id, int Width, int Height, byte[] Pixels);

static class AlbumArt
{
    /// <summary>Center-crops the media session's thumbnail to a square and scales it to <paramref name="size"/>.</summary>
    public static async Task<Art?> LoadAsync(IRandomAccessStreamReference? thumbnail, int size)
    {
        if (thumbnail == null) return null;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var scale = (double)size / Math.Min(decoder.PixelWidth, decoder.PixelHeight);
            var scaledW = (uint)Math.Ceiling(decoder.PixelWidth * scale);
            var scaledH = (uint)Math.Ceiling(decoder.PixelHeight * scale);
            var transform = new BitmapTransform
            {
                ScaledWidth = scaledW,
                ScaledHeight = scaledH,
                InterpolationMode = BitmapInterpolationMode.Fant,
                Bounds = new BitmapBounds
                {
                    X = (scaledW - (uint)size) / 2,
                    Y = (scaledH - (uint)size) / 2,
                    Width = (uint)size,
                    Height = (uint)size,
                },
            };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                                                       ExifOrientationMode.RespectExifOrientation,
                                                       ColorManagementMode.DoNotColorManage);
            var pixels = ToRgb565(data.DetachPixelData());
            var id = Convert.ToHexString(SHA1.HashData(pixels))[..12].ToLowerInvariant();
            return new Art(id, size, size, pixels);
        }
        catch (Exception e)
        {
            Log.Write($"art: {e.Message}");
            return null;
        }
    }

    /// <summary>BGRA8 → little-endian RGB565, LVGL's native 16-bit format on the ESP32.</summary>
    static byte[] ToRgb565(byte[] bgra)
    {
        var output = new byte[bgra.Length / 2];
        for (int i = 0, o = 0; i < bgra.Length; i += 4, o += 2)
        {
            var b = bgra[i];
            var g = bgra[i + 1];
            var r = bgra[i + 2];
            var v = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
            output[o] = (byte)v;
            output[o + 1] = (byte)(v >> 8);
        }
        return output;
    }
}
