[Setup]
AppName=Testes dos pré-requisitos PDV
AppVersion=1
DefaultDirName={tmp}\pdv-teste
PrivilegesRequired=lowest
Uninstallable=no
CreateAppDir=no
OutputDir=..\..\scratch\testes-instalador
OutputBaseFilename=testes-politica

[Code]
#include "..\..\PdvPadaria\instalador-politica.iss"
#include "..\..\PdvPadaria\instalador-nativo.iss"

procedure Conferir(Obtido, Esperado: Integer; const Caso: String);
begin
  if Obtido <> Esperado then RaiseException(Caso + ': decisão incorreta');
end;

function InitializeSetup: Boolean;
var Chave: THandle; Disposicao: Cardinal; Caminho: String;
begin
  Conferir(ResultadoDaAtualizacao(0, 112), 1, 'Instalado');
  Conferir(ResultadoDaAtualizacao(0, 80), 1, 'Substituído por atualização posterior');
  Conferir(ResultadoDaAtualizacao(0, 128), 1, 'Permanente');
  Conferir(ResultadoDaAtualizacao(0, 64), 0, 'Somente preparado não é instalado');
  Conferir(ResultadoDaAtualizacao(0, 101), 0, 'Instalação parcial');
  Conferir(ResultadoDaAtualizacao(0, 5), 0, 'Remoção pendente');
  Conferir(ResultadoDaAtualizacao(0, 0), 0, 'Código zero sem confirmação CBS');
  Conferir(ResultadoDaAtualizacao(0, 96), 2, 'Instalação pendente');
  Conferir(ResultadoDaAtualizacao(3010, 112), 2, 'Reinício mesmo com pacote instalado');
  Conferir(ResultadoDaAtualizacao(3010, 0), 2, 'Estado só disponível após reiniciar');
  Conferir(ResultadoDaAtualizacao(-2146498530, 80), 1, 'Não aplicável com substituição comprovada');
  Conferir(ResultadoDaAtualizacao(-2146498530, 0), 0, 'Não aplicável sem comprovação');
  Conferir(ResultadoDaAtualizacao(-2146498530, 64), 0, 'Não aplicável só preparado');
  Conferir(ResultadoDaAtualizacao(5, 112), 0, 'Erro não vira sucesso por estado anterior');
  if not ComponenteGraficoFunciona then RaiseException('Carga da DLL x86 e exportação D3DCompile falharam');
  Caminho := 'Software\PdvTesteInstalador\Volatil';
  Conferir(CriarChaveVolatil(HKCU, Caminho, 0, '', 1, $20206, 0, Chave, Disposicao), 0, 'Criação de marcador volátil');
  Conferir(FecharChave(Chave), 0, 'Liberação do handle');
  if not RegKeyExists(HKCU32, Caminho) then RaiseException('Marcador não encontrado na visão x86');
  if not RegDeleteKeyIfEmpty(HKCU32, Caminho) then RaiseException('Limpeza do marcador de teste falhou');
  RegDeleteKeyIfEmpty(HKCU32, 'Software\PdvTesteInstalador');
  if not SaveStringToFile(ExpandConstant('{param:RESULTADO}'), '19 verificacoes passaram', False) then
    RaiseException('Não foi possível registrar o resultado');
  Result := False;
end;
