param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$taskRepoRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $taskRepoRoot 'dist' }
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskCompiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }

$taskAssemblyInfo = Join-Path $taskOutput 'AssemblyInfo.generated.cs'
$taskMetadata = @"
using System.Reflection;
using System.Runtime.Versioning;
[assembly: AssemblyTitle("AirPodsSwitch")]
[assembly: AssemblyProduct("AirPodsSwitch")]
[assembly: AssemblyCompany("baka9881")]
[assembly: AssemblyCopyright("Copyright (c) 2026 baka9881")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
[assembly: AssemblyInformationalVersion("$Version")]
[assembly: TargetFramework(".NETFramework,Version=v4.8")]
"@
[IO.File]::WriteAllText($taskAssemblyInfo, $taskMetadata, [Text.UTF8Encoding]::new($false))
$taskSources = @(
    'AppPaths.cs', 'AudioCore.cs', 'NativeBluetoothAudio.cs',
    'AirPodsSwitch.cs', 'CompactSwitchWindow.cs', 'HeadphoneSetup.cs'
) | ForEach-Object { Join-Path (Join-Path $taskRepoRoot 'src') $_ }
$taskExe = Join-Path $taskOutput 'AirPodsSwitch.exe'
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /main:AirPodsSwitch.SwitchMain "/win32manifest:$(Join-Path $taskRepoRoot 'src\app.manifest')" "/out:$taskExe" `
    $taskAssemblyInfo $taskSources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

$taskArchive = Join-Path $taskOutput "AirPodsSwitch-$Version-win-x64.zip"
$taskPackageFiles = @($taskExe, (Join-Path $taskRepoRoot 'README.md'), (Join-Path $taskRepoRoot 'LICENSE'))
Compress-Archive -LiteralPath $taskPackageFiles -DestinationPath $taskArchive -Force
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$taskChecksum = Join-Path $taskOutput 'SHA256SUMS.txt'
[IO.File]::WriteAllText($taskChecksum, "$taskHash  $([IO.Path]::GetFileName($taskArchive))`n", [Text.UTF8Encoding]::new($false))
Write-Output "EXE: $taskExe"
Write-Output "ZIP: $taskArchive"
Write-Output "SHA256: $taskHash"
