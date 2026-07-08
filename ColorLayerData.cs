using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Autodraw;

public class ColorLayerData
{
    public Color LayerColor { get; set; }
    public List<Vector2> Pixels { get; set; } = new();
    public Bitmap LayerPreviewBitmap { get; set; }
    public string HexColor => $"{LayerColor.R:X2}{LayerColor.G:X2}{LayerColor.B:X2}";
    public int PixelCount => Pixels.Count;
}
