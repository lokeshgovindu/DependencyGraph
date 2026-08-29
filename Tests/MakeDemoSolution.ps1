# Generates a small multi-project C# solution with a realistic reference graph, so the
# DependencyGraph tool window has something interesting to draw.
param([string]$Root = "D:\Build\_DependencyGraphDemo")

$ErrorActionPreference = "Stop"

# name -> projects it references
$graph = [ordered]@{
    "Contoso.Core"        = @()
    "Contoso.Data"        = @("Contoso.Core")
    "Contoso.Services"    = @("Contoso.Core", "Contoso.Data")
    "Contoso.Widgets"     = @("Contoso.Core")
    "Contoso.Ui"          = @("Contoso.Core", "Contoso.Widgets")
    "Contoso.Reporting"   = @("Contoso.Data", "Contoso.Services")
    "Contoso.App"         = @("Contoso.Core", "Contoso.Services", "Contoso.Ui", "Contoso.Reporting")
}

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
New-Item -ItemType Directory -Force $Root | Out-Null

# Stable GUIDs so the solution and project files agree.
$guids = @{}
foreach ($name in $graph.Keys) { $guids[$name] = [guid]::NewGuid().ToString("B").ToUpper() }

foreach ($name in $graph.Keys) {
    $dir = Join-Path $Root $name
    New-Item -ItemType Directory -Force $dir | Out-Null

    $refs = ""
    foreach ($r in $graph[$name]) {
        $refs += @"
    <ProjectReference Include="..\$r\$r.csproj">
      <Project>$($guids[$r])</Project>
      <Name>$r</Name>
    </ProjectReference>

"@
    }

    $csproj = @"
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="`$(MSBuildExtensionsPath)\`$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('`$(MSBuildExtensionsPath)\`$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <PropertyGroup>
    <Configuration Condition=" '`$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '`$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>$($guids[$name])</ProjectGuid>
    <OutputType>Library</OutputType>
    <AppDesignerFolder>Properties</AppDesignerFolder>
    <RootNamespace>$($name -replace '\.','')</RootNamespace>
    <AssemblyName>$name</AssemblyName>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
    <FileAlignment>512</FileAlignment>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
  <PropertyGroup Condition=" '`$(Configuration)|`$(Platform)' == 'Debug|AnyCPU' ">
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>bin\Debug\</OutputPath>
    <DefineConstants>DEBUG;TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Core" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Class1.cs" />
  </ItemGroup>
  <ItemGroup>
$refs  </ItemGroup>
  <Import Project="`$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
"@
    Set-Content -Path (Join-Path $dir "$name.csproj") -Value $csproj -Encoding UTF8

    $ns = $name -replace '\.',''
    Set-Content -Path (Join-Path $dir "Class1.cs") -Encoding UTF8 -Value @"
namespace $ns
{
    public class Class1
    {
        public string Name => "$name";
    }
}
"@
}

# Solution file
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00")
[void]$sb.AppendLine("# Visual Studio Version 17")
[void]$sb.AppendLine("VisualStudioVersion = 17.6.33829.357")
[void]$sb.AppendLine("MinimumVisualStudioVersion = 10.0.40219.1")

$csGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}"
foreach ($name in $graph.Keys) {
    [void]$sb.AppendLine("Project(`"$csGuid`") = `"$name`", `"$name\$name.csproj`", `"$($guids[$name])`"")
    [void]$sb.AppendLine("EndProject")
}
# Solution folders, so the graph has something to group by.
$folderGuid = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}"
$folders = [ordered]@{
    "Components"   = @("Contoso.Core", "Contoso.Data", "Contoso.Services")
    "Presentation" = @("Contoso.Ui", "Contoso.Widgets")
}
$folderIds = @{}
foreach ($fname in $folders.Keys) {
    $folderIds[$fname] = [guid]::NewGuid().ToString("B").ToUpper()
    [void]$sb.AppendLine("Project(`"$folderGuid`") = `"$fname`", `"$fname`", `"$($folderIds[$fname])`"")
    [void]$sb.AppendLine("EndProject")
}

[void]$sb.AppendLine("Global")
[void]$sb.AppendLine("`tGlobalSection(SolutionConfigurationPlatforms) = preSolution")
[void]$sb.AppendLine("`t`tDebug|Any CPU = Debug|Any CPU")
[void]$sb.AppendLine("`t`tRelease|Any CPU = Release|Any CPU")
[void]$sb.AppendLine("`tEndGlobalSection")
[void]$sb.AppendLine("`tGlobalSection(ProjectConfigurationPlatforms) = postSolution")
foreach ($name in $graph.Keys) {
    $g = $guids[$name]
    [void]$sb.AppendLine("`t`t$g.Debug|Any CPU.ActiveCfg = Debug|Any CPU")
    [void]$sb.AppendLine("`t`t$g.Debug|Any CPU.Build.0 = Debug|Any CPU")
    [void]$sb.AppendLine("`t`t$g.Release|Any CPU.ActiveCfg = Release|Any CPU")
    [void]$sb.AppendLine("`t`t$g.Release|Any CPU.Build.0 = Release|Any CPU")
}
[void]$sb.AppendLine("`tEndGlobalSection")
[void]$sb.AppendLine("`tGlobalSection(SolutionProperties) = preSolution")
[void]$sb.AppendLine("`t`tHideSolutionNode = FALSE")
[void]$sb.AppendLine("`tEndGlobalSection")
[void]$sb.AppendLine("`tGlobalSection(NestedProjects) = preSolution")
foreach ($name in $folders.Keys) {
    foreach ($child in $folders[$name]) {
        [void]$sb.AppendLine("`t`t$($guids[$child]) = $($folderIds[$name])")
    }
}
[void]$sb.AppendLine("`tEndGlobalSection")
[void]$sb.AppendLine("EndGlobal")

$slnPath = Join-Path $Root "Contoso.sln"
Set-Content -Path $slnPath -Value $sb.ToString() -Encoding UTF8

"created $($graph.Count) projects"
"solution: $slnPath"
