# Testa a política e APIs nativas; não instala o PDV nem atualizações do Windows.
$ErrorActionPreference = 'Stop'
$pdvRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$pdvOutput = Join-Path $pdvRoot 'scratch\testes-instalador'
$pdvResultado = Join-Path $pdvOutput ([Guid]::NewGuid().ToString() + '.txt')
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' (Join-Path $PSScriptRoot 'testes.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar os testes do instalador.' }
$pdvArgumentos = '/VERYSILENT /SUPPRESSMSGBOXES /RESULTADO="' + $pdvResultado + '"'
Start-Process -FilePath (Join-Path $pdvOutput 'testes-politica.exe') -ArgumentList $pdvArgumentos -WindowStyle Hidden -Wait
if (-not (Test-Path -LiteralPath $pdvResultado)) { throw 'Teste do instalador falhou.' }
Get-Content -LiteralPath $pdvResultado
