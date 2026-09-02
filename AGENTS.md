# PDV Padaria — WPF Desktop App

This repository is a **WPF desktop application** (C#, `.NET Framework 4.8`) living in [`PdvPadaria/`](PdvPadaria/). It is the offline-first point-of-sale (caixa) for a bakery. The previous Next.js web app was retired on 2026-06-23 (recoverable in git history before that commit).

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
  **Which store a machine is** is decided by its sync token alone, through `Services/StoreIdentityService.cs`. Since 2026-08-21 the machine gets that token **at login**: the caixa user carries `storeId`, so RPC `registrar_caixa` mints a per-machine token stored at `%AppData%/pdv-padaria/caixa-token.dat`. `STORE_SYNC_TOKEN` in `.env` is the legacy path, still accepted; `STORE_ID` is only a first-boot-offline fallback. Both resolve through `loja_do_token`, cached to `loja-identidade.txt`.
  **Switching a machine to another store never deletes local history.** The credential changes only after an online login and the switch is refused while there is any unsent sale or movement belonging to another store. The stock-store marker changes only after the target store's cloud stock has been downloaded successfully; synchronized history remains in SQLite and every local history query must filter by `StoreId`.
  Two failures drove this. Until 2026-08-20 token and `STORE_ID` were independent, so a machine with them set to different stores sold as one store while showing another's stock, silently. And rotating a store token meant someone had to visit each PC — when that didn't happen, two stores went four days without syncing. **Don't reintroduce a read path that trusts `STORE_ID` over the token, and don't add a step that requires typing a secret into a machine.**
  **`SUPABASE_URL` and `SUPABASE_ANON_KEY` have built-in defaults in `EnvService._padrao`.** The installer writes `.env` with `onlyifdoesntexist` (it must, or every update would erase the store's config), so a machine installed with a blank `.env` stayed blank forever — "Configuracao da nuvem ausente no .env" at login and at sync, and no update could fix it. Neither value is a secret: the anon key is publishable by design and `docs/index.html` already exposes the same one. What protects the data is the row policy, not the key's secrecy — which is why the pending `anon_read USING (true)` cutover matters. A value written in `.env` still wins, so **never copy these two into `.env.exemplo`**: a forgotten copy there would silently pin machines to a stale server.
  **Never package `STORE_SYNC_TOKEN` in the installer.** `STORE_SYNC_TOKEN` is the write credential for the sync RPCs; the installer is published in a public repo, and shipping it both leaked the token and would stamp every store with the builder's identity. `setup.iss` excludes it and ships `.env.exemplo` with `onlyifdoesntexist`.
- **Reads are scoped by the token, not by the query.** `SyncService.ObterCadastrosAsync` calls RPC `pull_cadastros(p_token)`, which derives the store (and tenant) server-side — the same rule the writes already used. It replaced five direct table reads whose tenant filter was a URL parameter the client chose to send, over tables whose policy was `anon_read USING (true)`: any holder of the public key could read every tenant. **Don't add a read path that goes straight to a table.**
  Cutover is two-phase and **phase 2 is still pending**: the `anon_read` policies on `Product`, `Category`, `StoreProduct`, `BreadConfig` and `OwnerStockAdjustment` stay until every caixa runs 1.1.9+, because older clients still read the tables. Confirm via API logs (no more `GET /rest/v1/Product`, only `POST /rest/v1/rpc/pull_cadastros`), then drop them. `tests/testes_api.py` fails on exactly those five until it is done.
- **Sync RPCs refuse with HTTP 200.** `push_vendas` / `push_estoque` return `{"error": "..."}` as a normal result, so the status code is 200 either way. Always require a valid JSON success body and read `error` (`SyncService.ErroDaResposta`) — trusting the status made the PDV mark unsent sales as synced and drop them. Version 1.1.7 is a compatibility release and still uses the legacy absolute stock snapshot; the movement-ledger cutover must not be activated until every old client has been drained.

## Build

`dotnet` is at `C:\Program Files\dotnet\dotnet.exe` (not on PATH in bash).

```
dotnet build PdvPadaria/PdvPadaria.csproj -c Debug
```

The running app locks `bin/...exe`; to compile-check while it runs, build to a temp dir: `dotnet build PdvPadaria/PdvPadaria.csproj --output "$env:TEMP\pdvbuild"`.
