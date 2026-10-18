# Gera os arquivos da marca do Auxiliar a partir dos SVG mestres de assets\:
#   marca.svg     símbolo (24 px para cima)
#   marca-16.svg  símbolo de 16/20 px (alinhado ao pixel, só branco)
#   logo.svg      símbolo + AUXILIAR (tela de login)
# Saídas (as que o build embute no exe):
#   app.ico       16, 20, 24, 32, 40, 48, 64, 96, 128 (BMP 32 bits) + 256 (PNG)
#   favicon.png   símbolo 512 px (faixa de marca da sidebar)
#   logo.png      900 px (login)
#
# Renderiza com o Chrome instalado (modo headless): antialiasing do Skia, cada tamanho
# desenhado no tamanho final — nada é reduzido de uma imagem maior.
# Uso: powershell -ExecutionPolicy Bypass -File assets\gerar-marca.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = $PSScriptRoot
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw 'Chrome ou Edge não encontrado (necessário para renderizar os SVG).' }

function Renderizar([string]$svg, [int]$tam, [string]$png) {
  $tmp = Join-Path $env:TEMP ('marca_' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $tmp | Out-Null
  try {
    $conteudo = Get-Content (Join-Path $dir $svg) -Raw -Encoding UTF8
    $html = '<!doctype html><html><head><meta charset="utf-8"><style>html,body{margin:0;padding:0;background:transparent;overflow:hidden}' +
            "svg{display:block;width:${tam}px;height:${tam}px}</style></head><body>$conteudo</body></html>"
    [IO.File]::WriteAllText("$tmp\p.html", $html, (New-Object Text.UTF8Encoding $false))
    $a = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--window-size=$tam,$tam",
           '--default-background-color=00000000', "--user-data-dir=$tmp\perfil", "--screenshot=$tmp\saida.png", "file:///$($tmp -replace '\\','/')/p.html")
    Start-Process $chrome -ArgumentList $a -Wait -WindowStyle Hidden | Out-Null
    # o Chrome grava num caminho sem acento (a pasta "Farmácia" falha) e depois copiamos
    if (-not (Test-Path "$tmp\saida.png")) { throw "falhou ao renderizar $svg em $tam px" }
    Copy-Item "$tmp\saida.png" $png -Force
  } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
}

$tmpPng = Join-Path $env:TEMP ('marca_png_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmpPng | Out-Null

Renderizar 'marca.svg' 512 "$dir\favicon.png"
Renderizar 'logo.svg' 900 "$dir\logo.png"

# ---- app.ico ----
function EntradaBmp([string]$arquivo) {
  $b = New-Object Drawing.Bitmap $arquivo
  try {
    $w = $b.Width; $h = $b.Height
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $w; $x++) { $c = $b.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $linha = [int]([Math]::Ceiling($w / 32.0) * 4)
    for ($y = $h - 1; $y -ge 0; $y--) {
      $bits = New-Object byte[] $linha
      for ($x = 0; $x -lt $w; $x++) { if ($b.GetPixel($x, $y).A -eq 0) { $i = [int][Math]::Floor($x / 8); $bits[$i] = $bits[$i] -bor (0x80 -shr ($x % 8)) } }
      $bw.Write($bits)
    }
    $bw.Flush(); return ,($ms.ToArray())
  } finally { $b.Dispose() }
}

$tamanhos = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$dados = New-Object System.Collections.ArrayList
foreach ($t in $tamanhos) {
  $png = "$tmpPng\$t.png"
  if ($t -le 20) { Renderizar 'marca-16.svg' $t $png } else { Renderizar 'marca.svg' $t $png }
  if ($t -eq 256) { [void]$dados.Add([IO.File]::ReadAllBytes($png)) } else { [void]$dados.Add([byte[]](EntradaBmp $png)) }
}
$ico = New-Object IO.MemoryStream; $iw = New-Object IO.BinaryWriter($ico)
$iw.Write([int16]0); $iw.Write([int16]1); $iw.Write([int16]$tamanhos.Count)
$ofs = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
  $t = $tamanhos[$i]; $tb = [byte]0; if ($t -lt 256) { $tb = [byte]$t }
  [byte[]]$d = $dados[$i]
  $iw.Write($tb); $iw.Write($tb); $iw.Write([byte]0); $iw.Write([byte]0); $iw.Write([int16]1); $iw.Write([int16]32)
  $iw.Write([int]$d.Length); $iw.Write([int]$ofs); $ofs += $d.Length
}
foreach ($d in $dados) { $iw.Write([byte[]]$d) }
$iw.Flush(); [IO.File]::WriteAllBytes("$dir\app.ico", $ico.ToArray())
Remove-Item $tmpPng -Recurse -Force -ErrorAction SilentlyContinue
"Marca gerada em $dir (app.ico, favicon.png, logo.png)"
