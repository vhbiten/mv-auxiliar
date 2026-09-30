# Compila o app num unico .exe nativo. Compilador preferido: Roslyn vendorado em
# tools\roslyn (C# moderno mirando o MESMO .NET Framework 4.8 in-box). Se a pasta
# nao existir, cai no csc classico do Framework (C# 5) sem mudar mais nada.
$ErrorActionPreference = "Stop"

$roslyn = Join-Path $PSScriptRoot "tools\roslyn\tasks\net472\csc.exe"
if (Test-Path $roslyn) {
    $csc = $roslyn
    $lang = @("/langversion:latest")
} else {
    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    $lang = @()
}
$out = "mv-auxiliar.exe"

# Junta todos os .cs de src\ (a pasta test\ fica de fora de proposito)
$sources = Get-ChildItem -Path src -Recurse -Filter *.cs | ForEach-Object { $_.FullName }

$refs = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll",
    "System.Net.Http.dll",
    "System.Xml.dll",
    "System.Xml.Linq.dll",
    "System.IO.Compression.dll",
    "System.IO.Compression.FileSystem.dll"
)
$refArgs = $refs | ForEach-Object { "/reference:$_" }

# Imagens/ícone embutidos no exe (mantém arquivo único)
$resArgs = @(
    "/resource:assets\logo.png,logo.png",
    "/resource:assets\favicon.png,favicon.png",
    "/resource:assets\app.ico,app.ico"
)

# /target:winexe = app de janela (sem console preto atras); /win32icon = ícone do .exe
# /win32manifest = common controls v6 + DPI-aware (sem ele o app sai BORRADO em 125%)
# /codepage:65001 = lê os .cs como UTF-8 (acentos PT-BR e glifos ✓ × … corretos)
& $csc /nologo /codepage:65001 /target:winexe /win32icon:assets\app.ico /win32manifest:app.manifest /out:$out $lang $refArgs $resArgs $sources

if ($LASTEXITCODE -eq 0) {
    $size = [math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Output "OK -> $out ($size KB, csc: $(Split-Path -Parent $csc))"
} else {
    Write-Output "Falha na compilacao (exit $LASTEXITCODE)"
}
