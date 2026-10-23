# PDV Padaria

Sistema de **PDV (frente de caixa) e gestão** para padaria — aplicativo desktop **WPF** (C#, .NET Framework 4.8, Windows), offline-first.

## Visão geral

- **Caixa local:** app WPF em [`PdvPadaria/`](PdvPadaria/). Funciona offline; banco local SQLite em `%AppData%/pdv-padaria/dev.db`.
- **Nuvem:** banco central no **Supabase** (Postgres), acessado direto pela API REST. O app sincroniza vendas (push) e cadastros/estoque (pull).
- **Pagamento:** registro manual de dinheiro, PIX e cartão. O operador confirma o recebimento no banco ou na maquininha; o PDV não gera cobrança, QR Code, nem consulta pagamentos.
- **Usuários:** cadastrados/editados no painel do **Supabase**. O proprietário acompanha a rede pelo painel publicado em `docs/`.

> O app web Next.js original foi aposentado em 2026-06-23. O código segue recuperável no histórico do git.

## Build

`dotnet` em `C:\Program Files\dotnet\dotnet.exe`.

```
dotnet build PdvPadaria/PdvPadaria.csproj -c Debug
```

Executável gerado em `PdvPadaria/bin/Debug/net48/PdvPadaria.exe` (x86).

### Gerar os dois instaladores

Na raiz do repositório, execute o script de empacotamento em PowerShell:

```powershell
& .\PdvPadaria\gerar-instaladores.ps1
```

O script prepara a publicação e gera os dois pacotes em `PdvPadaria/Output/`:

- `Setup_PadariaVenancio.exe`: instalador padrão, usado pelo atualizador. Inclui a atualização gráfica KB4019990 para 32 e 64 bits; baixa SSU/SHA-2 e .NET Framework 4.8 da Microsoft quando necessários.
- `Setup_PadariaVenancio_Completo.exe`: inclui os sete arquivos oficiais (KB4490628, KB4474419 v3 e KB4019990 em ambas as arquiteturas, mais .NET Framework 4.8), sem downloads durante a instalação. É o pacote recomendado para o Windows 7 SP1 com falha ao abrir o PDV.

O instalador escolhe os componentes pela arquitetura do Windows, avisa quando há necessidade de reinício e prepara a retomada após o próximo acesso com uma conta de administrador. Se a retomada não ocorrer, abra o mesmo instalador novamente. Windows 7 sem SP1 e Windows 8.0 não são aceitos. A versão do aplicativo, `setup.iss` e `docs/version.json` devem coincidir; nunca empacote o `.env` da máquina de desenvolvimento.

Os CABs são aplicados pelo DISM local, sem fazer busca no Windows Update. O script verifica origem, assinatura Microsoft, tamanho e SHA-256 dos redistribuíveis antes de compilar; o instalador confere o hash novamente. O pacote prepara apenas os requisitos do PDV, não todas as atualizações do Windows. Não abra o PDV antes de concluir os reinícios solicitados. O diagnóstico do aplicativo fica em `%AppData%\pdv-padaria\erro.log` (com alternativa na pasta temporária se não houver acesso), e o instalador gera `Setup Log ...txt` na pasta temporária.

### Ordem de publicação

1. Gere e confira os dois instaladores, suas versões e hashes SHA-256.
2. **Envie primeiro o instalador completo para a GitHub Release `v1.1.18`**, como o arquivo `Setup_PadariaVenancio_Completo.exe`. Confirme o download público e o hash em `https://github.com/azelfo/pdv-padaria/releases/download/v1.1.18/Setup_PadariaVenancio_Completo.exe`.
3. Só depois publique em `main` o instalador padrão rastreado, a página `docs/baixar.html` e o manifesto `docs/version.json`. O campo `url` mantém o pacote padrão para o atualizador; `fullUrl` aponta para o pacote completo da Release. A página lê a versão e os endereços desse manifesto.
4. Aguarde a publicação do GitHub Pages e confira os dois botões da página pública. Preparar os arquivos localmente não os publica.

Não publique o novo manifesto enquanto o completo ainda não estiver acessível: ele é o caminho de instalação para as máquinas que não conseguem abrir o PDV.

## Configuração

O instalador cria o `.env` a partir de `PdvPadaria/.env.exemplo`, sem credenciais de loja. Endereço e chave pública da nuvem já têm valores padrão no aplicativo; o primeiro login online registra a máquina na loja do usuário. Não é necessário configurar uma integração de pagamentos.

As únicas chaves reconhecidas são:

```
SUPABASE_URL=
SUPABASE_ANON_KEY=
TENANT_ID=
STORE_SYNC_TOKEN=
STORE_ID=
TERMINAL_NAME=Caixa 01
```

Deixe os campos opcionais vazios para usar os padrões. `STORE_SYNC_TOKEN` e `STORE_ID` existem por compatibilidade com instalações antigas; a identidade confirmada pelo token tem prioridade. Nunca inclua um token de loja no instalador.

## Estrutura do banco

`prisma/schema.prisma` documenta as tabelas do Supabase (referência; sem tooling Node no repo).

## Verificação das correções

Testes locais, sem usar o banco ou credenciais das lojas:

```powershell
dotnet run --project tests/checagem-p0/checagem-p0.csproj
dotnet run --project tests/regressao-sync/regressao-sync.csproj
dotnet run --project tests/checagem-pagamento-etiquetas/checagem-pagamento-etiquetas.csproj
dotnet run --project tests/checagem-inicializacao/checagem-inicializacao.csproj
node tests/testes_painel.js
node tests/testes_contrato_sql.js
node tests/testes_download.js
& .\tests\instalador\executar.ps1
```

`py tests/testes_api.py` verifica a API real e o instalador publicado. Os cinco testes de leitura de catálogo continuam falhando enquanto a remoção das políticas `anon_read` aguarda a confirmação de atualização dos caixas, conforme `AGENTS.md`.

O patch de banco desta revisão está em `docs/correcoes-auditoria.sql`. Não execute `docs/supabase_rpcs.sql` inteiro: ele também contém uma mudança de protocolo de estoque que ainda não deve ser ativada.

As novas etiquetas de peso usam Code128 e guardam o peso original e o preço total. Atualize os caixas que lerão essas etiquetas e confira o leitor e a impressora antes de usá-las na loja. Etiquetas antigas de peso (EAN com prefixo 21) precisam ser pesadas e reemitidas, pois não guardam o peso original; códigos comuns de produtos continuam válidos.
