; Gere os dois pacotes com .\gerar-instaladores.ps1 (valida os redistribuíveis Microsoft).
#define AppVersao "1.1.18"
#ifndef PublishDir
  #define PublishDir "bin\Release\net48\publish"
#endif
#ifdef OFFLINE
  #define NomeSaida "Setup_PadariaVenancio_Completo"
#else
  #define NomeSaida "Setup_PadariaVenancio"
#endif

[Setup]
AppId={{C6F2A3F4-5987-45CC-AB1B-7AA8D4D4A994}
AppName=PDV - Padaria Venâncio
AppVersion={#AppVersao}
AppPublisher=Padaria Venâncio
AppPublisherURL=https://www.padariavenancio.com.br
DefaultDirName={autopf}\PDV Padaria Venancio
DefaultGroupName=PDV Padaria Venancio
DisableProgramGroupPage=yes
SetupIconFile=Resources\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename={#NomeSaida}
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
; Os pré-requisitos ficam antes do aplicativo para extração durante PrepareToInstall.
#ifdef OFFLINE
Source: "redist\Windows6.1-KB4490628-x86.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4490628-x64.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4474419-v3-x86.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4474419-v3-x64.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4019990-x86.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4019990-x64.msu"; Flags: dontcopy noencryption
Source: "redist\NDP48-x86-x64-AllOS-ENU.exe"; Flags: dontcopy noencryption
#else
; A reparação gráfica também funciona offline no pacote menor de atualização.
Source: "redist\Windows6.1-KB4019990-x86.msu"; Flags: dontcopy noencryption
Source: "redist\Windows6.1-KB4019990-x64.msu"; Flags: dontcopy noencryption
#endif
; Nunca distribuir configuração ou credencial da máquina que gerou o pacote.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: ".env,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: ".env.exemplo"; DestDir: "{app}"; DestName: ".env"; Flags: onlyifdoesntexist uninsneveruninstall
Source: ".env.exemplo"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Somente restos do antigo executável .NET 8; o banco e a configuração são preservados.
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\PdvPadaria.runtimeconfig.json"
Type: files; Name: "{app}\PdvPadaria.deps.json"

[Icons]
Name: "{group}\PDV - Padaria Venâncio"; Filename: "{app}\PdvPadaria.exe"
Name: "{autodesktop}\PDV - Padaria Venâncio"; Filename: "{app}\PdvPadaria.exe"

[Run]
Filename: "{app}\PdvPadaria.exe"; Description: "{cm:LaunchProgram,PDV - Padaria Venâncio}"; Flags: nowait postinstall skipifsilent; Check: PodeAbrirPdv
; O atualizador usa /SILENT /NORESTART para deixar erros e reinícios visíveis.
Filename: "{app}\PdvPadaria.exe"; Flags: nowait runasoriginaluser; Check: DeveReabrirSilenciosamente

[Code]
#include "instalador-politica.iss"
#include "redist\prerequisitos.iss"

const
  REG_NETFX = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';
  REG_CBS = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing';
  REG_REINICIO = 'SOFTWARE\PadariaVenancio\Instalacao\AguardandoReinicio';
  REG_RUNONCE = 'SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce';
  NOME_RETOMADA = 'PDVPadariaSetup{#AppVersao}';

var
  Pronto: Boolean;

#include "instalador-nativo.iss"

function RaizNativa: Integer;
begin
  if IsWin64 then Result := HKLM64 else Result := HKLM32;
end;

function Windows7: Boolean;
var V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := (V.Major = 6) and (V.Minor = 1);
end;

function SistemaNativo: String;
begin
  if IsWin64 then Result := ExpandConstant('{sysnative}')
  else Result := ExpandConstant('{sys}');
end;

function NetFramework48Instalado: Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM32, REG_NETFX, 'Release', Release);
  if Result then Result := Release >= 528040;
end;


function PodeAbrirPdv: Boolean;
begin
  Result := Pronto and not RegKeyExists(HKLM32, REG_REINICIO);
end;

function DeveReabrirSilenciosamente: Boolean;
begin
  // Na retomada pode estar conectado outro administrador, diferente do operador do caixa.
  Result := WizardSilent and PodeAbrirPdv and
    (ExpandConstant('{param:RETOMAR|0}') <> '1');
end;

function InitializeSetup: Boolean;
var V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  Result := not ((V.Major < 6) or ((V.Major = 6) and
    ((V.Minor < 1) or ((V.Minor = 1) and (V.ServicePackMajor < 1)) or (V.Minor = 2))));
  if not Result then
    MsgBox('O PDV precisa de Windows 7 com Service Pack 1, Windows 8.1, 10 ou 11.' + #13#10 +
      'Este pacote não instala Service Pack 1 nem troca a versão do Windows.', mbCriticalError, MB_OK);
end;

function AgendarRetomada(var NeedsRestart: Boolean): String;
var Pasta, Destino, Comando: String; Chave: THandle; Disposicao: Cardinal;
begin
  Pronto := False;
  NeedsRestart := True;
  Result := 'Reinicie o computador para concluir os componentes da Microsoft.' + #13#10 +
    'Depois do reinício, abra este instalador novamente.';
  // Registrar a barreira antes do cache: falta de espaço não pode permitir pular o boot.
  if CriarChaveVolatil(HKLM, REG_REINICIO, 0, '', 1, $20206, 0, Chave, Disposicao) <> 0 then begin
    Result := Result + #13#10 + 'Não foi possível registrar a retomada automática.';
    Exit;
  end;
  FecharChave(Chave);
  // Cache em Common Files: um usuário comum não pode trocar o executável da retomada elevada.
  Pasta := ExpandConstant('{commoncf32}\PadariaVenancio\Instalacao\{#AppVersao}');
  Destino := Pasta + '\{#NomeSaida}.exe';
  if not ForceDirectories(Pasta) then Exit;
  if CompareText(ExpandConstant('{srcexe}'), Destino) <> 0 then
    if not CopyFile(ExpandConstant('{srcexe}'), Destino, False) then Exit;
  if CompareText(GetSHA256OfFile(Destino), GetSHA256OfFile(ExpandConstant('{srcexe}'))) <> 0 then Exit;
  Comando := AddQuotes(Destino) + ' /RETOMAR=1 /SILENT /NORESTART /DIR=' + AddQuotes(WizardDirValue);
  if RegWriteStringValue(HKLM32, REG_RUNONCE, NOME_RETOMADA, Comando) then
    Result := 'Reinicie o computador para concluir os componentes da Microsoft.' + #13#10 +
      'A instalação continuará no próximo login de um administrador. Você também pode abrir este instalador novamente.';
end;

function EstadoDoPacote(const Identidade: String): Cardinal;
begin
  if not RegQueryDWordValue(RaizNativa, REG_CBS + '\Packages\' + Identidade, 'CurrentState', Result) then
    Result := 0;
end;

function PrepararArquivo(const Nome: String; var Caminho, Cab, Identidade: String): String;
var Url, Hash: String;
begin
  Result := '';
  if not DadosDoPacote(Nome, Url, Hash, Cab, Identidade) then begin
    Result := 'Pré-requisito desconhecido: ' + Nome;
    Exit;
  end;
  Caminho := ExpandConstant('{tmp}\') + Nome;
  try
#ifdef OFFLINE
    ExtractTemporaryFile(Nome);
#else
    if Pos('KB4019990', Nome) > 0 then ExtractTemporaryFile(Nome)
    else DownloadTemporaryFile(Url, Nome, Hash, nil);
#endif
    if CompareText(GetSHA256OfFile(Caminho), Hash) <> 0 then
      Result := 'O arquivo ' + Nome + ' não passou na verificação de integridade. Baixe o instalador completo novamente.';
  except
    Log(GetExceptionMessage);
    Result := 'Não foi possível preparar ' + Nome + '.' + #13#10 +
      'Use Setup_PadariaVenancio_Completo.exe, que contém todos os componentes e funciona sem download durante a instalação.';
  end;
end;

function InstalarAtualizacao(const KB: String; var NeedsRestart: Boolean; Reparar: Boolean): String;
var Nome, Url, Hash, Cab, Identidade, Arquivo, Pasta: String;
    Estado: Cardinal; Codigo, Decisao: Integer;
begin
  Result := '';
  Nome := 'Windows6.1-' + KB;
  if IsWin64 then Nome := Nome + '-x64.msu' else Nome := Nome + '-x86.msu';
  if not DadosDoPacote(Nome, Url, Hash, Cab, Identidade) then begin
    Result := 'Pacote ausente do manifesto: ' + Nome;
    Exit;
  end;
  Estado := EstadoDoPacote(Identidade);
  if Estado = 96 then begin Result := AgendarRetomada(NeedsRestart); Exit; end;
  if PacoteSatisfeito(Estado) and not Reparar then Exit;
  WizardForm.PreparingLabel.Caption := 'Preparando o Windows 7: ' + KB + '. Aguarde a conclusão.';
  Result := PrepararArquivo(Nome, Arquivo, Cab, Identidade);
  if Result <> '' then Exit;
  Pasta := ExpandConstant('{tmp}\') + KB;
  if not ForceDirectories(Pasta) then begin Result := 'Não foi possível criar a pasta temporária de ' + KB; Exit; end;
  if not Exec(SistemaNativo + '\expand.exe', AddQuotes(Arquivo) + ' -F:' + Cab + ' ' + AddQuotes(Pasta),
    '', SW_HIDE, ewWaitUntilTerminated, Codigo) then begin Result := 'Não foi possível extrair ' + KB; Exit; end;
  if (Codigo <> 0) or not FileExists(Pasta + '\' + Cab) then begin
    Result := 'Falha ao extrair ' + KB + ' (código ' + IntToStr(Codigo) + ').'; Exit;
  end;
  // CAB local evita a busca online do Windows Update; o DISM mantém a validação de aplicabilidade.
  if not Exec(SistemaNativo + '\dism.exe', '/Online /Add-Package /PackagePath:' + AddQuotes(Pasta + '\' + Cab) +
    ' /Quiet /NoRestart', '', SW_HIDE, ewWaitUntilTerminated, Codigo) then begin
    Result := 'Não foi possível executar a instalação de ' + KB; Exit;
  end;
  Estado := EstadoDoPacote(Identidade);
  Log(KB + ': código=' + IntToStr(Codigo) + '; estado CBS=' + IntToStr(Estado));
  Decisao := ResultadoDaAtualizacao(Codigo, Estado);
  if Decisao = 0 then begin
    Result := 'O Windows não concluiu ' + KB + ' (código ' + IntToStr(Codigo) + ').' + #13#10 +
      'A instalação do PDV foi interrompida. Consulte o log do instalador e C:\Windows\Logs\DISM\dism.log.';
    Exit;
  end;
  if (Decisao = 2) or (KB = 'KB4474419-v3') then
    Result := AgendarRetomada(NeedsRestart);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Arquivo, Cab, Identidade: String; Codigo: Integer;
begin
  Pronto := False;
  Result := '';
  if RegKeyExists(HKLM32, REG_REINICIO) then begin Result := AgendarRetomada(NeedsRestart); Exit; end;
  if Windows7 then begin
    if not NetFramework48Instalado then begin
      if RegKeyExists(RaizNativa, REG_CBS + '\RebootPending') then begin
        Result := AgendarRetomada(NeedsRestart); Exit;
      end;
      Result := InstalarAtualizacao('KB4490628', NeedsRestart, False);
      if Result <> '' then Exit;
      Result := InstalarAtualizacao('KB4474419-v3', NeedsRestart, False);
      if Result <> '' then Exit;
    end;
    if not ComponenteGraficoFunciona then begin
      Result := InstalarAtualizacao('KB4019990', NeedsRestart, True);
      if Result <> '' then Exit;
      if not ComponenteGraficoFunciona then begin
        Result := 'O componente gráfico D3DCompiler_47.dll continua sem carregar (código ' + IntToStr(ErroGrafico) + ').' + #13#10 +
          'O Windows precisa de reparo; o PDV não será aberto com esse componente indisponível.';
        Exit;
      end;
    end;
  end;
  if not NetFramework48Instalado then begin
    WizardForm.PreparingLabel.Caption := 'Instalando .NET Framework 4.8 da Microsoft. Isso pode levar alguns minutos.';
    Result := PrepararArquivo('NDP48-x86-x64-AllOS-ENU.exe', Arquivo, Cab, Identidade);
    if Result <> '' then Exit;
    if not Exec(Arquivo, '/q /norestart', '', SW_SHOW, ewWaitUntilTerminated, Codigo) then begin
      Result := 'Não foi possível executar o instalador do .NET Framework 4.8.'; Exit;
    end;
    Log('.NET Framework: código=' + IntToStr(Codigo));
    if (Codigo = 3010) or (Codigo = 1641) then begin Result := AgendarRetomada(NeedsRestart); Exit; end;
    if ((Codigo <> 0) and (Codigo <> 1638)) or not NetFramework48Instalado then begin
      Result := 'O .NET Framework 4.8 não foi instalado (código ' + IntToStr(Codigo) + ').' + #13#10 +
        'A instalação do PDV foi interrompida. Consulte os logs dd_NDP48 na pasta temporária do Windows.';
      Exit;
    end;
  end;
  Pronto := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RegDeleteValue(HKLM32, REG_RUNONCE, NOME_RETOMADA);
end;
