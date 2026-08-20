# PDV Padaria — WPF Desktop App

This repository is a **WPF desktop application** (C#, `net8.0-windows`) living in [`PdvPadaria/`](PdvPadaria/). It is the offline-first point-of-sale (caixa) for a bakery. The previous Next.js web app was retired on 2026-06-23 (recoverable in git history before that commit).

## Architecture

- **App:** WPF, code-behind style (no MVVM framework). Entry: `App.xaml.cs` → `Views/LoginWindow` → `MainWindow`.
- **Local DB:** SQLite via `sqlite-net-pcl`, stored at `%AppData%/pdv-padaria/dev.db`. Tables mirror the cloud (`Models/*.cs`).
- **Cloud:** Supabase Postgres, reached **directly** over its REST API (`/rest/v1/...`) — see `Services/SyncService.cs` and `Views/LoginWindow.xaml.cs`. There is no intermediate backend server.
- **Payments:** none integrated. Sales record a `paymentMethod` (PIX/DINHEIRO/CARTAO_*) but no gateway is called — there is no `InterPixService.cs`, and the old InfinitePay keys were dropped from config on 2026-08-19 because nothing read them.
- **Auth:** `Services/PasswordHasher.cs` (BCrypt; also accepts legacy plaintext for migration). Users are managed directly in the Supabase dashboard.
- **DB schema reference:** [`prisma/schema.prisma`](prisma/schema.prisma) documents the Supabase table structure. It is kept as reference only — there is no Node/Prisma tooling in this repo anymore.

## Conventions

- **Design tokens:** colors/typography live in `PdvPadaria/App.xaml` (XAML `{StaticResource ...}`) and `Services/AppColors.cs` (code-behind). Do **not** hardcode hex in XAML or `new SolidColorBrush(...)` in C#. Dark theme, amber accent `#F59E0B`.
- **Money:** stored and computed in **centavos** (int). Convert only for display.
- **Config:** `PdvPadaria/.env` (gitignored) supplies six keys — `SUPABASE_URL`, `SUPABASE_ANON_KEY`, `TENANT_ID`, `STORE_SYNC_TOKEN`, `TERMINAL_NAME`, and an optional `STORE_ID`. Read via `Services/EnvService.cs`, which treats a **blank value as absent** so the caller's fallback applies (`.env.exemplo` ships the keys empty).
  **Which store a machine is** comes from `STORE_SYNC_TOKEN` alone, resolved through `Services/StoreIdentityService.cs` (RPC `loja_do_token`, cached to `%AppData%/pdv-padaria/loja-identidade.txt` for offline boots). `STORE_ID` is only a first-boot-offline fallback. Until 2026-08-20 the two were independent — the token decided where writes landed, `STORE_ID` decided what the caixa read — and a machine with them set to different stores sold as one store while showing another's stock, silently. Don't reintroduce a read path that trusts `STORE_ID` over the token.
  **Never package `.env` in the installer.** `STORE_SYNC_TOKEN` is the write credential for the sync RPCs; the installer is published in a public repo, and shipping it both leaked the token and would stamp every store with the builder's identity. `setup.iss` excludes it and ships `.env.exemplo` with `onlyifdoesntexist`.
- **Sync RPCs refuse with HTTP 200.** `push_vendas` / `push_estoque` return `{"error": "..."}` as a normal result, so the status code is 200 either way. Always read the body (`SyncService.ErroDaResposta`) — trusting the status made the PDV mark unsent sales as synced and drop them.

## Build

`dotnet` is at `C:\Program Files\dotnet\dotnet.exe` (not on PATH in bash).

```
dotnet build PdvPadaria/PdvPadaria.csproj -c Debug
```

The running app locks `bin/...exe`; to compile-check while it runs, build to a temp dir: `dotnet build PdvPadaria/PdvPadaria.csproj --output "$env:TEMP\pdvbuild"`.
