using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using SharpHook;
using SharpHook.Native;
using SkiaSharp;

namespace Autodraw;

// This is solely for Inputs from the DrawStack stuff.
public class InputAction
{
    public enum ActionType
    {
        LeftClick,
        RightClick,
        MoveTo,
        WriteString,
        KeyDown,
        KeyUp
    }

    public ActionType Action { get; set; }
    public Vector2? Position { get; set; }
    public string? Data { get; set; }

    public InputAction(ActionType action, object? data = null)
    {
        Action = action;
        switch (action)
        {
            case ActionType.MoveTo:
                if (data is Vector2 pos)
                    Position = pos;
                break;

            case ActionType.WriteString:
            case ActionType.KeyDown:
            case ActionType.KeyUp:
                Data = data as string;
                break;
        }
    }

    public void PerformAction()
    {
        switch (Action)
        {
            case ActionType.MoveTo:
                Input.MoveTo((short)Position.Value.X, (short)Position.Value.Y);
                break;

            case ActionType.LeftClick:
                Input.SendClick(Input.MouseTypes.MouseLeft);
                break;

            case ActionType.RightClick:
                Input.SendClick(Input.MouseTypes.MouseRight);
                break;

            case ActionType.WriteString:
                Input.SendText(Data);
                break;

            case ActionType.KeyDown:
                if (Enum.TryParse(typeof(KeyCode), Data, true, out var kc1))
                    Input.SendKeyDown((KeyCode)kc1);
                break;

            case ActionType.KeyUp:
                if (Enum.TryParse(typeof(KeyCode), Data, true, out var kc2))
                    Input.SendKeyUp((KeyCode)kc2);
                break;
        }
    }
}

public static class Drawing
{
    // Variables

    public static int Interval = 10000;
    public static int ClickDelay = 1000;

    /// <summary>
    /// 0 indicates DFS, 1 indicates Edge-Following
    /// </summary>
    public static byte ChosenAlgorithm = 0;

    public static bool NoRescan = false;
    public static bool IsDrawing;
    public static bool SkipRescan;
    public static bool IsPaused;
    public static bool IsSkipping;
    public static bool FreeDraw2 = false;
    public static bool AutoAdvance = false;

    /// <summary>
    /// Maximum gap (in pixels) between two consecutive path points before
    /// the mouse click is released and re-pressed. A value of 1 means only
    /// truly adjacent pixels (including diagonals) are connected in a single
    /// stroke. Increase this value if you want to tolerate small gaps.
    /// </summary>
    public static int MaxGapPixels = 1;

    public static Vector2 LastPos = Config.Preview_LastLockPos;

    public static bool ShowPopup =
        Config.GetEntry("showPopup") == null || bool.Parse(Config.GetEntry("showPopup") ?? "true");

    private static DrawDataDisplay? _dataDisplay;
    private static int _totalScanSize;
    private static int _completeTotalScan;

    // Functions

    public static async Task NOP(long durationTicks)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedTicks < durationTicks)
            if (durationTicks - sw.ElapsedTicks > 150000)
                await Task.Delay(1);
    }

    // ─── FIX 1: Scan now reads the CORRECT color channel ─────────────
    /// <summary>
    /// Scans the bitmap and produces a 2D array where 1 = pixel to draw
    /// (dark) and 0 = empty (light).
    ///
    /// FIX: The original code read the first byte in memory assuming it was
    /// Red, but SkiaSharp typically uses BGRA byte order, so the first byte
    /// is actually Blue. This mismatch caused GetChunks (which correctly
    /// used Color.Red) and Scan to disagree on which pixels are "on",
    /// producing chunks that contained pixels absent from the dataArray.
    /// DFS then couldn't traverse those phantom connections, A* failed,
    /// and the path ended up with jumps that dragged the mouse.
    /// </summary>
    private static unsafe byte[,] Scan(SKBitmap bitmap)
    {
        _totalScanSize = 0;
        _completeTotalScan = 0;
        var pixelArray = new byte[bitmap.Width, bitmap.Height];
        var basePtr = (byte*)bitmap.GetPixels().ToPointer();

        // Determine the byte offset of the Red channel based on the bitmap's
        // actual color type.  BGRA = offset 2, RGBA = offset 0.
        int redOffset = bitmap.ColorType switch
        {
            SKColorType.Rgba8888 => 0,
            SKColorType.Bgra8888 => 2,
            _ => 2 // Default to BGRA (most common on desktop platforms)
        };

        int rowBytes = bitmap.RowBytes;
        int bytesPerPixel = bitmap.BytesPerPixel;

        for (var y = 0; y < bitmap.Height; y++)
        {
            byte* rowPtr = basePtr + (y * rowBytes);
            for (var x = 0; x < bitmap.Width; x++)
            {
                byte* pixelPtr = rowPtr + (x * bytesPerPixel);
                var redByte = *(pixelPtr + redOffset);

                pixelArray[x, y] = redByte < 127 ? (byte)1 : (byte)0;
                if (redByte < 127) _totalScanSize += 1;
            }
        }

        return pixelArray;
    }

    public static void Halt()
    {
        IsDrawing = false;
    }

    // ─── Helper: checks if two points are within MaxGapPixels ─────────
    private static bool PointsAreConnected(Vector2 a, Vector2 b)
    {
        return Math.Abs(a.X - b.X) <= MaxGapPixels &&
               Math.Abs(a.Y - b.Y) <= MaxGapPixels;
    }

    private static List<Dictionary<Vector2, int>> GetChunks(SKBitmap srcBitmap)
    {
        // Build a local data grid using the SAME criterion as Scan()
        // (Red channel < 127).  This ensures pixel-for-pixel agreement
        // between GetChunks and the dataArray used by DFS/A*.
        List<List<byte>> data = new List<List<byte>>();
        for (int x = 0; x < srcBitmap.Width; x++)
        {
            List<byte> column = new List<byte>();
            for (int y = 0; y < srcBitmap.Height; y++)
            {
                var color = srcBitmap.GetPixel(x, y);
                bool isOn = color.Red < 127;
                column.Add(isOn ? (byte)1 : (byte)0);
            }
            data.Add(column);
        }

        List<Dictionary<Vector2, int>> chunks = new();

        void Search(int startX, int startY)
        {
            var stack = new Stack<(int x, int y)>();
            stack.Push((startX, startY));
            data[startX][startY] = 2;

            var chunk = new Dictionary<Vector2, int>();

            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();

                // 8-directional neighbours
                int[][] dirs =
                {
                    new[] { -1,  0 }, new[] { 1, 0 },   // Left, Right
                    new[] {  0, -1 }, new[] { 0, 1 },   // Up, Down
                    new[] { -1, -1 }, new[] { 1, -1 },  // TL, TR
                    new[] { -1,  1 }, new[] { 1, 1 }    // BL, BR
                };

                foreach (var d in dirs)
                {
                    int nx = x + d[0];
                    int ny = y + d[1];

                    if (nx < 0 || nx >= srcBitmap.Width) continue;
                    if (ny < 0 || ny >= srcBitmap.Height) continue;

                    if (data[nx][ny] == 1)
                    {
                        data[nx][ny] = 2;
                        stack.Push((nx, ny));
                        chunk[new Vector2(nx, ny)] = 1;
                    }
                    else if (data[nx][ny] == 0)
                    {
                        chunk[new Vector2(x, y)] = 2;
                    }
                }
            }

            chunks.Add(chunk);
        }

        for (int y = 0; y < srcBitmap.Height; y++)
            for (int x = 0; x < srcBitmap.Width; x++)
            {
                if (data[x][y] == 1)
                    Search(x, y);
            }

        // Sort chunks by size (largest first)
        chunks.Sort((a, b) => b.Count.CompareTo(a.Count));

        return chunks;
    }

    private static List<List<Vector2>> GenerateActions(
        List<Dictionary<Vector2, int>> chunks, byte[,] data)
    {
        Vector2[] relativeDirections =
        {
            new(0, -1),    // Up
            new(1, 0),     // Right
            new(0, 1),     // Down
            new(-1, 0),    // Left
            new(-1, -1),   // Top-Left
            new(1, -1),    // Top-Right
            new(1, 1),     // Bottom-Right
            new(-1, 1)     // Bottom-Left
        };

        List<List<Vector2>> actions = new();

        foreach (Dictionary<Vector2, int> chunk in chunks)
        {
            foreach (KeyValuePair<Vector2, int> startPoint in chunk)
            {
                if (data[(int)startPoint.Key.X, (int)startPoint.Key.Y] != 1)
                    continue;

                var rawPath = ChosenFunction(
                    startPoint.Key, data, relativeDirections, chunk);

                // Safety net: split the raw path into contiguous segments.
                // Any jump larger than MaxGapPixels starts a new segment,
                // which will be drawn with its own click-down / click-up.
                actions.AddRange(SplitIntoContiguousSegments(rawPath));
            }
        }

        return actions;
    }

    /// <summary>
    /// Divides a path into segments where every consecutive pair of points
    /// is within MaxGapPixels of each other (Chebyshev distance).
    /// Any larger gap ends the current segment and starts a new one,
    /// preventing the mouse from dragging a line across empty space.
    /// </summary>
    private static List<List<Vector2>> SplitIntoContiguousSegments(
        List<Vector2> path)
    {
        var segments = new List<List<Vector2>>();
        if (path == null || path.Count == 0)
            return segments;

        var current = new List<Vector2> { path[0] };

        for (int i = 1; i < path.Count; i++)
        {
            var prev = path[i - 1];
            var pt = path[i];

            if (!PointsAreConnected(prev, pt))
            {
                // Finalize current segment and start a new one
                if (current.Count > 0)
                    segments.Add(current);
                current = new List<Vector2>();
            }

            current.Add(pt);
        }

        if (current.Count > 0)
            segments.Add(current);

        return segments;
    }

    private static List<Vector2> ChosenFunction(
        Vector2 start, byte[,] data,
        Vector2[] relativeDirections,
        Dictionary<Vector2, int> chunk)
    {
        if (ChosenAlgorithm == 1)
            return EdgeTraversal(start, data, relativeDirections);

        return DFS(start, data, relativeDirections);
    }

    private static List<Vector2> EdgeTraversal(
        Vector2 start, byte[,] data, Vector2[] directions)
    {
        List<Vector2> path = new();
        Vector2 currentPosition = start;
        int currentDirection = 1;

        while (true)
        {
            bool moved = false;

            foreach (int directionIndex in GetDirectionOrder(currentDirection))
            {
                Vector2 newPosition = currentPosition + directions[directionIndex];
                if (IsValidMove(newPosition, data))
                {
                    path.Add(newPosition);
                    currentPosition = newPosition;
                    currentDirection = directionIndex;
                    data[(int)newPosition.X, (int)newPosition.Y] = 2;
                    moved = true;
                    break;
                }
            }

            if (!moved)
                break;
        }

        return path;
    }

    private static List<Vector2> DFS(
        Vector2 start, byte[,] data, Vector2[] directions)
    {
        Stack<Vector2> stack = new();
        List<Vector2> path = new();

        Vector2? previousPosition = null;

        stack.Push(start);
        data[(int)start.X, (int)start.Y] = 2;

        while (stack.Count > 0)
        {
            Vector2 currentPosition = stack.Pop();

            // If the current position is not adjacent to the previous one,
            // try to bridge the gap with A*.  If A* fails (returns empty),
            // the path will contain a jump that SplitIntoContiguousSegments
            // will catch and split into separate drawing segments.
            if (previousPosition.HasValue &&
                !IsAdjacent(previousPosition.Value, currentPosition, directions))
            {
                List<Vector2> aStarPath =
                    AStar(previousPosition.Value, currentPosition, data);

                if (aStarPath.Count > 0)
                {
                    path.AddRange(aStarPath);
                    foreach (var position in aStarPath)
                        data[(int)position.X, (int)position.Y] = 2;
                }
                // If A* returned empty, we simply don't bridge the gap.
                // The jump in the path will be split by
                // SplitIntoContiguousSegments into a new segment.
            }

            path.Add(currentPosition);
            previousPosition = currentPosition;

            foreach (Vector2 direction in directions)
            {
                Vector2 neighbor = currentPosition + direction;
                if (IsValidMove(neighbor, data))
                {
                    data[(int)neighbor.X, (int)neighbor.Y] = 2;
                    stack.Push(neighbor);
                }
            }
        }

        return path;
    }

    private static bool IsAdjacent(
        Vector2 position1, Vector2 position2, Vector2[] directions)
    {
        foreach (Vector2 direction in directions)
        {
            if (position1 + direction == position2)
                return true;
        }

        return false;
    }

    private static List<Vector2> AStar(
        Vector2 start, Vector2 goal, byte[,] data)
    {
        PriorityQueue<Vector2, float> openSet = new();
        HashSet<Vector2> closedSet = new();
        Dictionary<Vector2, Vector2?> cameFrom = new();
        Dictionary<Vector2, float> gScore = new();
        Dictionary<Vector2, float> fScore = new();

        openSet.Enqueue(start, 0);
        gScore[start] = 0;
        fScore[start] = Heuristic(start, goal);

        while (openSet.Count > 0)
        {
            Vector2 current = openSet.Dequeue();

            if (current == goal)
                return ReconstructPath(cameFrom, current);

            closedSet.Add(current);

            for (int i = 0; i < 8; i++)
            {
                Vector2 neighbor = current + GetRelativeDirection(i);

                if (!IsWithinBounds(neighbor, data) ||
                    data[(int)neighbor.X, (int)neighbor.Y] == 0 ||
                    closedSet.Contains(neighbor))
                {
                    continue;
                }

                float tentativeGScore = gScore[current] + 1;

                if (!gScore.ContainsKey(neighbor) ||
                    tentativeGScore < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeGScore;
                    fScore[neighbor] =
                        gScore[neighbor] + Heuristic(neighbor, goal);

                    if (!openSet.UnorderedItems.Any(
                        item => item.Element == neighbor))
                    {
                        openSet.Enqueue(neighbor, fScore[neighbor]);
                    }
                }
            }
        }

        // No path found — return empty list.  The caller (DFS) will leave
        // a gap in the path, which SplitIntoContiguousSegments will handle.
        return new();
    }

    private static float Heuristic(Vector2 a, Vector2 b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }

    private static List<Vector2> ReconstructPath(
        Dictionary<Vector2, Vector2?> cameFrom, Vector2 current)
    {
        List<Vector2> path = new();
        while (cameFrom.ContainsKey(current) && cameFrom[current].HasValue)
        {
            path.Add(current);
            current = cameFrom[current].Value;
        }

        path.Reverse();
        return path;
    }

    private static IEnumerable<int> GetDirectionOrder(int currentDirection)
    {
        return new[]
        {
            (currentDirection + 3) % 4,  // Left
            currentDirection,            // Forward
            (currentDirection + 1) % 4,  // Right
            (currentDirection + 2) % 4,  // Backward
            4, 5, 6, 7                   // Diagonals
        };
    }

    private static Vector2 GetRelativeDirection(int directionIndex)
    {
        return directionIndex switch
        {
            0 => new Vector2(0, -1),   // Up
            1 => new Vector2(1, 0),    // Right
            2 => new Vector2(0, 1),    // Down
            3 => new Vector2(-1, 0),   // Left
            4 => new Vector2(-1, -1),  // Top-Left
            5 => new Vector2(1, -1),   // Top-Right
            6 => new Vector2(1, 1),    // Bottom-Right
            7 => new Vector2(-1, 1),   // Bottom-Left
            _ => throw new ArgumentOutOfRangeException(
                nameof(directionIndex), "Invalid direction index.")
        };
    }

    private static bool IsWithinBounds(Vector2 position, byte[,] data)
    {
        return position.X >= 0 && position.Y >= 0 &&
               position.X < data.GetLength(0) &&
               position.Y < data.GetLength(1);
    }

    private static bool IsValidMove(Vector2 position, byte[,] data)
    {
        return position.X >= 0 && position.Y >= 0 &&
               position.X < data.GetLength(0) &&
               position.Y < data.GetLength(1) &&
               data[(int)position.X, (int)position.Y] == 1;
    }

    private static bool StackHalted;

    public static async Task<bool> DrawStack(
        List<SKBitmap> stack, List<InputAction> actions, Vector2 position)
    {
        StackHalted = false;
        static void KeybindRelease(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == Config.Keybind_StopDrawing)
                StackHalted = true;
        }
        Input.taskHook.KeyReleased += KeybindRelease;

        foreach (SKBitmap bitmap in stack)
        {
            List<InputAction> actionsCopy = new(actions.Select(
                act => new InputAction(act.Action,
                    act.Data is not null ? act.Data : act.Position)));

            if (StackHalted) break;

            // Pre-Process Actions
            Color color = ImageProcessing.GetColor(bitmap);
            string hex = ColorToHexConverter.ToHexString(
                color, AlphaComponentPosition.Trailing);
            hex = hex.Substring(0, 6);

            foreach (var act in actionsCopy)
            {
                if (act.Action == InputAction.ActionType.WriteString &&
                    !string.IsNullOrEmpty(act.Data))
                {
                    act.Data = act.Data.Replace("{colorHex}", hex);
                }
            }

            foreach (var act in actionsCopy)
            {
                act.PerformAction();
                await NOP(1000000);
            }

            if (StackHalted) break;

            SKBitmap processedBitmap =
                ImageProcessing.Process(bitmap, ImageProcessing._currentFilters);
            await NOP(1000000);
            await Draw(processedBitmap, position);
        }

        return true;
    }

    // ─── FIX 2 & 3: Gap detection in the drawing loop ────────────────
    public static async Task<bool> Draw(SKBitmap bitmap, Vector2 position)
    {
        if (IsDrawing) return false;

        static void KeybindPress(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == Config.Keybind_SkipRescan)
            {
                if (NoRescan) return;
                SkipRescan = true;
            }
        }

        static void KeybindRelease(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == Config.Keybind_StopDrawing) Halt();
            if (e.Data.KeyCode == Config.Keybind_SkipRescan)
            {
                if (NoRescan) return;
                SkipRescan = false;
            }

            if (e.Data.KeyCode == Config.Keybind_PauseDrawing)
                IsPaused = !IsPaused;
        }

        Input.taskHook.KeyPressed += KeybindPress;
        Input.taskHook.KeyReleased += KeybindRelease;

        IsDrawing = true;
        var usedPos = position;

        Dispatcher.UIThread.Invoke(() =>
        {
            _dataDisplay = new DrawDataDisplay();
            _dataDisplay.Show();
            _dataDisplay.Position =
                new PixelPoint(
                    (int)(usedPos.X + bitmap.Width),
                    (int)(usedPos.Y + bitmap.Height));
        });

        LastPos = usedPos;
        Pos startPos = new() { X = (int)usedPos.X, Y = (int)usedPos.Y };

        // Click at the initial position to "activate" the canvas
        Input.MoveTo((short)startPos.X, (short)startPos.Y);
        await NOP(50000);
        Input.SendClick(Input.MouseTypes.MouseLeft);
        await NOP(50000);

        byte[,] dataArray = Scan(bitmap);

        Dispatcher.UIThread.Invoke(() =>
        {
            _dataDisplay.DataDisplayText.Text = $"Getting Chunks...";
        });

        List<Dictionary<Vector2, int>> chunks = GetChunks(bitmap);

        Dispatcher.UIThread.Invoke(() =>
        {
            _dataDisplay.DataDisplayText.Text = $"Generating Action Path...";
        });

        List<List<Vector2>> actions = GenerateActions(chunks, dataArray);

        int actionsComplete = 0;

        foreach (List<Vector2> action in actions)
        {
            actionsComplete++;
            bool isDown = false;
            int actionComplete = 0;

            // ── Safety: always release click before starting a new segment ──
            Input.SendClickUp(Input.MouseTypes.MouseLeft);
            await NOP(ClickDelay * 1000);

            // Single pixel = clean click without jitter
            if (action.Count == 1)
            {
                var p = action[0];
                short x = (short)(p.X + startPos.X);
                short y = (short)(p.Y + startPos.Y);

                Dispatcher.UIThread.Invoke(() =>
                {
                    _dataDisplay.DataDisplayText.Text =
                        $"ActionSet Completed: 1/1\n" +
                        $"ActionSet's Remaining: {actionsComplete}/{actions.Count}";
                });

                Input.MoveTo(x, y);
                await NOP(ClickDelay * 500);
                Input.SendClick(Input.MouseTypes.MouseLeft);
                await NOP(ClickDelay * 2500);

                if (!IsDrawing) break;
                continue;
            }

            // Track the last drawn point WITHIN this action to detect gaps
            Vector2? lastDrawnPoint = null;

            foreach (Vector2 p in action)
            {
                actionComplete++;
                if (!IsDrawing) break;

                short x = (short)(p.X + startPos.X);
                short y = (short)(p.Y + startPos.Y);

                Dispatcher.UIThread.Invoke(() =>
                {
                    _dataDisplay.DataDisplayText.Text =
                        $"ActionSet Completed: {actionComplete}/{action.Count}\n" +
                        $"ActionSet's Remaining: {actionsComplete}/{actions.Count}";
                });

                // ── GAP DETECTION (safety net) ──────────────────────────
                // Even though SplitIntoContiguousSegments should have
                // removed all jumps, this is a second line of defence.
                // If two consecutive points are farther apart than
                // MaxGapPixels, we release the click, move to the new
                // position, and click down again — preventing any
                // erroneous drag line across empty space.
                if (isDown && lastDrawnPoint.HasValue &&
                    !PointsAreConnected(lastDrawnPoint.Value, p))
                {
                    Input.SendClickUp(Input.MouseTypes.MouseLeft);
                    await NOP(ClickDelay * 2500);

                    // Move to the new position WITHOUT drawing
                    Input.MoveTo(x, y);
                    await NOP(ClickDelay * 2500);

                    // Re-press click to resume drawing
                    Input.SendClickDown(Input.MouseTypes.MouseLeft);
                    await NOP(Interval);

                    lastDrawnPoint = p;

                    if (IsPaused)
                    {
                        Input.SendClickUp(Input.MouseTypes.MouseLeft);
                        while (IsPaused) await NOP(500000);
                        Input.MoveTo(x, y);
                        await NOP(500000);
                        Input.SendClickDown(Input.MouseTypes.MouseLeft);
                    }

                    continue;
                }

                if (!isDown)
                {
                    isDown = true;

                    // Interpolated move to the first point of the segment
                    Vector2 currentPosition = Input.mousePos;
                    Vector2 targetPosition = new Vector2(x, y);
                    int steps = 100;
                    float stepDelay = ClickDelay * 2500f / steps;

                    for (int i = 1; i <= steps; i++)
                    {
                        var interpP = Vector2.Lerp(
                            currentPosition, targetPosition, i / (float)steps);
                        short interpX = (short)interpP.X;
                        short interpY = (short)interpP.Y;

                        Input.MoveTo(interpX, interpY);
                        await NOP((long)stepDelay);
                    }

                    // Jitter to ensure the target application registers the position
                    for (int i = 0; i < 10; i++)
                    {
                        Input.MoveTo((short)(x - 1), y);
                        await NOP(ClickDelay * 500);
                        Input.MoveTo(x, y);
                    }

                    Input.SendClickDown(Input.MouseTypes.MouseLeft);
                }

                if (IsPaused)
                {
                    Input.SendClickUp(Input.MouseTypes.MouseLeft);
                    while (IsPaused) await NOP(500000);
                    Input.MoveTo(x, y);
                    await NOP(500000);
                    Input.SendClickDown(Input.MouseTypes.MouseLeft);
                }

                Input.MoveTo(x, y);
                await NOP(Interval);

                lastDrawnPoint = p;
            }

            // Release click at the end of the segment
            Input.SendClickUp(Input.MouseTypes.MouseLeft);
            await NOP(ClickDelay * 2500);

            if (!IsDrawing) break;
        }

        Input.taskHook.KeyPressed -= KeybindPress;
        Input.taskHook.KeyReleased -= KeybindRelease;

        IsDrawing = false;

        Dispatcher.UIThread.Invoke(() =>
        {
            _dataDisplay.Close();
            if (ShowPopup)
                new MessageBox().ShowMessageBox(
                    "Drawing Finished!",
                    "The drawing has finished! Yippee!");
        });

        return true;
    }

    // ─── FIX 4: Gap detection in DrawColorLayers ─────────────────────
    public static async Task<bool> DrawColorLayers(
        List<ColorLayerData> layers,
        List<InputAction> actions,
        Vector2 position)
    {
        StackHalted = false;
        var continueTcs = new TaskCompletionSource<bool>();

        void KeybindHandler(object? sender, KeyboardHookEventArgs e)
        {
            if (e.Data.KeyCode == Config.Keybind_StopDrawing)
                StackHalted = true;

            if (e.Data.KeyCode == Config.Keybind_StartDrawing &&
                !continueTcs.Task.IsCompleted)
            {
                continueTcs.TrySetResult(true);
            }
        }
        Input.taskHook.KeyReleased += KeybindHandler;

        for (var layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            if (StackHalted) break;

            if (layerIndex > 0 && !AutoAdvance)
            {
                continueTcs = new TaskCompletionSource<bool>();
                await continueTcs.Task;
                if (StackHalted) break;
            }

            var layer = layers[layerIndex];

            var actionsCopy = new List<InputAction>(actions.Select(
                act => new InputAction(act.Action,
                    act.Data is not null ? act.Data : act.Position)));

            var hex = layer.HexColor.Replace("#", "");
            foreach (var act in actionsCopy)
            {
                if (act.Action == InputAction.ActionType.WriteString &&
                    !string.IsNullOrEmpty(act.Data))
                {
                    act.Data = act.Data.Replace("{colorHex}", hex);
                }
            }

            foreach (var act in actionsCopy)
            {
                act.PerformAction();
                await NOP(500000);
            }

            var optimizedPixels = PathOptimizer.OptimizePath(layer.Pixels);
            if (optimizedPixels.Count == 0) continue;

            var firstPos = new Vector2(
                position.X + optimizedPixels[0].X,
                position.Y + optimizedPixels[0].Y);

            Input.MoveTo((short)firstPos.X, (short)firstPos.Y);
            await NOP(50000);

            Input.SendClickDown(Input.MouseTypes.MouseLeft);

            Vector2? lastDrawn = null;

            for (var i = 0; i < optimizedPixels.Count; i++)
            {
                if (StackHalted) break;

                var px = (short)(position.X + optimizedPixels[i].X);
                var py = (short)(position.Y + optimizedPixels[i].Y);

                // ── GAP DETECTION for color layers ─────────────────────
                // If the gap between the current pixel and the last drawn
                // pixel exceeds MaxGapPixels, release click, move, re-click.
                if (lastDrawn.HasValue &&
                    !PointsAreConnected(
                        new Vector2(lastDrawn.Value.X, lastDrawn.Value.Y),
                        new Vector2(optimizedPixels[i].X, optimizedPixels[i].Y)))
                {
                    Input.SendClickUp(Input.MouseTypes.MouseLeft);
                    await NOP(ClickDelay * 2500);

                    Input.MoveTo(px, py);
                    await NOP(ClickDelay * 2500);

                    Input.SendClickDown(Input.MouseTypes.MouseLeft);
                    await NOP(Interval);

                    lastDrawn = optimizedPixels[i];
                    continue;
                }

                Input.MoveTo(px, py);
                await NOP(Interval);

                lastDrawn = optimizedPixels[i];
            }

            Input.SendClickUp(Input.MouseTypes.MouseLeft);
            await NOP(ClickDelay * 2500);
        }

        Input.taskHook.KeyReleased -= KeybindHandler;
        return true;
    }

    private class Pos
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}