; Script de Instalação do Inno Setup para o PDV Padaria Venâncio
; Aplicação WPF em .NET Framework 4.8 (roda em Windows 7 SP1, 8.1, 10 e 11)
;
; Este mesmo arquivo gera DOIS instaladores:
;   ISCC setup.iss            -> Setup_PadariaVenancio.exe (pequeno, ~4 MB)
;                                Baixa o .NET Framework 4.8 se faltar. Usado pelo
;                                auto-update, onde a máquina já tem o componente.
;   ISCC /DOFFLINE setup.iss  -> Setup_PadariaVenancio_Completo.exe (~120 MB)
;                                Traz o .NET Framework 4.8 embutido. Necessário no
;                                Windows 7/8.1, onde o download falha: o WinHTTP
;                                dessas versões ainda usa TLS 1.0 e o site da
;                                Microsoft exige TLS 1.2.

#ifdef OFFLINE
  #define NomeSaida "Setup_PadariaVenancio_Completo"
  #define ArquivoNetFx "NDP48-x86-x64-AllOS-ENU.exe"
#else
  #define NomeSaida "Setup_PadariaVenancio"
#endif

[Setup]
AppId={{C6F2A3F4-5987-45CC-AB1B-7AA8D4D4A994}
AppName=PDV - Padaria Venâncio
AppVersion=1.1.4
AppPublisher=Padaria Venâncio
AppPublisherURL=https://www.padariavenancio.com.br
DefaultDirName={autopf}\PDV Padaria Venancio
DefaultGroupName=PDV Padaria Venancio
DisableProgramGroupPage=yes
; Localização do arquivo de ícone de alta resolução para o próprio instalador (.exe)
SetupIconFile=d:\PDV\PdvPadaria\Resources\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename={#NomeSaida}
; Permite instalação administrativa ou por usuário comum
PrivilegesRequired=admin
; Auto-update (UpdateService.cs): fecha o PDV se estiver aberto ao sobrescrever os arquivos
; (defesa extra — o próprio app já se fecha sozinho antes de iniciar o instalador silencioso).
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
; Executável Principal
Source: "d:\PDV\PdvPadaria\bin\Release\net48\publish\PdvPadaria.exe"; DestDir: "{app}"; Flags: ignoreversion
;
; ATENÇÃO — o .env é EXCLUÍDO daqui de propósito. Dois motivos, os dois graves:
;
;   1) SEGREDO: ele guarda STORE_SYNC_TOKEN (credencial de escrita da sincronização),
;      INFINITE_CLIENT_SECRET e a chave PIX. Este instalador é publicado num repositório
;      PÚBLICO no GitHub, então qualquer um que baixasse o .exe teria esses valores.
;   2) IDENTIDADE: STORE_ID e STORE_SYNC_TOKEN dizem QUAL loja é aquele caixa. Copiar o
;      .env de quem gerou o instalador faria toda loja atualizada virar a mesma loja —
;      as vendas de todas cairiam num único STORE_ID e a conferência de estoque pararia
;      de fechar.
;
; O que vai no pacote é o .env.exemplo, sem valores, criado só se ainda não houver .env.
Source: "d:\PDV\PdvPadaria\bin\Release\net48\publish\*"; DestDir: "{app}"; Excludes: ".env"; Flags: ignoreversion recursesubdirs createallsubdirs
; Modelo em branco. "onlyifdoesntexist" garante que a atualização NUNCA sobrescreve a
; configuração da loja; numa instalação nova ele vira o .env a ser preenchido.
Source: "d:\PDV\PdvPadaria\.env.exemplo"; DestDir: "{app}"; DestName: ".env"; Flags: onlyifdoesntexist
Source: "d:\PDV\PdvPadaria\.env.exemplo"; DestDir: "{app}"; Flags: ignoreversion
; Nota: Ajuste os caminhos acima se utilizar a publicação com RID específico (ex: \publish\win-x64\)
#ifdef OFFLINE
; .NET Framework 4.8 embutido (só na versão Completo). "dontcopy" = fica dentro do
; instalador e só é extraído para a pasta temporária quando realmente faltar.
Source: "d:\PDV\PdvPadaria\redist\{#ArquivoNetFx}"; Flags: dontcopy noencryption
#endif

[InstallDelete]
; Restos da versão anterior, que rodava em .NET 8 e usava outro layout de arquivos.
; Sem isto, a pasta ficaria com arquivos órfãos após a migração para .NET Framework 4.8.
Type: filesandordirs; Name: "{app}\runtimes"
Type: files; Name: "{app}\PdvPadaria.runtimeconfig.json"
Type: files; Name: "{app}\PdvPadaria.deps.json"

[Icons]
; Atalho no Menu Iniciar (Criado Obrigatoriamente)
Name: "{group}\PDV - Padaria Venâncio"; Filename: "{app}\PdvPadaria.exe"
; Atalho na Área de Trabalho (Criado Obrigatoriamente)
Name: "{autodesktop}\PDV - Padaria Venâncio"; Filename: "{app}\PdvPadaria.exe"

[Run]
; Opção para iniciar o PDV automaticamente após a conclusão da instalação (instalação manual,
; com wizard visível — o operador vê a caixa de seleção na tela final).
Filename: "{app}\PdvPadaria.exe"; Description: "{cm:LaunchProgram,PDV - Padaria Venâncio}"; Flags: nowait postinstall skipifsilent
; Auto-update silencioso (UpdateService.cs chama Setup com /VERYSILENT): sem wizard, então a
; entrada acima nunca roda (skipifsilent). Esta reabre o PDV sozinho ao final da atualização.
Filename: "{app}\PdvPadaria.exe"; Flags: nowait; Check: ShouldRelaunchSilently

[Code]
const
  // Instalador offline oficial do .NET Framework 4.8 (roda em Windows 7 SP1, 8.1, 10 e 11).
  URL_NETFX48 = 'https://go.microsoft.com/fwlink/?linkid=2088631';
  // Chave onde o Windows registra a versão instalada do .NET Framework 4.x.
  // Release >= 528040 significa 4.8 ou superior (valor documentado pela Microsoft).
  REG_NETFX = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';
  RELEASE_MINIMO_48 = 528040;

function ShouldRelaunchSilently(): Boolean;
begin
  Result := WizardSilent();
end;

// Lê no registro a versão do .NET Framework 4.x instalada. A Microsoft documenta
// que o valor "Release" >= 528040 significa 4.8 ou superior.
function NetFramework48Instalado(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKEY_LOCAL_MACHINE, REG_NETFX, 'Release', Release) then
    Result := Release >= RELEASE_MINIMO_48;
end;

// O .NET Framework 4.8 roda em Windows 7 SP1, 8.1, 10 e 11 — mas NÃO no Windows 7
// sem Service Pack 1, nem no Windows 8.0 "puro" (que precisa do update grátis 8.1),
// nem em versões anteriores. Barra com mensagem clara em vez de instalar um app
// que não vai abrir.
function InitializeSetup(): Boolean;
var
  Versao: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(Versao);

  // Windows 7 = 6.1 (exige SP1) | Windows 8.0 = 6.2 | Windows 8.1 = 6.3 | Win10/11 = 10.0
  if (Versao.Major < 6) or ((Versao.Major = 6) and (Versao.Minor < 1)) then
  begin
    MsgBox('Este computador usa uma versão do Windows muito antiga (anterior ao Windows 7).' + #13#10#13#10 +
           'O PDV precisa de Windows 7 com Service Pack 1, Windows 8.1, Windows 10 ou Windows 11.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  // Windows 7 sem SP1 não recebe o .NET Framework 4.8.
  if (Versao.Major = 6) and (Versao.Minor = 1) and (Versao.ServicePackMajor < 1) then
  begin
    MsgBox('Este Windows 7 está sem o Service Pack 1.' + #13#10#13#10 +
           'Instale o Service Pack 1 pelo Windows Update e rode este instalador de novo.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;

  // Windows 8.0 não recebe o .NET Framework 4.8; o update para 8.1 é gratuito.
  if (Versao.Major = 6) and (Versao.Minor = 2) then
  begin
    MsgBox('Este computador usa o Windows 8, que a Microsoft não atualiza mais.' + #13#10#13#10 +
           'Atualize gratuitamente para o Windows 8.1 (pela Loja do Windows ou Windows Update) ' +
           'e rode este instalador de novo.',
           mbCriticalError, MB_OK);
    Result := False;
    Exit;
  end;
end;

// Antes de copiar os arquivos: garante que o .NET Framework 4.8 existe.
// No Windows 10/11 ele já vem de fábrica, então isto só age em máquinas antigas
// (Windows 7 SP1 / 8.1), baixando e instalando em silêncio. Retornar texto aqui
// aborta a instalação mostrando esse texto ao usuário.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  CodigoSaida: Integer;
  Instalador: String;
begin
  Result := '';

  if NetFramework48Instalado() then
    Exit;

  WizardForm.PreparingLabel.Caption :=
    'Instalando o componente .NET Framework 4.8 da Microsoft. Isso leva alguns minutos ' +
    'e acontece apenas nesta primeira vez.';

#ifdef OFFLINE
  // Versão Completo: o componente vem dentro do instalador, não depende de internet.
  try
    ExtractTemporaryFile('{#ArquivoNetFx}');
  except
    Result := 'Não foi possível preparar o componente .NET Framework 4.8 que vem junto com este instalador.';
    Exit;
  end;
  Instalador := ExpandConstant('{tmp}\{#ArquivoNetFx}');
#else
  // Versão pequena: baixa da Microsoft. No Windows 7/8.1 isso costuma FALHAR porque o
  // WinHTTP dessas versões usa TLS 1.0 e o site da Microsoft exige TLS 1.2 — por isso a
  // mensagem abaixo aponta para o instalador Completo em vez de só pedir para tentar de novo.
  try
    DownloadTemporaryFile(URL_NETFX48, 'ndp48-instalador.exe', '', nil);
  except
    Result := 'Este computador ainda não tem o componente ".NET Framework 4.8" da Microsoft, ' +
              'e não foi possível baixá-lo automaticamente.' + #13#10#13#10 +
              'Isso é normal no Windows 7 e 8.1.' + #13#10#13#10 +
              'Use o instalador "Setup_PadariaVenancio_Completo.exe", que já traz esse ' +
              'componente embutido e não precisa de download.';
    Exit;
  end;
  Instalador := ExpandConstant('{tmp}\ndp48-instalador.exe');
#endif

  if not Exec(Instalador, '/q /norestart', '', SW_SHOW, ewWaitUntilTerminated, CodigoSaida) then
  begin
    Result := 'Não foi possível executar o instalador do .NET Framework 4.8 da Microsoft.';
    Exit;
  end;

  // 0 = sucesso; 3010 e 1641 = sucesso mas pede reinício; 1638 = já há versão igual/maior.
  if (CodigoSaida = 3010) or (CodigoSaida = 1641) then
    NeedsRestart := True
  else if (CodigoSaida <> 0) and (CodigoSaida <> 1638) then
  begin
    Result := 'A instalação do .NET Framework 4.8 da Microsoft falhou (código ' +
              IntToStr(CodigoSaida) + ').' + #13#10#13#10 +
              'Instale manualmente o ".NET Framework 4.8" pelo site da Microsoft e rode este instalador de novo.';
    Exit;
  end;
end;
