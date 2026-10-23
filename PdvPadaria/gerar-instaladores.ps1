#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$appDir = $PSScriptRoot
$rootDir = Split-Path -Parent $appDir
$redistDir = Join-Path $appDir 'redist'
$projectPath = Join-Path $appDir 'PdvPadaria.csproj'
$setupPath = Join-Path $appDir 'setup.iss'
$outputDir = Join-Path $appDir 'Output'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $appDir 'prerequisitos.json') | ConvertFrom-Json
$expectedFiles = @(
    'Windows6.1-KB4490628-x86.msu', 'Windows6.1-KB4490628-x64.msu',
    'Windows6.1-KB4474419-v3-x86.msu', 'Windows6.1-KB4474419-v3-x64.msu',
    'Windows6.1-KB4019990-x86.msu', 'Windows6.1-KB4019990-x64.msu',
    'NDP48-x86-x64-AllOS-ENU.exe'
)
if ($manifest.schemaVersion -ne 1 -or $manifest.files.Count -ne 7 -or
    (Compare-Object ($expectedFiles | Sort-Object) ($manifest.files.file | Sort-Object))) {
    throw 'O manifesto deve conter exatamente os sete componentes conhecidos, sem duplicatas.'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
New-Item -ItemType Directory -Path $redistDir -Force | Out-Null
$pascal = @(
    '// Gerado por gerar-instaladores.ps1 a partir de prerequisitos.json.'
    'function DadosDoPacote(const Nome: String; var Url, Hash, Cab, Identidade: String): Boolean;'
    'begin'
    "  Url := ''; Hash := ''; Cab := ''; Identidade := '';"
    '  Result := False;'
)
$index = 0
foreach ($item in $manifest.files) {
    $uri = [Uri]$item.url
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Port -ne 443 -or
        $uri.Host -notin @('catalog.s.download.windowsupdate.com', 'download.visualstudio.microsoft.com', 'download.microsoft.com') -or
        $item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $item.bytes -le 0) {
        throw ('Origem ou integridade invalida no manifesto: ' + $item.file)
    }
    $path = Join-Path $redistDir $item.file
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host ('Baixando componente oficial: ' + $item.file)
        $partialPath = Join-Path $redistDir ('download-' + $item.file)
        Invoke-WebRequest -UseBasicParsing -Uri $uri.AbsoluteUri -OutFile $partialPath
        $path = $partialPath
    }
    if ((Get-Item -LiteralPath $path).Length -ne $item.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.sha256) {
        throw ('Tamanho ou SHA-256 incorreto: ' + $item.file)
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw ('Assinatura Microsoft invalida: ' + $item.file)
    }
    $finalPath = Join-Path $redistDir $item.file
    if ($path -ne $finalPath) { Move-Item -LiteralPath $path -Destination $finalPath }

    $cab = ''
    $identity = ''
    if ($item.file.EndsWith('.msu')) {
        $cab = [string]$item.cab
        $identity = [string]$item.packageIdentity
        if ($cab -ne ([IO.Path]::GetFileNameWithoutExtension($item.file) + '.cab') -or
            $identity -notmatch '^Package_for_KB[0-9]+~31bf3856ad364e35~(x86|amd64)~~[0-9.]+$') {
            throw ('Identidade de pacote invalida: ' + $item.file)
        }
    }
    $prefix = if ($index -eq 0) { '  if' } else { '  else if' }
    $pascal += "$prefix Nome = '$($item.file)' then"
    $pascal += '  begin'
    $pascal += "    Url := '$($item.url.Replace("'", "''"))';"
    $pascal += "    Hash := '$($item.sha256.ToLowerInvariant())';"
    $pascal += "    Cab := '$cab';"
    $pascal += "    Identidade := '$identity';"
    $pascal += '    Result := True;'
    $pascal += '  end'
    $index++
    Write-Host ($item.file + ': tamanho, SHA-256 e assinatura Microsoft conferidos.')
}
$pascal[$pascal.Count - 1] += ';'
$pascal += 'end;'
Set-Content -LiteralPath (Join-Path $redistDir 'prerequisitos.iss') -Value $pascal -Encoding UTF8

# Compilacao: usa uma pasta vazia para nao levar DLLs antigas ao instalador.
[xml]$project = Get-Content -Raw -LiteralPath $projectPath
$version = [string]$project.Project.PropertyGroup.Version
$fileVersion = [string]$project.Project.PropertyGroup.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Versao invalida no projeto.' }
$setup = Get-Content -Raw -LiteralPath $setupPath
$literalVersion = $setup -match ('(?m)^AppVersion=' + [regex]::Escape($version) + '\s*$')
$definedVersion = ($setup -match '(?m)^AppVersion=\{#AppVersao\}\s*$') -and
    ($setup -match ('(?m)^#define\s+AppVersao\s+"' + [regex]::Escape($version) + '"\s*$'))
if (-not ($literalVersion -or $definedVersion)) {
    throw 'AppVersion do setup.iss difere da versao do projeto.'
}
$publishSource = [regex]::Match($setup, '(?m)^Source:\s*"\{#PublishDir\}\\\*";[^\r\n]*Excludes:\s*"([^"]*)"')
$exclusions = @($publishSource.Groups[1].Value.Split(',') | ForEach-Object { $_.Trim() })
if (-not $publishSource.Success -or $exclusions -notcontains '.env' -or
    $setup -match '(?m)^Source:\s*"(?:[^"\r\n]*[\\/])?\.env"') {
    throw 'O setup deve usar PublishDir e excluir o .env de origem.'
}
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$iscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
foreach ($tool in @($dotnet, $iscc)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw ('Ferramenta ausente: ' + $tool) }
}
$stageName = 'instalador-' + $version + '-' + (Get-Date -Format 'yyyyMMddHHmmssfff')
$publishDir = [IO.Path]::GetFullPath((Join-Path $rootDir ('scratch\' + $stageName + '\publish')))
if ((Test-Path -LiteralPath $publishDir) -and (Get-ChildItem -LiteralPath $publishDir -Force | Select-Object -First 1)) {
    throw ('A pasta de publicacao deve estar vazia para evitar arquivos antigos: ' + $publishDir)
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
& $dotnet publish $projectPath -c Release -r win-x86 --self-contained false --output $publishDir /p:PlatformTarget=x86
if ($LASTEXITCODE -ne 0) { throw ('dotnet publish falhou: ' + $LASTEXITCODE) }

$generatedEnv = [IO.Path]::GetFullPath((Join-Path $publishDir '.env'))
if ((Split-Path -Parent $generatedEnv) -ne $publishDir -or $generatedEnv -eq (Join-Path $appDir '.env')) {
    throw 'Caminho inesperado para o .env gerado.'
}
if (Test-Path -LiteralPath $generatedEnv) { Remove-Item -LiteralPath $generatedEnv -Force }
if (Get-ChildItem -LiteralPath $publishDir -Filter '.env' -Recurse -Force) {
    throw 'A publicacao ainda contem .env; o instalador nao sera gerado.'
}
$exe = Get-Item -LiteralPath (Join-Path $publishDir 'PdvPadaria.exe')
if ($exe.VersionInfo.FileVersion -ne $fileVersion) { throw 'Executavel publicado com versao incorreta.' }

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
& $iscc ("/DPublishDir=$publishDir") ("/O$outputDir") $setupPath
if ($LASTEXITCODE -ne 0) { throw ('ISCC do instalador pequeno falhou: ' + $LASTEXITCODE) }
& $iscc '/DOFFLINE' ("/DPublishDir=$publishDir") ("/O$outputDir") $setupPath
if ($LASTEXITCODE -ne 0) { throw ('ISCC do instalador completo falhou: ' + $LASTEXITCODE) }

$installers = foreach ($name in @('Setup_PadariaVenancio.exe', 'Setup_PadariaVenancio_Completo.exe')) {
    $path = Join-Path $outputDir $name
    $file = Get-Item -LiteralPath $path
    [ordered]@{ file = $name; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ version = $version; installers = @($installers) } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $outputDir 'instaladores.json') -Encoding UTF8

# Atualiza somente metadados locais; publique o pacote completo antes deste manifesto.
$versionPath = Join-Path $rootDir 'docs\version.json'
$versionMetadata = Get-Content -Raw -LiteralPath $versionPath | ConvertFrom-Json
$newMetadata = [ordered]@{
    version = $version
    url = 'https://raw.githubusercontent.com/azelfo/pdv-padaria/main/PdvPadaria/Output/Setup_PadariaVenancio.exe'
    sha256 = $installers[0].sha256
    fullUrl = 'https://github.com/azelfo/pdv-padaria/releases/download/v' + $version + '/Setup_PadariaVenancio_Completo.exe'
    fullSha256 = $installers[1].sha256
}
foreach ($property in $newMetadata.Keys) {
    $versionMetadata | Add-Member -MemberType NoteProperty -Name $property -Value $newMetadata[$property] -Force
}
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($versionPath, (($versionMetadata | ConvertTo-Json -Depth 10) + [Environment]::NewLine), $utf8WithoutBom)

$instructions = @(
    ('PDV Padaria Venancio - versao ' + $version)
    ''
    ($installers[0].file + ' - ' + $installers[0].bytes + ' bytes: atualizacao menor; pode baixar componentes ausentes.')
    ($installers[1].file + ' - ' + $installers[1].bytes + ' bytes: pacote completo, recomendado para Windows 7 SP1 com falha ao abrir o PDV.')
    ''
    'Execute como administrador. Aceite os reinicios solicitados; a instalacao continua no proximo login de administrador ou ao abrir o instalador novamente.'
    'O pacote inclui apenas os componentes Microsoft necessarios ao PDV; nao instala todas as atualizacoes do Windows e nao instala o Service Pack 1.'
    'Em caso de erro, guarde o log Setup Log na pasta %TEMP%. Consulte tambem C:\Windows\Logs\DISM\dism.log e os logs dd_NDP48 em %TEMP% quando indicados.'
    ''
    ('Publicacao: envie primeiro o pacote completo para a Release v' + $version + ' no GitHub e confirme o download. Depois publique o instalador menor e docs/version.json no branch main.')
    'Este script prepara arquivos locais; nao publica automaticamente. Os hashes e tamanhos estao em instaladores.json.'
)
[IO.File]::WriteAllLines((Join-Path $outputDir 'LEIA-ME.txt'), $instructions, $utf8WithoutBom)
Write-Host ('Instaladores, hashes e LEIA-ME gerados em ' + $outputDir + '; docs/version.json atualizado localmente.')
