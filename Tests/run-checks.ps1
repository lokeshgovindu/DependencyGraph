# Builds and runs the checks in this folder against bin\Debug\DependencyGraph.dll.
#
#   powershell -ExecutionPolicy Bypass -File Tests\run-checks.ps1
#
# GraphChecks is a console app rather than a unit-test project on purpose: the code it exercises
# needs a real MSAGL GraphViewer in a WPF visual tree, and it drives the shipped assembly by
# reflection so the checks cannot drift from a copy of the logic.
param(
    [string]$Configuration = "Debug",
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$bin = Join-Path $Root "bin\$Configuration"
$dll = Join-Path $bin "DependencyGraph.dll"

if (-not (Test-Path $dll)) { throw "Build the extension first - $dll not found." }

$work = Join-Path $env:TEMP "DependencyGraphChecks"
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item "$bin\*.dll" $work -Force

$ref = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
if (-not (Test-Path $ref)) { throw "The .NET Framework 4.8 reference assemblies are needed." }

$csc = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe" |
       Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found under any Visual Studio install." }

$exe = Join-Path $work "GraphChecks.exe"
& $csc.FullName /nologo /target:exe /platform:x64 /out:$exe /nostdlib+ `
    /r:"$ref\mscorlib.dll" /r:"$ref\System.dll" /r:"$ref\System.Core.dll" /r:"$ref\System.Xml.dll" `
    /r:"$ref\Facades\netstandard.dll" `
    /r:"$ref\PresentationCore.dll" /r:"$ref\PresentationFramework.dll" /r:"$ref\WindowsBase.dll" `
    /r:"$ref\System.Xaml.dll" `
    /r:"$work\AutomaticGraphLayout.dll" /r:"$work\AutomaticGraphLayout.Drawing.dll" `
    /r:"$work\Microsoft.Msagl.WpfGraphControl.dll" `
    (Join-Path $PSScriptRoot "GraphChecks.cs")
if ($LASTEXITCODE -ne 0) { throw "GraphChecks failed to compile." }

Push-Location $work
try { & $exe $work (Join-Path $work "DependencyGraph.dll"); $graphExit = $LASTEXITCODE }
finally { Pop-Location }

""
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "ContrastCheck.ps1") -Dll $dll
$contrastExit = $LASTEXITCODE

""
if ($graphExit -eq 0 -and $contrastExit -eq 0) { "ALL CHECKS PASSED"; exit 0 }
"FAILED - graph checks: $graphExit, contrast checks: $contrastExit"
exit 1
