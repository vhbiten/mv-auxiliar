# Gestao das chaves de acesso do mv-auxiliar.
#
# Cada instalacao tem um licenca.txt (a chave) ao lado do exe. Aqui no projeto,
# licencas.txt guarda so os HASHES autorizados + um rotulo por linha, e e publicado
# como asset da release fixa "licencas" — e dela que o app valida ao abrir
# (src\core\Licenca.cs). Chave em claro NUNCA fica em arquivo do projeto.
#
# Uso:
#   .\licenca.ps1 -Nova "farmacia-central"                    gera chave e MOSTRA no terminal
#   .\licenca.ps1 -Nova "outro-pc" -Arquivo caminho\licenca.txt   grava a chave direto no arquivo
#   .\licenca.ps1 -Revogar "farmacia-central"                 tira da lista -> instalacao bloqueia
#   .\licenca.ps1 -Listar
#   .\licenca.ps1 -Publicar                                   re-sobe a lista (apos editar na mao)
param(
    [string]$Nova = "",
    [string]$Revogar = "",
    [string]$Arquivo = "",
    [switch]$Listar,
    [switch]$Publicar
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$gh = "C:\Program Files\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) { $gh = "gh" }
$repo = "vhbiten/mv-auxiliar"
$lista = Join-Path $PSScriptRoot "licencas.txt"
$utf8 = New-Object System.Text.UTF8Encoding($false)

if (-not (Test-Path $lista)) {
    [IO.File]::WriteAllText($lista, "# Chaves autorizadas do mv-auxiliar: hash sha256 da chave + rotulo, um por linha.`n# Gerencie com licenca.ps1 (a chave em claro nao existe aqui, so na instalacao).`n", $utf8)
}

function Publicar-Lista {
    # Sondagem tolerante: o "release not found" sai no stderr e, com ErrorActionPreference
    # Stop, o PowerShell 5 transformaria isso em excecao antes de olharmos o exit code.
    $ea = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    & $gh release view licencas -R $repo 2>&1 | Out-Null
    $achou = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $ea
    if (-not $achou) {
        & $gh release create licencas $lista -R $repo --title "Controle de licencas" `
            --notes "Lista de instalacoes autorizadas (hashes das chaves)." --latest=false | Out-Null
    } else {
        & $gh release upload licencas $lista -R $repo --clobber | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar a lista no GitHub." }
    Write-Output "Lista publicada."
}

function Hash-Chave([string]$chave) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $chaveNorm = $chave.Trim().ToUpperInvariant().Replace(" ", "")
    ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($chaveNorm)) | ForEach-Object { $_.ToString("x2") }) -join ""
}

if ($Nova) {
    $linhas = [IO.File]::ReadAllLines($lista)
    if ($linhas | Where-Object { $_ -match "\s$([regex]::Escape($Nova))\s*$" }) { throw "Ja existe o rotulo '$Nova'." }

    # Chave XXXXX-XXXXX-XXXXX-XXXXX, sem caracteres ambiguos (I L O 0 1)
    $alfabeto = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] 20
    $rng.GetBytes($bytes)
    $chars = foreach ($b in $bytes) { $alfabeto[$b % $alfabeto.Length] }
    $chave = (-join $chars[0..4]) + "-" + (-join $chars[5..9]) + "-" + (-join $chars[10..14]) + "-" + (-join $chars[15..19])

    # A chave sai da memoria ANTES de qualquer coisa que possa falhar (publicar):
    # arquivo pedido ou terminal — nunca as duas coisas, nunca um log.
    if ($Arquivo) {
        [IO.File]::WriteAllText($Arquivo, $chave, $utf8)
        Write-Output "Chave de '$Nova' gravada em: $Arquivo (guarde este arquivo; ele nao se recupera, so se troca)."
    } else {
        Write-Output "Chave de '$Nova' (anote agora, ela nao fica salva aqui):"
        Write-Output "  $chave"
        Write-Output "Na instalacao: crie licenca.txt ao lado do mv-auxiliar.exe com essa linha."
    }
    [IO.File]::WriteAllLines($lista, $linhas + ((Hash-Chave $chave) + "  " + $Nova), $utf8)
    Publicar-Lista
}
elseif ($Revogar) {
    $linhas = [IO.File]::ReadAllLines($lista)
    $novas = $linhas | Where-Object { $_ -notmatch "\s$([regex]::Escape($Revogar))\s*$" }
    if ($novas.Count -eq $linhas.Count) { throw "Nao achei o rotulo '$Revogar'." }
    [IO.File]::WriteAllLines($lista, $novas, $utf8)
    Publicar-Lista
    Write-Output "Revogada: '$Revogar'. A instalacao bloqueia na proxima validacao (abertura ou ciclo de 6h)."
}
elseif ($Publicar) {
    Publicar-Lista
}
else {
    Write-Output "Autorizadas hoje (rotulos):"
    foreach ($l in [IO.File]::ReadAllLines($lista)) {
        $s = $l.Trim()
        if ($s.Length -eq 0 -or $s.StartsWith("#")) { continue }
        $p = $s -split "\s+", 2
        Write-Output ("  " + $p[1] + "  (" + $p[0].Substring(0, 12) + "...)")
    }
}
