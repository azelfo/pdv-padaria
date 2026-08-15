; Script de Instalação do Inno Setup para o PDV Padaria Venâncio
; Desenvolvido para empacotar a aplicação WPF compilada (.NET 8.0)

[Setup]
AppId={{C6F2A3F4-5987-45CC-AB1B-7AA8D4D4A994}
AppName=PDV - Padaria Venâncio
AppVersion=1.0.12
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
OutputBaseFilename=Setup_PadariaVenancio
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
Source: "d:\PDV\PdvPadaria\bin\Release\net8.0-windows\publish\PdvPadaria.exe"; DestDir: "{app}"; Flags: ignoreversion
; Todas as dependências geradas pelo Publish (DLLs, configurações, .env)
Source: "d:\PDV\PdvPadaria\bin\Release\net8.0-windows\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Nota: Ajuste os caminhos acima se utilizar a publicação com RID específico (ex: \publish\win-x64\)

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
  // Link permanente da Microsoft para o .NET 8 Desktop Runtime x64 (redireciona
  // sempre para o build mais recente da linha 8.0).
  URL_DOTNET8 = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

function ShouldRelaunchSilently(): Boolean;
begin
  Result := WizardSilent();
end;

// Pasta onde o .NET registra os runtimes de Desktop (WPF/WinForms) instalados.
// A detecção é feita por PASTA, e não pelo registro: a chave de registro do .NET
// muda de lugar conforme a versão do instalador, mas esta pasta é estável.
function PastaRuntimesDesktop(): String;
begin
  if IsWin64 then
    Result := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')
  else
    Result := ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.WindowsDesktop.App');
end;

// True se existir qualquer runtime da linha 8.x instalado (ex.: 8.0.30).
function DotNet8DesktopInstalado(): Boolean;
var
  Base: String;
  FindRec: TFindRec;
begin
  Result := False;
  Base := PastaRuntimesDesktop();
  if not DirExists(Base) then
    Exit;

  if FindFirst(Base + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if Copy(FindRec.Name, 1, 2) = '8.' then
          begin
            Result := True;
            Exit;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// O .NET 8 (e portanto este PDV) não roda em Windows 7, 8 nem 8.1 — a própria
// Microsoft não dá suporte. Barra a instalação com uma mensagem clara em vez de
// deixar o app instalar e falhar na hora de abrir.
function InitializeSetup(): Boolean;
var
  Versao: TWindowsVersion;
begin
  Result := True;
  GetWindowsVersionEx(Versao);
  if Versao.Major < 10 then
  begin
    MsgBox('Este sistema precisa do Windows 10 ou Windows 11.' + #13#10#13#10 +
           'A tecnologia usada pelo PDV (.NET 8) não funciona no Windows 7, 8 ou 8.1 — ' +
           'a Microsoft encerrou o suporte a essas versões.' + #13#10#13#10 +
           'Atualize o computador para o Windows 10 ou 11 para usar o PDV.',
           mbCriticalError, MB_OK);
    Result := False;
  end;
end;

// Antes de copiar os arquivos: garante que o .NET 8 Desktop Runtime existe.
// Se faltar, baixa da Microsoft e instala em silêncio. Roda tanto na instalação
// manual quanto na atualização automática. Retornar texto aqui aborta a instalação
// mostrando esse texto ao usuário.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  CodigoSaida: Integer;
  Instalador: String;
begin
  Result := '';

  if DotNet8DesktopInstalado() then
    Exit;

  // Baixa o runtime (arquivo vai para a pasta temporária do Setup).
  try
    DownloadTemporaryFile(URL_DOTNET8, 'dotnet8-desktop-runtime.exe', '', nil);
  except
    Result := 'O PDV precisa do componente .NET 8 da Microsoft, que ainda não está instalado ' +
              'neste computador, e não foi possível baixá-lo agora.' + #13#10#13#10 +
              'Verifique a conexão com a internet e tente novamente.';
    Exit;
  end;

  Instalador := ExpandConstant('{tmp}\dotnet8-desktop-runtime.exe');

  if not Exec(Instalador, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, CodigoSaida) then
  begin
    Result := 'Não foi possível executar o instalador do componente .NET 8 da Microsoft.';
    Exit;
  end;

  // 0 = sucesso; 3010 = sucesso mas pede reinício; 1638 = já existe versão igual/maior.
  if CodigoSaida = 3010 then
    NeedsRestart := True
  else if (CodigoSaida <> 0) and (CodigoSaida <> 1638) then
  begin
    Result := 'A instalação do componente .NET 8 da Microsoft falhou (código ' +
              IntToStr(CodigoSaida) + ').' + #13#10#13#10 +
              'Instale manualmente o ".NET Desktop Runtime 8" pelo site da Microsoft e rode este instalador de novo.';
    Exit;
  end;
end;
