param(
    [ValidateSet('all', 'win-x64', 'win-arm64', 'win-x86')][string]$Runtime = 'all',
    [string]$OutputDirectory,
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('artifacts\release\v' + $Version) }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$taskRuntimes = if ($Runtime -eq 'all') { @('win-x64', 'win-arm64', 'win-x86') } else { @($Runtime) }
foreach ($taskRuntime in $taskRuntimes) {
    $taskBuildRoot = Join-Path $taskRoot ('output\publish-' + $taskRuntime)
    $taskPublishRoot = Join-Path $taskBuildRoot 'app'
    dotnet publish (Join-Path $taskRoot 'src\Skapihub.Desktop\Skapihub.Desktop.csproj') -c Release -r $taskRuntime --self-contained false --artifacts-path (Join-Path $taskBuildRoot 'build') -o $taskPublishRoot --nologo -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=embedded -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw ('发布失败：' + $taskRuntime) }
    $taskFiles = @(Get-ChildItem -LiteralPath $taskPublishRoot -Recurse -File)
    if ($taskFiles.Count -ne 1 -or $taskFiles[0].Extension -ne '.exe') {
        $taskFiles | Select-Object FullName, Length | Format-Table | Out-Host
        throw ('发布目录必须只有一个 EXE：' + $taskRuntime)
    }
    $taskArchitecture = $taskRuntime.Substring(4)
    $taskName = 'skapihub-v' + $Version + '-windows-' + $taskArchitecture + '.exe'
    $taskDestination = Join-Path $OutputDirectory $taskName
    Copy-Item -LiteralPath $taskFiles[0].FullName -Destination $taskDestination -Force
    Write-Host ('已生成 {0} ({1:N2} MiB)' -f $taskName, ((Get-Item -LiteralPath $taskDestination).Length / 1MB))
}
