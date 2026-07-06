# Compila o app num unico .exe nativo, usando o csc do .NET Framework
# (presente em todo Windows). Nao precisa de Visual Studio nem dotnet.
$ErrorActionPreference = "Stop"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
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
    "System.Xml.Linq.dll"
)
$refArgs = $refs | ForEach-Object { "/reference:$_" }

# Imagens/ícone embutidos no exe (mantém arquivo único)
$resArgs = @(
    "/resource:assets\logo.png,logo.png",
    "/resource:assets\favicon.png,favicon.png",
    "/resource:assets\app.ico,app.ico"
)

# /target:winexe = app de janela (sem console preto atras); /win32icon = ícone do .exe
# /codepage:65001 = lê os .cs como UTF-8 (acentos PT-BR e glifos ✓ × … corretos)
& $csc /nologo /codepage:65001 /target:winexe /win32icon:assets\app.ico /out:$out $refArgs $resArgs $sources

if ($LASTEXITCODE -eq 0) {
    $size = [math]::Round((Get-Item $out).Length / 1KB, 1)
    Write-Output "OK -> $out ($size KB)"
} else {
    Write-Output "Falha na compilacao (exit $LASTEXITCODE)"
}
