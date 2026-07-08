using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Autodraw.Algorithms;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Autodraw;

public static class ImageSplitting
{
    public static byte Colors = 12;
    public static Color BackgroundColor = new(255, 0, 0, 0);
    public static int SourceWidth;
    public static int SourceHeight;

    public static List<ColorLayerData> ProcessInMemory(SKBitmap source, byte colorCount, Color backgroundColor)
    {
        Colors = colorCount;
        BackgroundColor = backgroundColor;
        SourceWidth = source.Width;
        SourceHeight = source.Height;

        using var quantizer = new ImageMagickQuantizer();
        var quantizedBitmap = quantizer.Quantize(source, colorCount, backgroundColor);
        var histogram = quantizer.GetColorHistogram();

        var layers = ExtractLayers(quantizedBitmap, histogram);
        GenerateLayerPreviews(layers, source.Width, source.Height);

        return layers;
    }

    private static unsafe List<ColorLayerData> ExtractLayers(SKBitmap quantizedBitmap, Dictionary<Color, int> histogram)
    {
        var width = quantizedBitmap.Width;
        var height = quantizedBitmap.Height;
        var colorToLayer = new Dictionary<uint, List<Vector2>>();

        foreach (var (color, _) in histogram)
        {
            var key = MakeColorKey(color);
            colorToLayer[key] = new List<Vector2>();
        }

        var ptr = (byte*)quantizedBitmap.GetPixels().ToPointer();
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var b = *ptr++;
            var g = *ptr++;
            var r = *ptr++;
            var a = *ptr++;

            var key = MakeColorKey(Color.FromArgb(a, r, g, b));
            if (colorToLayer.TryGetValue(key, out var pixels))
            {
                pixels.Add(new Vector2(x, y));
            }
        }

        var result = new List<ColorLayerData>();
        foreach (var (color, _) in histogram)
        {
            var key = MakeColorKey(color);
            if (colorToLayer.TryGetValue(key, out var pixels) && pixels.Count > 0)
            {
                result.Add(new ColorLayerData
                {
                    LayerColor = color,
                    Pixels = pixels
                });
            }
        }

        return result;
    }

    private static uint MakeColorKey(Color c)
    {
        return (uint)((c.R << 16) | (c.G << 8) | c.B);
    }

    private static void GenerateLayerPreviews(List<ColorLayerData> layers, int srcWidth, int srcHeight)
    {
        foreach (var layer in layers)
        {
            layer.LayerPreviewBitmap = GenerateThumbnail(layer, srcWidth, srcHeight, 64, 64);
        }
    }

    private static Bitmap GenerateThumbnail(ColorLayerData layer, int srcWidth, int srcHeight, int thumbW, int thumbH)
    {
        var color = layer.LayerColor;
        using var thumb = new SKBitmap(thumbW, thumbH);
        thumb.Erase(SKColors.Transparent);

        var scaleX = (float)thumbW / srcWidth;
        var scaleY = (float)thumbH / srcHeight;

        using var canvas = new SKCanvas(thumb);
        using var paint = new SKPaint
        {
            Color = new SKColor(color.R, color.G, color.B, color.A),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        foreach (var px in layer.Pixels)
        {
            var tx = px.X * scaleX;
            var ty = px.Y * scaleY;
            canvas.DrawCircle(tx, ty, 0.6f, paint);
        }

        canvas.Flush();

        using var enc = SKImage.FromBitmap(thumb).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = enc.AsStream();
        return new Bitmap(stream);
    }

    public static unsafe Bitmap? GenerateLayerFullPreview(ColorLayerData layer, int? width = null, int? height = null)
    {
        var w = width ?? SourceWidth;
        var h = height ?? SourceHeight;
        if (w == 0 || h == 0) return null;

        var color = layer.LayerColor;
        using var bmp = new SKBitmap(w, h);
        var ptr = (byte*)bmp.GetPixels().ToPointer();

        foreach (var px in layer.Pixels)
        {
            var offset = ((int)px.Y * w + (int)px.X) * 4;
            ptr[offset] = color.B;
            ptr[offset + 1] = color.G;
            ptr[offset + 2] = color.R;
            ptr[offset + 3] = 255;
        }

        using var enc = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = enc.AsStream();
        return new Bitmap(stream);
    }
}
