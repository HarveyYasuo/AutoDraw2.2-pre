using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;
using ImageMagick;
using SkiaSharp;
using Autodraw;

namespace Autodraw.Algorithms;

public class ImageMagickQuantizer : IQuantizer, IDisposable
{
    private MagickImage _mImage;
    private Dictionary<Color, int> _colorHistogram;

    public SKBitmap Quantize(SKBitmap source, byte colorCount, Color backgroundColor)
    {
        var enc = source.Encode(SKEncodedImageFormat.Png, 100);
        var stream = enc.AsStream();
        _mImage = new MagickImage(stream);

        _mImage.BackgroundColor = new MagickColor(backgroundColor.R, backgroundColor.G, backgroundColor.B);
        _mImage.Quantize(new QuantizeSettings
        {
            Colors = colorCount,
            DitherMethod = DitherMethod.No,
            TreeDepth = 8
        });
        _mImage.Alpha(AlphaOption.Remove);

        byte[] imageBytes;
        using (var ms = new MemoryStream())
        {
            _mImage.Write(ms);
            imageBytes = ms.ToArray();
        }

        var newBitmap = SKBitmap.Decode(imageBytes).NormalizeColor();

        _colorHistogram = new Dictionary<Color, int>();
        foreach (var (key, value) in _mImage.Histogram())
        {
            _colorHistogram[new Color(key.A, key.R, key.G, key.B)] = value;
        }

        return newBitmap;
    }

    public Dictionary<Color, int> GetColorHistogram()
    {
        return _colorHistogram;
    }

    public void Dispose()
    {
        _mImage?.Dispose();
    }
}
