param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0',
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishDir = Join-Path $projectRoot 'artifacts\publish'

if (-not $IsccPath) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) {
        $IsccPath = $compiler.Source
    } else {
        $IsccPath = Join-Path $projectRoot '.tools\InnoSetup\ISCC.exe'
        if (-not (Test-Path -LiteralPath $IsccPath)) {
            $toolsDir = Join-Path $projectRoot '.tools'
            New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
            $downloadPath = Join-Path $toolsDir 'innosetup-6.7.4.exe'
            Write-Host 'Descargando Inno Setup (compilador portable)...'
            Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $downloadPath
            $signature = Get-AuthenticodeSignature -LiteralPath $downloadPath
            if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B\.V\.') {
                throw 'La firma del compilador Inno Setup no es valida.'
            }
            $compilerDir = Split-Path -Parent $IsccPath
            $setupArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/PORTABLE=1', '/NOICONS', "/DIR=`"$compilerDir`"")
            $setup = Start-Process -FilePath $downloadPath -ArgumentList $setupArgs -WindowStyle Hidden -Wait -PassThru
            if ($setup.ExitCode -ne 0) { throw "No se pudo preparar Inno Setup: $($setup.ExitCode)" }
        }
    }
}

if (-not (Test-Path -LiteralPath $IsccPath)) { throw "No se encuentra ISCC.exe: $IsccPath" }

& dotnet publish (Join-Path $projectRoot 'UsageTray.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=embedded' "-p:Version=$Version" -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Ha fallado dotnet publish.' }

& $IsccPath "/DAppVersion=$Version" (Join-Path $projectRoot 'installer\UsageTray.iss')
if ($LASTEXITCODE -ne 0) { throw 'Ha fallado la creacion del instalador.' }

Write-Host "Instalador generado: $(Join-Path $projectRoot "artifacts\installer\AI-Usage-Tray-$Version-Setup.exe")"
