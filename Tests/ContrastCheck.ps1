# Reads the real GraphPalette values out of the built DependencyGraph.dll and reports WCAG
# contrast ratios for every text-on-background pairing the UI actually uses, for both themes.
param([string]$Dll = "D:\Build\github\lokeshgovindu\DependencyGraph\bin\Debug\DependencyGraph.dll")

$ErrorActionPreference = "Stop"
$asm = [System.Reflection.Assembly]::LoadFrom($Dll)
$pt = $asm.GetType("DependencyGraph.UI.GraphPalette", $true)

function Get-Palette([string]$which) {
    $m = $pt.GetMethod($which, [System.Reflection.BindingFlags]::NonPublic -bor [System.Reflection.BindingFlags]::Static)
    return $m.Invoke($null, $null)
}

function Get-Rgb($palette, [string]$prop) {
    $c = $pt.GetProperty($prop).GetValue($palette)
    $ct = $c.GetType()
    return @{
        R = [int]$ct.GetProperty("R").GetValue($c)
        G = [int]$ct.GetProperty("G").GetValue($c)
        B = [int]$ct.GetProperty("B").GetValue($c)
    }
}

function Get-Luminance($rgb) {
    $vals = @()
    foreach ($k in @("R","G","B")) {
        $s = $rgb[$k] / 255.0
        if ($s -le 0.03928) { $vals += ($s / 12.92) } else { $vals += [Math]::Pow((($s + 0.055) / 1.055), 2.4) }
    }
    return 0.2126 * $vals[0] + 0.7152 * $vals[1] + 0.0722 * $vals[2]
}

function Get-Contrast($a, $b) {
    $la = Get-Luminance $a
    $lb = Get-Luminance $b
    $hi = [Math]::Max($la, $lb); $lo = [Math]::Min($la, $lb)
    return ($hi + 0.05) / ($lo + 0.05)
}

# text colour, background colour, what it is, minimum acceptable ratio
$pairs = @(
    @("NodeText",  "Background",      "command bar / status text on bar",  4.5),
    @("StatLabel", "Background",       "status field name",                 4.5),
    @("StatValue", "Background",       "status field value",                4.5),
    @("NodeText",  "NodeFill",         "node label",                        4.5),
    @("RootText",  "RootFill",         "root node label",                   4.5),
    @("NodeText",  "ReferencingFill",  "label on 'referencing' highlight",  4.5),
    @("NodeText",  "ReferencedFill",   "label on 'referenced' highlight",   4.5),
    @("NodeStroke","Background",       "borders / separators (non-text)",    3.0),
    @("Edge",      "Background",       "edges (non-text)",                   3.0)
)

$failures = 0
foreach ($theme in @("Light","Dark")) {
    $p = Get-Palette $theme
    ""
    "=== $theme palette ==="
    foreach ($pair in $pairs) {
        $fg = Get-Rgb $p $pair[0]
        $bg = Get-Rgb $p $pair[1]
        $ratio = Get-Contrast $fg $bg
        $min = [double]$pair[3]
        $ok = $ratio -ge $min
        if (-not $ok) { $failures++ }
        "{0}  {1,-34} {2,5:0.00}:1  (min {3:0.0})  {4} on {5}" -f `
            $(if ($ok) { "PASS" } else { "FAIL" }), $pair[2], $ratio, $min, $pair[0], $pair[1]
    }
}
""
if ($failures -eq 0) { "ALL CONTRAST CHECKS PASSED" } else { "$failures CONTRAST CHECK(S) FAILED" }
exit $(if ($failures -eq 0) { 0 } else { 1 })
