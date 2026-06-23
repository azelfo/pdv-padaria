# PDV Padaria

Sistema de **PDV (frente de caixa) e gestão** para padaria — aplicativo desktop **WPF** (C#, .NET 8, Windows), offline-first.

## Visão geral

- **Caixa local:** app WPF em [`PdvPadaria/`](PdvPadaria/). Funciona offline; banco local SQLite em `%AppData%/pdv-padaria/dev.db`.
- **Nuvem:** banco central no **Supabase** (Postgres), acessado direto pela API REST. O app sincroniza vendas (push) e cadastros/estoque (pull).
- **Pagamento:** PIX via **Banco Inter** (certificado mTLS, polling de status).
- **Usuários:** cadastrados/editados direto no painel do **Supabase** (não há admin web).

> O app web Next.js original foi aposentado em 2026-06-23. O código segue recuperável no histórico do git.

## Build

`dotnet` em `C:\Program Files\dotnet\dotnet.exe`.

```
dotnet build PdvPadaria/PdvPadaria.csproj -c Debug
```

Executável gerado em `PdvPadaria/bin/Debug/net8.0-windows/PdvPadaria.exe`.

## Configuração

Crie `PdvPadaria/.env` (não versionado) com:

```
SUPABASE_URL=...
SUPABASE_ANON_KEY=...
TENANT_ID=...
STORE_ID=...
TERMINAL_NAME=Caixa 01
INTER_CLIENT_ID=...
INTER_CLIENT_SECRET=...
INTER_CHAVE_PIX=...
INTER_CERT_PATH=...
INTER_CERT_PASSWORD=...
INTER_ENV=producao
```

## Estrutura do banco

`prisma/schema.prisma` documenta as tabelas do Supabase (referência; sem tooling Node no repo).
