using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using SkiaSharp;

namespace Autodraw;

public partial class MainWindow : Window
{
    public static MainWindow? CurrentMainWindow;

    private readonly Regex _numberRegex = new(@"[^0-9]");
    private SKBitmap? _rawBitmap;
    private SKBitmap? _processedBitmap;
    private Bitmap? _displayedBitmap;
    private List<ColorLayerData> _currentLayers = new();
    private Color _bgColor = new(255, 0, 0, 0);
    private int _minBlackThreshold;
    private int _maxBlackThreshold = 127;
    private int _alphaThresh = 127;
    private bool _inChange;
    private bool _layersDirty = true;
    public int widthLock;
    public int heightLock;
    public int widthNumber = 1;
    public int heightNumber = 1;

    public MainWindow()
    {
        InitializeComponent();

        if (Design.IsDesignMode) return;

        this.AttachDevTools();

        var installedLanguage = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
        Thread.CurrentThread.CurrentCulture = new CultureInfo(Config.GetEntry("userlang") ?? installedLanguage);
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(Config.GetEntry("userlang") ?? installedLanguage);

        CurrentMainWindow = this;
        Config.init();

        CloseAppButton.Click += (_, _) => Close();
        MinimizeAppButton.Click += (_, _) => WindowState = WindowState.Minimized;
        SettingsButton.Click += (_, _) => new Settings().Show();
        DevButton.Click += (_, _) => new DevTest().Show();

        ImportButton.Click += ImportButtonOnClick;
        SaveImageButton.Click += SaveImageOnClick;
        ClearImageButton.Click += ClearImageOnClick;
        RefreshPreviewButton.Click += (_, _) => UpdatePreview();
        ProcessButton.Click += ProcessButtonOnClick;
        StartDrawingButton.Click += StartDrawingOnClick;
        ExportLayersButton.Click += ExportLayersOnClick;
        ExportLayersMemoryButton.Click += ExportLayersOnClick;

        BgColorButton.Click += OpenBgColorPicker;
        CloseColorPicker.Click += CloseColorPickerClick;
        ColorCountBox.TextChanged += OnQuantizeParameterChanged;

        ImageSaveImage.Click += SaveImageOnClick;
        ImageClearImage.Click += ClearImageOnClick;

        BgColorButton.Background = new SolidColorBrush(_bgColor);
        ColorPickerView.Palette = new SixteenColorPalette();

        // Drawing speed controls
        DrawIntervalBox.TextChanging += (_, e) => { HandleTextChange(e); Drawing.Interval = int.TryParse(DrawIntervalBox.Text, out var v) ? v : 7500; };
        ClickDelayBox.TextChanging += (_, e) => { HandleTextChange(e); Drawing.ClickDelay = int.TryParse(ClickDelayBox.Text, out var v) ? v : 600; };

        // Thresholds
        MinThreshBox.TextChanging += (_, e) => { HandleTextChange(e); _minBlackThreshold = int.TryParse(MinThreshBox.Text, out var v) ? v : 0; };
        MaxThreshBox.TextChanging += (_, e) => { HandleTextChange(e); _maxBlackThreshold = int.TryParse(MaxThreshBox.Text, out var v) ? v : 127; };
        AlphaThreshBox.TextChanging += (_, e) => { HandleTextChange(e); _alphaThresh = int.TryParse(AlphaThreshBox.Text, out var v) ? v : 127; };

        FreeDrawCheckbox.Click += (_, _) => Drawing.FreeDraw2 = FreeDrawCheckbox.IsChecked ?? false;
        AutoAdvanceCheckbox.Click += (_, _) => Drawing.AutoAdvance = AutoAdvanceCheckbox.IsChecked ?? false;

        // Size controls
        WidthInput.TextChanging += WidthInputOnTextChanged;
        HeightInput.TextChanging += HeightInputOnTextChanged;
        WidthInput.LostFocus += (_, _) => ApplyResizeFromWidthInput();
        HeightInput.LostFocus += (_, _) => ApplyResizeFromHeightInput();
        WidthInput.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ApplyResizeFromWidthInput(); };
        HeightInput.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) ApplyResizeFromHeightInput(); };
        WidthLock.Click += (_, _) => { widthLock = widthLock > 0 ? 0 : widthNumber; };
        HeightLock.Click += (_, _) => { heightLock = heightLock > 0 ? 0 : heightNumber; };

        // Filter text handlers
        EventHandler<TextChangingEventArgs> filterTextHandler = (_, e) => HandleTextChange(e);
        HorizontalFilterText.TextChanging += filterTextHandler;
        VerticalFilterText.TextChanging += filterTextHandler;

        // Config management
        RefreshConfigsButton.Click += RefreshConfigList;
        SelectFolderElement.Click += SetConfigFolderViaDialog;
        SaveConfigButton.Click += SaveConfigViaDialog;
        OpenConfigElement.Click += LoadConfigViaDialog;
        LoadSelectButton.Click += LoadSelectedConfig;
        RefreshConfigList(null, null);

        LayerList.SelectionChanged += LayerListOnSelectionChanged;

        Input.Start();
    }

    // === IMAGE HANDLING ===

    private async void ImportButtonOnClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Image",
            FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
            AllowMultiple = false
        });
        if (file.Count == 1)
        {
            var path = file[0].TryGetLocalPath();
            if (path != null) ImportImage(path);
        }
    }

    public void ImportImage(string? path, byte[]? img = null)
    {
        _rawBitmap?.Dispose();
        _rawBitmap = img is null ? SKBitmap.Decode(path).NormalizeColor() : SKBitmap.Decode(img).NormalizeColor();
        _processedBitmap = null;
        _currentLayers.Clear();
        LayerList.Items.Clear();
        _layersDirty = true;
        UpdatePreview();
        ImageInfoText.Content = $"{_rawBitmap.Width}x{_rawBitmap.Height}px";
        WidthInput.Text = _rawBitmap.Width.ToString();
        HeightInput.Text = _rawBitmap.Height.ToString();
    }

    private void UpdatePreview()
    {
        var src = _processedBitmap ?? _rawBitmap;
        if (src == null) return;
        _displayedBitmap?.Dispose();
        _displayedBitmap = src.ConvertToAvaloniaBitmap();
        ImagePreview.Image = _displayedBitmap;
    }

    private async void SaveImageOnClick(object? sender, RoutedEventArgs e)
    {
        var src = _processedBitmap ?? _rawBitmap;
        if (src is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } }
        });
        if (file is not null)
        {
            var encodedData = src.Encode(SKEncodedImageFormat.Png, 100);
            await using var stream = await file.OpenWriteAsync();
            encodedData.SaveTo(stream);
        }
    }

    private void ClearImageOnClick(object? sender, RoutedEventArgs e)
    {
        _rawBitmap?.Dispose();
        _rawBitmap = null;
        _processedBitmap?.Dispose();
        _processedBitmap = null;
        _displayedBitmap = null;
        _currentLayers.Clear();
        ImagePreview.Image = null;
        ImageInfoText.Content = "No image loaded";
        LayerList.Items.Clear();
    }

    // === PROCESSING ===

    private void ProcessButtonOnClick(object? sender, RoutedEventArgs e)
    {
        if (_rawBitmap == null) return;
        var filters = GetSelectFilters();
        _processedBitmap?.Dispose();
        _processedBitmap = ImageProcessing.Process(_rawBitmap, filters);
        UpdatePreview();
        _layersDirty = false;
        var oldHex = LayerList.SelectedItem is ColorLayerData s ? s.HexColor : null;
        UpdateLayers();
        if (oldHex != null)
        {
            foreach (var item in LayerList.Items)
                if (item is ColorLayerData cl && cl.HexColor == oldHex)
                    { LayerList.SelectedItem = item; break; }
        }
    }

    private ImageProcessing.Filters GetSelectFilters()
    {
        ImageProcessing._currentFilters.MinThreshold = (byte)_minBlackThreshold;
        ImageProcessing._currentFilters.MaxThreshold = (byte)_maxBlackThreshold;
        ImageProcessing._currentFilters.AlphaThreshold = (byte)_alphaThresh;
        ImageProcessing._currentFilters.Invert = InvertFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.Outline = OutlineFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.Crosshatch = CrosshatchFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.DiagCrosshatch = DiagCrossFilterCheck.IsChecked ?? false;
        ImageProcessing._currentFilters.HorizontalLines = int.Parse(HorizontalFilterText.Text ?? "0");
        ImageProcessing._currentFilters.VerticalLines = int.Parse(VerticalFilterText.Text ?? "0");
        return ImageProcessing._currentFilters;
    }

    // === QUANTIZATION ===

    public void UpdateLayers()
    {
        if (_rawBitmap == null) return;
        var colorCount = byte.TryParse(ColorCountBox.Text, out var c) ? c : (byte)12;
        if (colorCount < 1) colorCount = 1;
        if (colorCount > 255) colorCount = 255;

        try
        {
            _currentLayers = ImageSplitting.ProcessInMemory(_rawBitmap, colorCount, _bgColor);
            if (LumSortCheckbox.IsChecked ?? false)
                _currentLayers = PathOptimizer.SortByLuminance(_currentLayers, darkestFirst: false);

            LayerList.Items.Clear();
            foreach (var layer in _currentLayers)
                LayerList.Items.Add(layer);
            _layersDirty = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Quantization error: {ex.Message}");
        }
    }

    private int _debounceKey;
    private void OnQuantizeParameterChanged(object? sender, TextChangedEventArgs e)
    {
        var thread = new Thread(() =>
        {
            var key = new Random().Next();
            _debounceKey = key;
            Thread.Sleep(400);
            if (_debounceKey == key)
                Dispatcher.UIThread.Invoke(UpdateLayers);
        });
        thread.Start();
    }

    private async void ExportLayersOnClick(object? sender, RoutedEventArgs e)
    {
        if (_currentLayers.Count == 0) return;
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export layers to folder" });
        if (folder.Count != 1) return;
        var path = folder[0].TryGetLocalPath();
        if (path == null) return;

        foreach (var layer in _currentLayers)
        {
            using var bitmap = new SKBitmap(64, 64);
            bitmap.Erase(SKColors.Transparent);
            using var canvas = new SKCanvas(bitmap);
            var color = layer.LayerColor;
            canvas.Clear(new SKColor(color.R, color.G, color.B, 255));
            canvas.Flush();
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var fileName = Path.Combine(path, $"{layer.HexColor.Replace("#", "")}.png");
            await using var fs = File.Create(fileName);
            encoded.SaveTo(fs);
        }
    }

    // === LAYER SELECTION ===

    private void LayerListOnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LayerList.SelectedItem is ColorLayerData layer)
        {
            try
            {
                _displayedBitmap?.Dispose();
                _displayedBitmap = ImageSplitting.GenerateLayerFullPreview(layer);
                ImagePreview.Image = _displayedBitmap;
                ImageInfoText.Content = $"{layer.HexColor} — {layer.PixelCount} px";
            }
            catch { }

            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is { } clipboard)
                {
                    clipboard.SetTextAsync(layer.HexColor);
                }
            }
            catch { }
        }
        else
        {
            UpdatePreview();
        }
    }

    // === DRAWING ORCHESTRATION ===

    private async void StartDrawingOnClick(object? sender, RoutedEventArgs e)
    {
        if (_rawBitmap == null) return;

        // Only re-process if no layers exist yet (new image without Process)
        if (_currentLayers.Count == 0 || _layersDirty)
        {
            UpdateLayers();
            if (_currentLayers.Count == 0) return;
            _layersDirty = false;
        }

        // Use the CURRENT selection directly — same objects as _currentLayers
        List<ColorLayerData> drawLayers;
        if (LayerList.SelectedItem is ColorLayerData selectedLayer &&
            _currentLayers.Contains(selectedLayer))
        {
            var sorted = PathOptimizer.SortByLuminance(new List<ColorLayerData> { selectedLayer }, darkestFirst: false);
            drawLayers = sorted;
        }
        else
        {
            drawLayers = PathOptimizer.SortByLuminance(_currentLayers, darkestFirst: false);
        }

        Drawing.Interval = int.TryParse(DrawIntervalBox.Text, out var interval) ? interval : 7500;
        Drawing.ClickDelay = int.TryParse(ClickDelayBox.Text, out var clickDelay) ? clickDelay : 600;
        Drawing.ChosenAlgorithm = (byte)DrawAlgorithmBox.SelectedIndex;

        var actionStack = new List<InputAction>();
        if (ColorPickerEnabled.IsChecked ?? true)
        {
            var pickerX = int.TryParse(ColorPickerX.Text, out var px) ? px : 1670;
            var pickerY = int.TryParse(ColorPickerY.Text, out var py) ? py : 820;
            var pickerY2 = int.TryParse(ColorPickerY2.Text, out var py2) ? py2 : 824;
            actionStack = new List<InputAction>
            {
                new(InputAction.ActionType.MoveTo, new Vector2(pickerX, pickerY)),
                new(InputAction.ActionType.LeftClick),
                new(InputAction.ActionType.MoveTo, new Vector2(pickerX, pickerY2)),
                new(InputAction.ActionType.LeftClick),
                new(InputAction.ActionType.LeftClick),
                new(InputAction.ActionType.LeftClick),
                new(InputAction.ActionType.KeyDown, "VcLeftControl"),
                new(InputAction.ActionType.KeyDown, "VcA"),
                new(InputAction.ActionType.KeyUp, "VcLeftControl"),
                new(InputAction.ActionType.KeyUp, "VcA"),
                new(InputAction.ActionType.WriteString, "{colorHex}")
            };
        }

        var previewSrc = _processedBitmap ?? _rawBitmap;
        if (previewSrc == null) return;

        // If drawing only one selected layer, show just that layer in preview
        Bitmap previewBitmap;
        if (drawLayers.Count == 1)
        {
            previewBitmap = ImageSplitting.GenerateLayerFullPreview(drawLayers[0])
                            ?? previewSrc.ConvertToAvaloniaBitmap();
        }
        else
        {
            previewBitmap = previewSrc.ConvertToAvaloniaBitmap();
        }

        var preview = new Preview();
        preview.ReadyColorLayerDraw(previewBitmap, drawLayers, actionStack);
        WindowState = WindowState.Minimized;
    }

    // === COLOR PICKER ===

    private void OpenBgColorPicker(object? sender, RoutedEventArgs e)
    {
        ColorPaletteOverlay.IsVisible = true;
        ColorPickerView.ColorChanged += OnBgColorChanged;
    }

    private void OnBgColorChanged(object? sender, ColorChangedEventArgs e)
    {
        _bgColor = e.NewColor;
        BgColorButton.Background = new SolidColorBrush(_bgColor);
    }

    private void CloseColorPickerClick(object? sender, RoutedEventArgs e)
    {
        ColorPaletteOverlay.IsVisible = false;
        ColorPickerView.ColorChanged -= OnBgColorChanged;
        UpdateLayers();
    }

    // === SIZE CONTROLS ===

    private void WidthInputOnTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (_inChange) return;
        var numText = _numberRegex.Replace(WidthInput.Text ?? "", "");
        _inChange = true; WidthInput.Text = numText; _inChange = false;
        e.Handled = true;
        if (numText.Length < 1) return;
    }

    private void HeightInputOnTextChanged(object? sender, TextChangingEventArgs e)
    {
        if (_inChange) return;
        var numText = _numberRegex.Replace(HeightInput.Text ?? "", "");
        _inChange = true; HeightInput.Text = numText; _inChange = false;
        e.Handled = true;
        if (numText.Length < 1) return;
    }

    private void ApplyResizeFromWidthInput()
    {
        if (_rawBitmap == null) return;
        if (!int.TryParse(WidthInput.Text, out var newWidth) || newWidth < 1) return;
        if (newWidth == _rawBitmap.Width) return;

        int newHeight;
        if (widthLock > 0)
        {
            var ratio = (double)_rawBitmap.Height / _rawBitmap.Width;
            newHeight = (int)Math.Round(newWidth * ratio);
            if (newHeight < 1) newHeight = 1;
            _inChange = true; HeightInput.Text = newHeight.ToString(); _inChange = false;
        }
        else
        {
            newHeight = _rawBitmap.Height;
        }

        ResizeRawBitmap(newWidth, newHeight);
    }

    private void ApplyResizeFromHeightInput()
    {
        if (_rawBitmap == null) return;
        if (!int.TryParse(HeightInput.Text, out var newHeight) || newHeight < 1) return;
        if (newHeight == _rawBitmap.Height) return;

        int newWidth;
        if (heightLock > 0)
        {
            var ratio = (double)_rawBitmap.Width / _rawBitmap.Height;
            newWidth = (int)Math.Round(newHeight * ratio);
            if (newWidth < 1) newWidth = 1;
            _inChange = true; WidthInput.Text = newWidth.ToString(); _inChange = false;
        }
        else
        {
            newWidth = _rawBitmap.Width;
        }

        ResizeRawBitmap(newWidth, newHeight);
    }

    private void ResizeRawBitmap(int newWidth, int newHeight)
    {
        var old = _rawBitmap;
        _rawBitmap = new SKBitmap(newWidth, newHeight);
        using (var canvas = new SKCanvas(_rawBitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(old, new SKRect(0, 0, newWidth, newHeight), paint);
        }
        old?.Dispose();
        _processedBitmap = null;
        _currentLayers.Clear();
        LayerList.Items.Clear();
        _layersDirty = true;
        UpdatePreview();
        ImageInfoText.Content = $"{newWidth}x{newHeight}px";
    }

    // === TEXT HELPERS ===

    private void HandleTextChange(TextChangingEventArgs e)
    {
        var src = (TextBox)e.Source;
        src.Text = _numberRegex.Replace(src.Text, "");
        e.Handled = true;
        if (src.Text.Length < 1) src.Text = "0";
    }

    // === CONFIG MANAGEMENT ===

    public static FilePickerFileType ConfigsFileFilter { get; } = new("AutoDraw Config Files")
    {
        Patterns = new[] { "*.drawcfg" }
    };

    public async void SetConfigFolderViaDialog(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folder.Count != 1) return;
        Config.SetEntry("ConfigFolder", folder[0].TryGetLocalPath());
        RefreshConfigList(null, null);
    }

    public void RefreshConfigList(object? sender, RoutedEventArgs? e)
    {
        var configFolder = Config.GetEntry("ConfigFolder");
        if (configFolder == null || !Directory.Exists(configFolder)) return;
        var files = Directory.GetFiles(configFolder, "*.drawcfg");
        var fileNames = files.Select(Path.GetFileNameWithoutExtension).ToArray();
        ConfigsListBox.Items.Clear();
        foreach (var name in fileNames) ConfigsListBox.Items.Add(name);
    }

    public void LoadSelectedConfig(object? sender, RoutedEventArgs e)
    {
        if (ConfigsListBox.SelectedItem == null) return;
        var selected = ConfigsListBox.SelectedItem.ToString();
        if (selected == null) return;
        var configFolder = Config.GetEntry("ConfigFolder");
        if (configFolder == null) return;
        LoadConfig(Path.Combine(configFolder, selected + ".drawcfg"));
    }

    public void LoadConfig(string? path)
    {
        if (path == null || !path.EndsWith(".drawcfg")) return;
        var lines = File.ReadAllLines(path);
        DrawIntervalBox.Text = lines.Length > 0 ? lines[0] : "7500";
        ClickDelayBox.Text = lines.Length > 1 ? lines[1] : "600";
        if (lines.Length > 4 && bool.TryParse(lines[4], out var fd2))
        {
            FreeDrawCheckbox.IsChecked = fd2;
            Drawing.FreeDraw2 = fd2;
        }
    }

    public async void SaveConfigViaDialog(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Config",
            FileTypeChoices = new[] { ConfigsFileFilter }
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var sw = new StreamWriter(stream);
        string?[] values = { DrawIntervalBox.Text, ClickDelayBox.Text, MaxThreshBox.Text, AlphaThreshBox.Text, FreeDrawCheckbox.IsChecked.ToString(), "", MinThreshBox.Text };
        await sw.WriteAsync(string.Join("\r\n", values));
    }

    public async void LoadConfigViaDialog(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Config",
            FileTypeFilter = new[] { ConfigsFileFilter },
            AllowMultiple = false
        });
        if (file.Count == 1) LoadConfig(file[0].TryGetLocalPath());
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.GetPosition(this).Y <= 20)
            BeginMoveDrag(e);
    }
}
