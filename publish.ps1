# Publica uma versao nova do mv-auxiliar na prateleira de atualizacoes
# (GitHub Releases do repo publico vhbiten/mv-auxiliar). O app da farmacia
# baixa e aplica sozinho na proxima vez que abrir (src\core\AtualizadorApp.cs).
#
# Uso:   .\publish.ps1 -Notas "o que mudou"
#        .\publish.ps1 -Notas "..." -Menor     (0.1.3 -> 0.2.0)
#        .\publish.ps1 -Notas "..." -Maior     (0.1.3 -> 1.0.0)
#        (sem chave de tamanho: 0.1.3 -> 0.1.4)
#
# O que ele faz, na ordem:
#   1. sobe a versao em src\AppConfig.cs (fonte unica, embutida no exe);
#   2. compila (build.ps1);
#   3. monta o pacote: exe + Desbloquear.bat + dados semente de deploy\mv-auxiliar\dados.
#      FICAM FORA: config.txt (IP da rede do hospital, arquivo da maquina) e logs;
#   4. trava de seguranca: nada sai daqui contendo a senha (padrao vindo da variavel
#      de ambiente PEP_PASS, nunca escrito em arquivo) nem o IP do servidor;
#   5. zipa (deploy\mv-auxiliar.zip), guarda snapshot do codigo em snapshots\;
#   6. cria a release vX.Y.Z com mv-auxiliar.zip + versao.txt.
#
# Qualquer falha desfaz o aumento de versao, para a numeracao nao pular.
param(
    [string]$Notas = "",
    [switch]$Menor,
    [switch]$Maior
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$gh = "C:\Program Files\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) { $gh = "gh" }
$repo = "vhbiten/mv-auxiliar"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# --- 1) versao ---------------------------------------------------------------
$cfgPath = Join-Path $PSScriptRoot "src\AppConfig.cs"
$cfgOriginal = [IO.File]::ReadAllText($cfgPath, [Text.Encoding]::UTF8)
if ($cfgOriginal -notmatch 'Versao = "(\d+)\.(\d+)\.(\d+)"') { throw "Nao achei a versao em AppConfig.cs" }
$vMaior = [int]$Matches[1]; $vMenor = [int]$Matches[2]; $vPatch = [int]$Matches[3]
if ($Maior)     { $vMaior++; $vMenor = 0; $vPatch = 0 }
elseif ($Menor) { $vMenor++; $vPatch = 0 }
else            { $vPatch++ }
$versao = "$vMaior.$vMenor.$vPatch"
[IO.File]::WriteAllText($cfgPath, ($cfgOriginal -replace 'Versao = "\d+\.\d+\.\d+"', ('Versao = "' + $versao + '"')), $utf8)
Write-Output "Versao nova: $versao"

$stage = Join-Path $env:TEMP ("mv-auxiliar-pub-" + [guid]::NewGuid().ToString("n").Substring(0, 8))
try {
    # --- 2) compila ----------------------------------------------------------
    $antes = Get-Date
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build.ps1")
    $exe = Join-Path $PSScriptRoot "mv-auxiliar.exe"
    if (-not (Test-Path $exe) -or (Get-Item $exe).LastWriteTime -lt $antes) { throw "Compilacao falhou" }

    # --- 3) pacote -----------------------------------------------------------
    $appDir = Join-Path $stage "mv-auxiliar"
    New-Item -ItemType Directory -Path (Join-Path $appDir "dados") -Force | Out-Null
    Copy-Item $exe (Join-Path $appDir "mv-auxiliar.exe")
    Copy-Item (Join-Path $PSScriptRoot "deploy\mv-auxiliar\Desbloquear.bat") $appDir
    # licenca-carimbo.txt e janela.txt ficam FORA: sao estado da maquina (ultima
    # validacao de licenca; janela maximizada/tamanho), nao semente de instalacao.
    Copy-Item (Join-Path $PSScriptRoot "deploy\mv-auxiliar\dados\*.txt") (Join-Path $appDir "dados") -Exclude @("licenca-carimbo.txt", "janela.txt")

    # --- 4) trava de seguranca ----------------------------------------------
    # Padroes proibidos no que vai a publico: a senha (de PEP_PASS, se definida
    # nesta janela), o IP interno (lido do config.txt local, que fica de fora)
    # e as chaves de licenca em claro (dos licenca.txt locais).
    $proibidos = @()
    if ($env:PEP_PASS) { $proibidos += $env:PEP_PASS }
    $cfgLocal = Join-Path $PSScriptRoot "deploy\mv-auxiliar\config.txt"
    if (Test-Path $cfgLocal) {
        foreach ($l in [IO.File]::ReadAllLines($cfgLocal)) {
            if ($l -match '^\s*host\s*=\s*(\S+)') { $proibidos += ($Matches[1] -replace '^https?://', '') }
        }
    }
    foreach ($lic in @((Join-Path $PSScriptRoot "licenca.txt"), (Join-Path $PSScriptRoot "deploy\mv-auxiliar\licenca.txt"))) {
        if (Test-Path $lic) {
            $ch = [IO.File]::ReadAllText($lic).Trim()
            if ($ch) { $proibidos += $ch }
        }
    }
    $alvos = @(Get-ChildItem -Path (Join-Path $PSScriptRoot "src") -Recurse -Filter *.cs) +
             @(Get-ChildItem -Path $appDir -Recurse -File)
    foreach ($p in $proibidos) {
        $hit = $alvos | Select-String -Pattern ([regex]::Escape($p)) -List -ErrorAction SilentlyContinue
        if ($hit) { throw ("TRAVADO: conteudo sensivel em " + ($hit[0].Path) + " - nada foi publicado.") }
    }
    foreach ($n in @("config.txt", "licenca.txt", "dados\licenca-carimbo.txt", "dados\janela.txt")) {
        if (Test-Path (Join-Path $appDir $n)) { throw "TRAVADO: $n nao pode ir no pacote." }
    }

    # --- 5) zip + snapshot ---------------------------------------------------
    $zip = Join-Path $PSScriptRoot "deploy\mv-auxiliar.zip"
    Compress-Archive -Path $appDir -DestinationPath $zip -Force
    Copy-Item $exe (Join-Path $PSScriptRoot "deploy\mv-auxiliar\mv-auxiliar.exe") -Force   # pasta pronta-para-copiar
    $versaoTxt = Join-Path $stage "versao.txt"
    [IO.File]::WriteAllText($versaoTxt, $versao, $utf8)
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot "snapshots") -Force | Out-Null
    Compress-Archive -Path @("src", "build.ps1", "publish.ps1", "licenca.ps1", "licencas.txt", "app.manifest", "assets") `
        -DestinationPath (Join-Path $PSScriptRoot "snapshots\src-v$versao.zip") -Force

    # --- 6) release ----------------------------------------------------------
    if (-not $Notas) { $Notas = "Atualizacao $versao" }
    & $gh release create "v$versao" $zip $versaoTxt -R $repo --title "v$versao" --notes $Notas --latest
    if ($LASTEXITCODE -ne 0) { throw "gh release create falhou (exit $LASTEXITCODE)" }

    Write-Output ""
    Write-Output "Publicado: https://github.com/$repo/releases/tag/v$versao"
    Write-Output "A farmacia atualiza sozinha na proxima vez que abrir o programa."
}
catch {
    [IO.File]::WriteAllText($cfgPath, $cfgOriginal, $utf8)   # versao volta ao que era
    throw
}
finally {
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
