using System;
using System.Collections.Generic;
using System.Numerics;

namespace AnimatedWin2dControls.Impressionist;

internal static class OctTreePaletteGenerator
{
    public static ThemeColorResult CreateThemeColor(
        Dictionary<Vector3, int> sourceColor,
        bool ignoreWhite = false)
    {
        var quantizer = new PaletteQuantizer();

        foreach (var (color, pop) in sourceColor)
        {
            if (ignoreWhite && sourceColor.Count > 1 &&
                color.X > 250f && color.Y > 250f && color.Z > 250f)
                continue;
            quantizer.AddColorRange(color, pop);
        }

        quantizer.Quantize(1);
        var result = quantizer.GetThemeResult();
        bool isDark = result.RGBVectorLStarIsDark();
        return new ThemeColorResult(result, isDark);
    }

    public static PaletteResult CreatePalette(
        Dictionary<Vector3, int> sourceColor,
        int clusterCount,
        bool ignoreWhite = false)
    {
        if (sourceColor.Count == 1) ignoreWhite = false;

        var themeResult = CreateThemeColor(sourceColor, ignoreWhite);
        bool colorIsDark = themeResult.ColorIsDark;

        var quantizer = new PaletteQuantizer();
        foreach (var (color, pop) in sourceColor)
        {
            if (ignoreWhite && color.X > 250f && color.Y > 250f && color.Z > 250f)
                continue;
            if (colorIsDark && !color.PaletteRGBVectorLStarIsDark())
                continue;
            if (!colorIsDark && !color.PaletteRGBVectorLStarIsLight())
                continue;
            quantizer.AddColorRange(color, pop);
        }

        quantizer.Quantize(clusterCount);
        var quantizeResult = quantizer.GetPaletteResult(clusterCount);

        var palette = new List<Vector3>(clusterCount);
        int count = quantizeResult.Count;
        if (count > 0)
        {
            for (int i = 0; i < clusterCount; i++)
                palette.Add(quantizeResult[i % count]);
        }
        else
        {
            for (int i = 0; i < clusterCount; i++)
                palette.Add(Vector3.Zero);
        }

        return new PaletteResult(palette, colorIsDark, themeResult);
    }

    private sealed class PaletteQuantizer
    {
        private readonly Node _root;
        private readonly List<Node>[] _levelNodes;

        public PaletteQuantizer()
        {
            _root = new Node(this);
            _levelNodes = new List<Node>[8];
            for (int i = 0; i < 8; i++)
                _levelNodes[i] = new List<Node>();
        }

        public void AddColorRange(Vector3 color, int count)
            => _root.AddColorRange(color, 0, count);

        public void AddLevelNode(Node node, int level)
            => _levelNodes[level].Add(node);

        public List<Vector3> GetPaletteResult(int count)
        {
            var dict = _root.GetPaletteResult();

            var entries = new List<KeyValuePair<Vector3, int>>(dict.Count);
            foreach (var kv in dict) entries.Add(kv);
            entries.Sort(static (a, b) => b.Value.CompareTo(a.Value));

            int take = Math.Min(count, entries.Count);
            var result = new List<Vector3>(take);
            for (int i = 0; i < take; i++)
                result.Add(entries[i].Key);
            return result;
        }

        public Vector3 GetThemeResult() => _root.GetThemeResult();

        public void Quantize(int colorCount)
        {
            int nodesToRemove = _levelNodes[7].Count - colorCount;
            int level = 6;
            bool toBreak = false;

            while (level >= 0 && nodesToRemove > 0)
            {
                var nodes = _levelNodes[level];

                var candidates = new List<Node>(nodes.Count);
                foreach (var n in nodes)
                {
                    if (n.ChildrenCount - 1 <= nodesToRemove)
                        candidates.Add(n);
                }
                candidates.Sort(static (a, b) => a.ChildrenCount.CompareTo(b.ChildrenCount));

                foreach (var leaf in candidates)
                {
                    if (leaf.ChildrenCount > nodesToRemove)
                    {
                        toBreak = true;
                        continue;
                    }
                    nodesToRemove -= leaf.ChildrenCount - 1;
                    leaf.Merge();
                    if (nodesToRemove <= 0) break;
                }

                if (level + 1 < _levelNodes.Length)
                    _levelNodes[level + 1].Clear();
                level--;
                if (toBreak) break;
            }
        }
    }

    private sealed class Node
    {
        private readonly PaletteQuantizer _parent;
        private Node?[] _children = new Node?[8];
        private Vector3 _color;
        private int _count;

        public int ChildrenCount
        {
            get
            {
                int c = 0;
                for (int i = 0; i < 8; i++)
                    if (_children[i] is not null) c++;
                return c;
            }
        }

        public Node(PaletteQuantizer parent) => _parent = parent;

        public void AddColorRange(Vector3 color, int level, int count)
        {
            if (level < 8)
            {
                int index = GetIndex(color, level);
                if (_children[index] is null)
                {
                    var newNode = new Node(_parent);
                    _children[index] = newNode;
                    _parent.AddLevelNode(newNode, level);
                }
                _children[index]!.AddColorRange(color, level + 1, count);
            }
            else
            {
                _color = color;
                _count += count;
            }
        }

        public Vector3 GetThemeResult()
        {
            var palette = GetPaletteResult();
            var sum = Vector3.Zero;
            int total = 0;
            foreach (var (color, pop) in palette)
            {
                sum += color * pop;
                total += pop;
            }
            return total > 0 ? sum / total : Vector3.Zero;
        }

        public Dictionary<Vector3, int> GetPaletteResult()
        {
            var result = new Dictionary<Vector3, int>();
            CollectLeaves(result);
            return result;
        }

        private void CollectLeaves(Dictionary<Vector3, int> result)
        {
            bool hasChildren = false;
            for (int i = 0; i < 8; i++)
            {
                if (_children[i] is not null)
                {
                    hasChildren = true;
                    _children[i]!.CollectLeaves(result);
                }
            }

            if (!hasChildren)
            {
                if (result.TryGetValue(_color, out int existing))
                    result[_color] = existing + _count;
                else
                    result[_color] = _count;
            }
        }

        private static int GetIndex(Vector3 color, int level)
        {
            int mask = 0b10000000 >> level;
            int ret = 0;
            if (((byte)color.X & mask) != 0) ret |= 0b100;
            if (((byte)color.Y & mask) != 0) ret |= 0b010;
            if (((byte)color.Z & mask) != 0) ret |= 0b001;
            return ret;
        }

        public void Merge()
        {
            float sumX = 0f, sumY = 0f, sumZ = 0f;
            int total = 0;
            for (int i = 0; i < 8; i++)
            {
                if (_children[i] is not null)
                {
                    sumX += _children[i]!._color.X * _children[i]!._count;
                    sumY += _children[i]!._color.Y * _children[i]!._count;
                    sumZ += _children[i]!._color.Z * _children[i]!._count;
                    total += _children[i]!._count;
                }
            }
            if (total > 0)
                _color = new Vector3(sumX / total, sumY / total, sumZ / total);
            _count = total;
            _children = new Node?[8];
        }
    }
}
