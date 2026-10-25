param(
    [Parameter(Mandatory)][string]$Path,
    [ValidateRange(8000, 768000)][int]$SampleRate = 48000,
    [ValidateRange(1, 3600)][int]$Seconds = 180
)

# One reusable second of stereo float PCM; no third-party audio tools required.
$samples = [float[]]::new($SampleRate * 2)
for ($frame = 0; $frame -lt $SampleRate; $frame++) {
    $sample = [float](0.1 * [Math]::Sin(2 * [Math]::PI * 1000 * $frame / $SampleRate))
    $samples[$frame * 2] = $sample
    $samples[$frame * 2 + 1] = $sample
}
$block = [byte[]]::new($samples.Length * 4)
[Buffer]::BlockCopy($samples, 0, $block, 0, $block.Length)
$bytes = [long]$block.Length * $Seconds
if ($bytes -gt [int]::MaxValue - 36) { throw 'Fixture exceeds the supported RIFF size; reduce sample rate or duration.' }
$bytes = [int]$bytes
$writer = [IO.BinaryWriter]::new([IO.File]::Create($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)))
try {
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
    $writer.Write([int](36 + $bytes))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $writer.Write([int]16)
    $writer.Write([int16]3)
    $writer.Write([int16]2)
    $writer.Write($SampleRate)
    $writer.Write([int]($SampleRate * 8))
    $writer.Write([int16]8)
    $writer.Write([int16]32)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write($bytes)
    for ($second = 0; $second -lt $Seconds; $second++) { $writer.Write($block) }
}
finally { $writer.Dispose() }
