; Script de Instalação do Inno Setup para o PDV Padaria Venâncio
; Desenvolvido para empacotar a aplicação WPF compilada (.NET 8.0)

[Setup]
AppId={{C6F2A3F4-5987-45CC-AB1B-7AA8D4D4A994}
AppName=PDV - Padaria Venâncio
AppVersion=1.0.1
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
; Opção para iniciar o PDV automaticamente após a conclusão da instalação
Filename: "{app}\PdvPadaria.exe"; Description: "{cm:LaunchProgram,PDV - Padaria Venâncio}"; Flags: nowait postinstall skipifsilent
