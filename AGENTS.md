# PDV Padaria — WPF Desktop App

This repository is a **WPF desktop application** (C#, `net8.0-windows`) living in [`PdvPadaria/`](PdvPadaria/). It is the offline-first point-of-sale (caixa) for a bakery. The previous Next.js web app was retired on 2026-06-23 (recoverable in git history before that commit).

## Architecture

- **App:** WPF, code-behind style (no MVVM framework). Entry: `App.xaml.cs` → `Views/LoginWindow` → `MainWindow`.
- **Local DB:** SQLite via `sqlite-net-pcl`, stored at `%AppData%/pdv-padaria/dev.db`. Tables mirror the cloud (`Models/*.cs`).
- **Cloud:** Supabase Postgres, reached **directly** over its REST API (`/rest/v1/...`) — see `Services/SyncService.cs` and `Views/LoginWindow.xaml.cs`. There is no intermediate backend server.
- **Payments:** Banco Inter PIX (mTLS + polling) via `Services/InterPixService.cs`.
- **Auth:** `Services/PasswordHasher.cs` (BCrypt; also accepts legacy plaintext for migration). Users are managed directly in the Supabase dashboard.
- **DB schema reference:** [`prisma/schema.prisma`](prisma/schema.prisma) documents the Supabase table structure. It is kept as reference only — there is no Node/Prisma tooling in this repo anymore.

## Conventions

- **Design tokens:** colors/typography live in `PdvPadaria/App.xaml` (XAML `{StaticResource ...}`) and `Services/AppColors.cs` (code-behind). Do **not** hardcode hex in XAML or `new SolidColorBrush(...)` in C#. Dark theme, amber accent `#F59E0B`.
- **Money:** stored and computed in **centavos** (int). Convert only for display.
- **Config:** `PdvPadaria/.env` (gitignored) supplies `SUPABASE_*`, `TENANT_ID`, `STORE_ID`, `INTER_*`. Read via `Services/EnvService.cs`.

## Build

`dotnet` is at `C:\Program Files\dotnet\dotnet.exe` (not on PATH in bash).

```
dotnet build PdvPadaria/PdvPadaria.csproj -c Debug
```

The running app locks `bin/...exe`; to compile-check while it runs, build to a temp dir: `dotnet build PdvPadaria/PdvPadaria.csproj --output "$env:TEMP\pdvbuild"`.
