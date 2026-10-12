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
node tests/testes_painel.js
node tests/testes_contrato_sql.js
```

`py tests/testes_api.py` verifica a API real e o instalador publicado. Os cinco testes de leitura de catálogo continuam falhando enquanto a remoção das políticas `anon_read` aguarda a confirmação de atualização dos caixas, conforme `AGENTS.md`.

O patch de banco desta revisão está em `docs/correcoes-auditoria.sql`. Não execute `docs/supabase_rpcs.sql` inteiro: ele também contém uma mudança de protocolo de estoque que ainda não deve ser ativada.

As novas etiquetas de peso usam Code128 e guardam o peso original e o preço total. Atualize os caixas que lerão essas etiquetas e confira o leitor e a impressora antes de usá-las na loja. Etiquetas antigas de peso (EAN com prefixo 21) precisam ser pesadas e reemitidas, pois não guardam o peso original; códigos comuns de produtos continuam válidos.
