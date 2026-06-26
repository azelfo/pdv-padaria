# Pendências do Code Review (pré-Release)

Última atualização: 2026-06-26. Branch `main`.

## Estado atual
- Todas as correções do review **já estão commitadas** em `main` (último: `9654a75`).
- **Ainda NÃO foi gerado o instalador** com essas mudanças. Última versão empacotada: **1.0.4**.
- As mudanças de banco (RPCs) **já estão ativas na nuvem** Supabase.

---

## 1. PENDÊNCIA IMEDIATA — gerar o instalador de produção

As correções de C# (commit `9654a75`) só chegam às máquinas via novo instalador.

Passos (rodar na raiz `D:\PDV`):

```powershell
# 1. bump da versão em PdvPadaria\setup.iss:  AppVersion=1.0.4  ->  1.0.5
# 2. publicar Release
& "C:\Program Files\dotnet\dotnet.exe" publish PdvPadaria\PdvPadaria.csproj -c Release
# 3. compilar instalador
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "D:\PDV\PdvPadaria\setup.iss"
# 4. resultado: D:\PDV\PdvPadaria\Output\Setup_PadariaVenancio.exe
# 5. commitar o bump + git push origin main
```

## TESTE OBRIGATÓRIO antes de liberar
Numa máquina real: finalizar uma venda e **deixar o app aberto > 60s** (1 ciclo de sync).
Confirmar que NÃO aparece erro "database is locked". É o cenário que a correção do
SharedCache (commit `9654a75`) blinda.

---

## 2. Achados ADIADOS (reportados, não aplicados) — onde analisar e o que mudar

### A) Unificar parser de número  (severidade: BAIXA — só com teste interativo)
Há duas funções de parse divergentes:
- `PdvPadaria\MainWindow.xaml.cs:497` — `TryParseDouble` (Invariant + troca `,`→`.`)
- `PdvPadaria\Views\PaymentWindow.xaml.cs:256` — `TryParseDouble` (Invariant, depois pt-BR)

**Mudar:** criar `PdvPadaria\Services\NumberParser.cs` com UMA função robusta a pt-BR/en-US
(remover separador de milhar; último `,`/`.` = decimal) e reusar nas duas telas.
**Por que não fiz:** a entrada realista de padaria (R$ < 100, sem separador de milhar) já
funciona hoje. Reescrever parsing de dinheiro sem poder testar interativamente tem risco
maior que o ganho. Validar digitando valores reais antes de mexer.

### B) Movimentos de estoque que nunca sobem (auditoria)  (BAIXA)
- `PdvPadaria\Services\SyncService.cs:101-119` — o push só marca `IsSynced=true` em
  movimentos ligados a uma venda (`SaleId` preenchido).
- Movimentos de **ajuste manual** (`MainWindow.xaml.cs` ~1724) e de reposição/entrada
  nascem com `SaleId` nulo e ficam `IsSynced=false` para sempre.

**Mudar (se quiser histórico de ajustes na nuvem):** incluir esses movimentos no payload de
`PushSalesAsync`/`push_vendas`, ou marcá-los como sincronizados após o snapshot de estoque.
**Hoje não quebra nada** — o painel usa a "foto" de saldo (StoreProduct), não o histórico.

### C) Seletores de loja do dono presos em offline  (BAIXA)
Se o app abrir SEM rede, `FetchLojasAsync()` retorna lista vazia, o seletor marca
`_xxxSelectorReady = true` e NUNCA repovoa até relogar.
Setups afetados em `PdvPadaria\MainWindow.xaml.cs`:
- `SetupStockStoreSelector` (~1788)
- `SetupHistoryStoreSelector` (~2159)
- `SetupAlertStoreSelector` (~2066)
- `SetupDashStoreSelector` (~2750)

**Mudar:** só fazer `_xxxSelectorReady = true` se `lojas.Count > 0`; caso contrário, deixar
para repovoar no próximo acesso à aba.

---

## 3. Área NÃO revisada (merece um segundo olhar)
- `PdvPadaria\MainWindow.xaml` — os bindings dos `TextBox` de **hora** do histórico/dashboard
  (`HistoryStartTimeTextBox`, etc.). Verificar se o valor "commita" antes do clique em
  Filtrar (pode faltar `UpdateSourceTrigger=PropertyChanged`); se houver dúvida, ler o
  `.Text` direto no handler do botão em vez de confiar no binding.

---

## 4. Dívida técnica pós-lançamento (NÃO bloqueia o Release)
1. `MainWindow.xaml.cs` = 3.789 linhas (God Object) → quebrar em `partial class` por aba
   (`MainWindow.Pdv.cs`, `MainWindow.Stock.cs`, `MainWindow.Sync.cs`, etc.).
2. PIX/Cartão = delay artificial de 20s em `PaymentWindow.xaml.cs:80` (`StartPaymentDelay`) —
   sem integração real; o operador confirma na maquininha física. Decidir se mantém o delay.

---

## Resumo do que JÁ foi corrigido (não refazer)
- Email do seed `.com.br`→`.com` (`DatabaseService.cs`)
- `ajustar_estoque` grava `OwnerStockAdjustment` (RPC na nuvem)
- 5 wrappers órfãos + using removidos (`DatabaseService.cs`)
- Leak do `CancellationTokenSource` (`PaymentWindow.xaml.cs`)
- `extensions.crypt` em todas as RPCs de auth
- `SharedCache` removido das 2 conexões (`DatabaseService.cs`)
- `IncreaseQuantity` soma todas as linhas do produto (`MainWindow.xaml.cs:878`)
