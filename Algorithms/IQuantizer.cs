using System.Collections.Generic;
using Avalonia.Media;
using SkiaSharp;

namespace Autodraw.Algorithms;

public interface IQuantizer
{
    SKBitmap Quantize(SKBitmap source, byte colorCount, Color backgroundColor);
    Dictionary<Color, int> GetColorHistogram();
}
