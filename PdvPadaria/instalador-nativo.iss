var ErroGrafico: Cardinal;

function LoadLibraryW(Nome: String): THandle;
  external 'LoadLibraryW@kernel32.dll stdcall';
function FreeLibrary(Modulo: THandle): Boolean;
  external 'FreeLibrary@kernel32.dll stdcall';
function GetProcAddress(Modulo: THandle; Nome: AnsiString): Cardinal;
  external 'GetProcAddress@kernel32.dll stdcall';
function CriarChaveVolatil(Raiz: THandle; Nome: String; Reservado: Cardinal;
  Classe: String; Opcoes, Acesso: Cardinal; Seguranca: Cardinal;
  var Chave: THandle; var Disposicao: Cardinal): LongInt;
  external 'RegCreateKeyExW@advapi32.dll stdcall';
function FecharChave(Chave: THandle): LongInt;
  external 'RegCloseKey@advapi32.dll stdcall';

function ComponenteGraficoFunciona: Boolean;
var Modulo: THandle; Caminho: String;
begin
  // PDV e instalador são x86: testar a DLL de 32 bits, mesmo no Windows x64.
  if IsWin64 then Caminho := ExpandConstant('{syswow64}\D3DCompiler_47.dll')
  else Caminho := ExpandConstant('{sys}\D3DCompiler_47.dll');
  ErroGrafico := 0;
  Modulo := LoadLibraryW(Caminho);
  if Modulo = 0 then begin
    ErroGrafico := DLLGetLastError;
    Result := False;
    Exit;
  end;
  Result := GetProcAddress(Modulo, 'D3DCompile') <> 0;
  if not Result then ErroGrafico := 127;
  FreeLibrary(Modulo);
end;
