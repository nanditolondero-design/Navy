# Abre la app con distintos argumentos y guarda una captura de su ventana por escenario.
# Lo usa el workflow "Capturas de pantalla" (windows-latest). Las capturas casi negras se descartan.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$OutDir
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect { public int Left, Top, Right, Bottom; }

public static class Native
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out NativeRect r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out NativeRect r, int size);
}
"@
[Native]::SetProcessDPIAware() | Out-Null

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ── Entorno de demostración ──
$demo = Join-Path $env:RUNNER_TEMP 'demo'
$downloads = Join-Path $env:RUNNER_TEMP 'descargas'
New-Item -ItemType Directory -Force -Path $demo, $downloads | Out-Null

# Archivo de ejemplo para la pestaña de descargas
$fs = [IO.File]::Create((Join-Path $demo 'ejemplo-descarga.zip'))
$fs.SetLength(25MB)
$fs.Close()

# Página local con solicitudes a dominios de anuncios (para mostrar los escudos)
@'
<!doctype html>
<html lang="es"><head><meta charset="utf-8"><title>Página de ejemplo</title>
<style>
  body { font-family: "Segoe UI", sans-serif; max-width: 720px; margin: 48px auto; line-height: 1.6; color: #222; }
  h1 { font-size: 2rem; } .ad { display: none; }
</style></head>
<body>
  <h1>Una página con anuncios</h1>
  <p>Esta página intenta cargar anuncios y rastreadores desde varios dominios. El botón de escudos muestra cuántas solicitudes se bloquearon.</p>
  <p>Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.</p>
  <img class="ad" src="https://ad.doubleclick.net/a1.gif"><img class="ad" src="https://pagead2.googlesyndication.com/a2.gif">
  <img class="ad" src="https://cdn.taboola.com/a3.gif"><img class="ad" src="https://widgets.outbrain.com/a4.gif">
  <img class="ad" src="https://static.criteo.net/a5.gif"><img class="ad" src="https://ib.adnxs.com/a6.gif">
  <img class="ad" src="https://secure.adsrvr.org/a7.gif"><img class="ad" src="https://sb.scorecardresearch.com/a8.gif">
  <img class="ad" src="https://pixel.quantserve.com/a9.gif"><img class="ad" src="https://c.amazon-adsystem.com/a10.gif">
  <script src="https://www.google-analytics.com/analytics.js"></script>
  <script src="https://securepubads.g.doubleclick.net/tag/js/gpt.js"></script>
</body></html>
'@ | Set-Content -Path (Join-Path $demo 'escudos.html') -Encoding UTF8

# Ajustes: guardar descargas sin preguntar, para que la captura no quede esperando un diálogo
$settingsDir = Join-Path $env:LOCALAPPDATA 'Navegador'
New-Item -ItemType Directory -Force -Path $settingsDir | Out-Null
@{ AskDownloadLocation = $false; DownloadFolder = $downloads } | ConvertTo-Json |
    Set-Content -Path (Join-Path $settingsDir 'settings.json') -Encoding UTF8

# Carpeta de ejemplo para el explorador de archivos
$files = Join-Path $env:RUNNER_TEMP 'MisArchivos'
New-Item -ItemType Directory -Force -Path (Join-Path $files 'Documentos'), (Join-Path $files 'Imagenes'), (Join-Path $files 'Proyectos') | Out-Null
$rnd = New-Object System.Random
foreach ($f in @(
    @('Informe-anual.docx', 184320), @('Presupuesto.xlsx', 96256), @('Presentacion.pptx', 2411724),
    @('notas.txt', 1840), @('cancion.mp3', 4718592), @('video-vacaciones.mp4', 52428800)
)) {
    $bytes = New-Object byte[] $f[1]
    $rnd.NextBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $files $f[0]), $bytes)
}
Compress-Archive -Path (Join-Path $files 'notas.txt'), (Join-Path $files 'Informe-anual.docx') -DestinationPath (Join-Path $files 'respaldo.zip') -Force
$bmp = New-Object System.Drawing.Bitmap 320, 200
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::SteelBlue)
$g.Dispose()
$bmp.Save((Join-Path $files 'foto-playa.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$server = Start-Process python -ArgumentList '-m', 'http.server', '8000', '--bind', '127.0.0.1', '--directory', $demo `
    -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

function Test-NotBlank([System.Drawing.Bitmap]$bmp) {
    $sum = 0.0; $n = 0
    for ($x = 10; $x -lt $bmp.Width; $x += [Math]::Max(1, [int]($bmp.Width / 30))) {
        for ($y = 10; $y -lt $bmp.Height; $y += [Math]::Max(1, [int]($bmp.Height / 20))) {
            $c = $bmp.GetPixel($x, $y)
            $sum += ($c.R + $c.G + $c.B) / 3.0
            $n++
        }
    }
    return (($sum / [Math]::Max(1, $n)) -gt 12)
}

function Capture([string]$name, [string[]]$appArgs, [int]$wait) {
    Write-Host "== $name =="
    $p = Start-Process -FilePath $Exe -ArgumentList $appArgs -PassThru
    Start-Sleep -Seconds $wait
    $p.Refresh()
    $h = $p.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) {
        Write-Warning "$name : la app no abrió ventana"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        return
    }
    [Native]::ShowWindow($h, 9) | Out-Null
    [Native]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Seconds 1

    $r = New-Object NativeRect
    if ([Native]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) {
        [Native]::GetWindowRect($h, [ref]$r) | Out-Null
    }
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    if ($w -lt 200 -or $hh -lt 200) {
        Write-Warning "$name : ventana demasiado chica ($w x $hh)"
    } else {
        $bmp = New-Object System.Drawing.Bitmap $w, $hh
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $g.Dispose()
        if (Test-NotBlank $bmp) {
            $bmp.Save((Join-Path $OutDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            Write-Host "$name.png guardada ($w x $hh)"
        } else {
            Write-Warning "$name : captura vacía, se descarta"
        }
        $bmp.Dispose()
    }
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$size = '--size=1100x720'
Capture 'principal'     @($size, '--url=https://es.wikipedia.org/wiki/Navegador_web') 22
Capture 'incognito'     @($size, '--incognito', '--url=https://duckduckgo.com') 22
Capture 'descargas'     @($size, '--url=http://localhost:8000/ejemplo-descarga.zip') 16
Capture 'escudos'       @($size, '--url=http://localhost:8000/escudos.html', '--demo-shields') 22
Capture 'archivos'      @($size, '--url=http://localhost:8000/escudos.html', "--drawer=$files") 20
Capture 'configuracion' @($size, '--open=settings') 14

Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
