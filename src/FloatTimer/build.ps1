param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\outputs\FloatTimer-Portable'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap 64,64
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::FromArgb(21,42,50))
$pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(101,222,186)),4
$graphics.DrawEllipse($pen,12,12,40,40)
$pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$graphics.DrawLine($pen,32,21,32,33)
$graphics.DrawLine($pen,32,33,42,38)
$iconPath = Join-Path $PSScriptRoot 'FloatTimer.ico'
$icon = [Drawing.Icon]::FromHandle($bitmap.GetHicon())
$stream = [IO.File]::Create($iconPath)
$icon.Save($stream)
$stream.Dispose()
$icon.Dispose()
$graphics.Dispose()
$pen.Dispose()
$bitmap.Dispose()
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) { $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$references = @('System.dll','System.Core.dll','System.Runtime.Serialization.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$bellAsset = Join-Path $PSScriptRoot 'assets\desk-bell.wav'
$audioCredits = Join-Path $PSScriptRoot 'assets\音频来源.txt'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/win32icon:$iconPath" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/resource:$bellAsset,FloatTimer.DeskBell.wav" "/resource:$audioCredits,FloatTimer.AudioCredits.txt" "/out:$(Join-Path $OutputDirectory 'FloatTimer.exe')" @references (Join-Path $PSScriptRoot 'FloatTimer.cs') (Join-Path $PSScriptRoot 'AudioSettings.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
$quickGuide = Join-Path $PSScriptRoot '快速使用指南.txt'
if (Test-Path -LiteralPath $quickGuide) { Copy-Item -LiteralPath $quickGuide -Destination $OutputDirectory -Force }
Write-Output (Join-Path $OutputDirectory 'FloatTimer.exe')
