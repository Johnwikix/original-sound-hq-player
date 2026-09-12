$path = 'External\AnimatedWin2dControls\AnimatedWin2dControls\Renderer\Background\RotatingMeshBackgroundRenderer.cs'
$s = Get-Content $path -Raw
$s = $s.Replace('using ComputeSharp.D2D1;', "using ComputeSharp.D2D1;`r`nusing ComputeSharp.D2D1.Interop;")
$start = $s.IndexOf('    /// <summary>')
$end = $s.IndexOf('    public sealed class', $start)
$s = $s.Substring(0,$start) + @"
    /// <summary>
    /// Rotating artwork and pinch mesh ported from Lyricify-Backgrounds (Apache 2.0).
    /// Rotation and blur run at 1/8 resolution. A spatially indexed triangle pass
    /// reproduces PinchVertex coverage at output resolution; the composite point-samples
    /// its UV field to preserve overdraw boundaries and applies theme treatment/dither.
    /// </summary>
"@ + "`r`n" + $s.Substring($end)
$start = $s.IndexOf('        /// <summary>')
$end = $s.IndexOf('        private const float BackdropPixelScale', $start)
$s = $s.Substring(0,$start) + "        /// <summary>Rotation/blur density, corresponding to the original backdrop downsample.</summary>`r`n" + $s.Substring($end)
$start = $s.IndexOf('        /// <summary>', $s.IndexOf('private const float LandscapeTextureScale'))
$end = $s.IndexOf('        /// <summary>', $start + 20)
$s = $s.Remove($start,$end-$start)
$s = $s.Replace('private const float MeshWarpTimeScale = 3.5f;', 'private const float MeshWarpTimeScale = 5f;')
$s = $s.Replace('private const float RotationScale = 1.6f;', 'private const float RotationScale = 1f;')
$s = $s.Replace('private CanvasBitmap? _meshBitmap;', 'private D2D1ResourceTextureManager? _meshVertices;' + "`r`n        private D2D1ResourceTextureManager? _meshCells;")
$s = $s.Replace('_meshBitmap?.Dispose();', '_meshVertices = null;')
$s = $s.Replace('_meshBitmap = null;', '_meshCells = null;')
$s = $s.Replace('EnsureMeshBitmap(control, backdropHeight > backdropWidth);','EnsureMeshResources(backdropHeight > backdropWidth);')
$s = $s.Replace('_meshBitmap is not null','_meshVertices is not null && _meshCells is not null')
$s = $s.Replace('_meshColumns);', '_meshColumns, pinchTextureScale, pinchTextureOffset);')
$s = $s.Replace('ditherStrength: 1f,' + "`r`n                            pinchTextureScale,`r`n                            pinchTextureOffset);", 'ditherStrength: 1f);')
$s = $s.Replace('Describe(sb, " mesh", _meshBitmap);','sb.Append($" meshResources={_meshVertices is not null && _meshCells is not null}");')
$s = $s.Replace('* SolvePixelScale','')
$start = $s.IndexOf('        private void EnsureMeshBitmap')
$end = $s.IndexOf('        private void EnsureCoverBitmaps', $start)
$replacement = @"
        private void EnsureMeshResources(bool isPortrait)
        {
            if (_meshVertices is not null && _meshCells is not null && _meshIsPortrait == isPortrait)
                return;

            RotatingMeshWarp.MeshData mesh = RotatingMeshWarp.Create(
                isPortrait ? RotatingMeshWarp.ResolvePortraitPreset(_presetSlot) : _presetSlot,
                isPortrait);
            var packed = new float[mesh.Rows * mesh.Columns * 4];
            for (int i = 0; i < mesh.From.Length; i++)
            {
                packed[i * 4] = mesh.From[i].X;
                packed[i * 4 + 1] = mesh.From[i].Y;
                packed[i * 4 + 2] = mesh.To[i].X;
                packed[i * 4 + 3] = mesh.To[i].Y;
            }
            var index = RotatingMeshSpatialIndex.Create(mesh);
            _meshVertices = null;
            _meshCells = null;
            try
            {
                // Data textures have no image-input/DPI/alpha processing. The solve
                // effect has zero image inputs, so resource registers start at zero.
                var vertices = CreateMeshResource(packed, mesh.Columns, mesh.Rows);
                var cells = CreateMeshResource(index.Texels, index.Width,
                    RotatingMeshSpatialIndex.TileCount * RotatingMeshSpatialIndex.TileCount);
                _solveEffect!.ResourceTextureManagers[0] = vertices;
                _solveEffect.ResourceTextureManagers[1] = cells;
                _meshVertices = vertices;
                _meshCells = cells;
                _meshRows = mesh.Rows;
                _meshColumns = mesh.Columns;
                _meshIsPortrait = isPortrait;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Rotating mesh resource creation failed; using blurred artwork");
            }
        }

        private static D2D1ResourceTextureManager CreateMeshResource(float[] data, int width, int height)
        {
            return new D2D1ResourceTextureManager(
                new uint[] { (uint)width, (uint)height },
                D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four,
                D2D1Filter.MinMagMipPoint,
                new[] { D2D1ExtendMode.Clamp, D2D1ExtendMode.Clamp },
                MemoryMarshal.AsBytes(data.AsSpan()), new uint[] { (uint)width * 16 });
        }

"@
$s = $s.Substring(0,$start) + $replacement + $s.Substring($end)
Set-Content $path $s -Encoding utf8
