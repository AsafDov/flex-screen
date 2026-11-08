# Builds the deck simulator: simulator\build\flex-screen-sim.exe
#
#   powershell -ExecutionPolicy Bypass -File simulator\build.ps1
#
# Needs Visual Studio's C++ build tools (MSVC) and the firmware's PlatformIO libraries
# (pio run -d firmware fetches them); CMake and Ninja come from PlatformIO's packages.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$build = Join-Path $here 'build'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'No Visual Studio with the C++ build tools found' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'

$pio = Join-Path $env:USERPROFILE '.platformio\packages'
$cmake = Join-Path $pio 'tool-cmake\bin\cmake.exe'
$ninja = Join-Path $pio 'tool-ninja\ninja.exe'
foreach ($tool in $cmake, $ninja) { if (-not (Test-Path $tool)) { throw "Missing $tool (a PlatformIO package)" } }

# MSVC's environment (compiler, headers, libraries), as a developer prompt would set it.
cmd /c "`"$vcvars`" >nul && set" | ForEach-Object {
  if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1], $matches[2]) }
}

if (-not (Test-Path (Join-Path $build 'build.ninja'))) {
  & $cmake -S $here -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" -DCMAKE_BUILD_TYPE=Release `
    -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl
  if ($LASTEXITCODE) { throw 'cmake configure failed' }
}
& $cmake --build $build
if ($LASTEXITCODE) { throw 'build failed' }
Write-Host "Built $(Join-Path $build 'flex-screen-sim.exe')"
