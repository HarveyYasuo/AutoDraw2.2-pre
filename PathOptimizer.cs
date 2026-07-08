using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Avalonia.Media;

namespace Autodraw;

public static class PathOptimizer
{
    public static List<ColorLayerData> SortByLuminance(List<ColorLayerData> layers, bool darkestFirst = false)
    {
        return darkestFirst
            ? layers.OrderByDescending(l => GetLuminance(l.LayerColor)).ToList()
            : layers.OrderBy(l => GetLuminance(l.LayerColor)).ToList();
    }

    private static double GetLuminance(Color color)
    {
        return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
    }

    public static List<Vector2> OptimizePath(List<Vector2> pixels)
    {
        if (pixels.Count <= 2)
            return new List<Vector2>(pixels);

        var unvisited = new List<Vector2>(pixels);
        var result = new List<Vector2>(pixels.Count);

        var current = unvisited[0];
        unvisited.RemoveAt(0);
        result.Add(current);

        while (unvisited.Count > 0)
        {
            var minDist = float.MaxValue;
            var bestIdx = 0;

            for (var i = 0; i < unvisited.Count; i++)
            {
                var dist = Vector2.DistanceSquared(current, unvisited[i]);
                if (!(dist < minDist)) continue;
                minDist = dist;
                bestIdx = i;
                if (dist <= 1) break;
            }

            current = unvisited[bestIdx];
            unvisited.RemoveAt(bestIdx);
            result.Add(current);
        }

        return result;
    }

    public static List<List<Vector2>> GroupIntoClusters(List<Vector2> pixels, float maxDistSq = 25f)
    {
        if (pixels.Count == 0)
            return new List<List<Vector2>>();

        var visited = new bool[pixels.Count];
        var clusters = new List<List<Vector2>>();

        for (var i = 0; i < pixels.Count; i++)
        {
            if (visited[i]) continue;

            var cluster = new List<Vector2>();
            var queue = new Queue<int>();
            queue.Enqueue(i);
            visited[i] = true;

            while (queue.Count > 0)
            {
                var idx = queue.Dequeue();
                cluster.Add(pixels[idx]);

                for (var j = 0; j < pixels.Count; j++)
                {
                    if (visited[j]) continue;
                    if (Vector2.DistanceSquared(pixels[idx], pixels[j]) <= maxDistSq)
                    {
                        visited[j] = true;
                        queue.Enqueue(j);
                    }
                }
            }

            clusters.Add(OptimizePath(cluster));
        }

        return clusters;
    }
}
