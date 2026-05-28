"use client";

import { useState, useMemo } from "react";
import { useRouter } from "next/navigation";
import { 
  ArrowLeft, 
  Calendar, 
  TrendingUp, 
  Coins, 
  Store as StoreIcon, 
  User, 
  BarChart3, 
  CreditCard, 
  QrCode, 
  DollarSign, 
  Package, 
  Layers,
  ArrowRight,
  ChevronRight,
  Percent,
  RefreshCw
} from "lucide-react";

interface SaleItemData {
  id: string;
  productId: string;
  productName: string;
  categoryName: string;
  quantity: number;
  priceUnit: number;
  subtotal: number;
  type: string;
  details: string | null;
  unitMeasure: string;
}

interface SaleData {
  id: string;
  storeId: string;
  storeName: string;
  userId: string;
  userName: string;
  saleDate: string;
  subtotal: number;
  discount: number;
  total: number;
  paymentMethod: string;
  paymentStatus: string;
  receivedAmount: number | null;
  changeAmount: number | null;
  notes: string | null;
  items: SaleItemData[];
}

interface StoreData {
  id: string;
  name: string;
}

interface DashboardClientProps {
  session: any;
  sales: SaleData[];
  stores: StoreData[];
}

export default function DashboardClient({ session, sales, stores }: DashboardClientProps) {
  const router = useRouter();

  // Função auxiliar para pegar data local no formato YYYY-MM-DD
  const getLocalDateString = (date: Date = new Date()) => {
    const offset = date.getTimezoneOffset();
    const localDate = new Date(date.getTime() - offset * 60 * 1000);
    return localDate.toISOString().split("T")[0];
  };

  const todayStr = getLocalDateString();

  // Estados de Filtro
  const [startDate, setStartDate] = useState(todayStr);
  const [endDate, setEndDate] = useState(todayStr);
  const [selectedStoreId, setSelectedStoreId] = useState("TODOS");
  const [hoveredSaleId, setHoveredSaleId] = useState<string | null>(null);

  // Redirecionamentos de navegação
  const handleBackToPdv = () => {
    router.push("/pdv");
  };

  const handleGoToEstoque = () => {
    router.push("/pdv/estoque");
  };

  // Re-validar ou limpar filtros
  const handleResetFilters = () => {
    setStartDate(todayStr);
    setEndDate(todayStr);
    setSelectedStoreId("TODOS");
  };

  // 1. Filtragem das vendas em memória
  const filteredSales = useMemo(() => {
    return sales.filter((sale) => {
      // Extrai YYYY-MM-DD da data ISO UTC para comparação local direta de string
      const saleLocalDate = sale.saleDate.split("T")[0];
      const matchDate = saleLocalDate >= startDate && saleLocalDate <= endDate;
      const matchStore = selectedStoreId === "TODOS" || sale.storeId === selectedStoreId;
      return matchDate && matchStore;
    });
  }, [sales, startDate, endDate, selectedStoreId]);

  // 2. Métricas Financeiras Consolidadas
  const metrics = useMemo(() => {
    let totalRevenueCentavos = 0;
    let totalDiscountCentavos = 0;
    let totalSubtotalCentavos = 0;
    const count = filteredSales.length;

    filteredSales.forEach((sale) => {
      totalRevenueCentavos += sale.total;
      totalDiscountCentavos += sale.discount;
      totalSubtotalCentavos += sale.subtotal;
    });

    const totalRevenue = totalRevenueCentavos / 100;
    const totalDiscount = totalDiscountCentavos / 100;
    const totalSubtotal = totalSubtotalCentavos / 100;
    const averageTicket = count > 0 ? totalRevenue / count : 0;

    return {
      totalRevenue,
      totalDiscount,
      totalSubtotal,
      count,
      averageTicket,
    };
  }, [filteredSales]);

  // 3. Ranking dos 5 Produtos Mais Vendidos (Volume de Saídas)
  const topProducts = useMemo(() => {
    const stats: Record<string, { productName: string; quantity: number; revenue: number; unitMeasure: string; categoryName: string }> = {};

    filteredSales.forEach((sale) => {
      sale.items.forEach((item) => {
        const key = item.productId;
        if (!stats[key]) {
          stats[key] = {
            productName: item.productName,
            quantity: 0,
            revenue: 0,
            unitMeasure: item.unitMeasure,
            categoryName: item.categoryName,
          };
        }
        stats[key].quantity += item.quantity;
        stats[key].revenue += item.subtotal;
      });
    });

    return Object.values(stats)
      .sort((a, b) => b.quantity - a.quantity)
      .slice(0, 5);
  }, [filteredSales]);

  // Encontra a maior quantidade para calcular percentual da barra de progresso do produto
  const maxProductQty = useMemo(() => {
    if (topProducts.length === 0) return 1;
    return Math.max(...topProducts.map((p) => p.quantity));
  }, [topProducts]);

  // 4. Ranking de Categorias Mais Vendidas (Por Receita)
  const categoryStats = useMemo(() => {
    const stats: Record<string, { categoryName: string; quantity: number; revenue: number }> = {};

    filteredSales.forEach((sale) => {
      sale.items.forEach((item) => {
        const key = item.categoryName;
        if (!stats[key]) {
          stats[key] = {
            categoryName: key,
            quantity: 0,
            revenue: 0,
          };
        }
        stats[key].quantity += item.quantity;
        stats[key].revenue += item.subtotal;
      });
    });

    return Object.values(stats)
      .sort((a, b) => b.revenue - a.revenue);
  }, [filteredSales]);

  // Encontra a maior receita de categoria para percentual
  const maxCategoryRevenue = useMemo(() => {
    if (categoryStats.length === 0) return 1;
    return Math.max(...categoryStats.map((c) => c.revenue));
  }, [categoryStats]);

  // 5. Métodos de Pagamento e Participação
  const paymentStats = useMemo(() => {
    const stats = {
      DINHEIRO: { name: "Dinheiro", value: 0, icon: DollarSign, color: "text-emerald-400", bg: "bg-emerald-500/10" },
      PIX: { name: "Pix", value: 0, icon: QrCode, color: "text-cyan-400", bg: "bg-cyan-500/10" },
      CARTAO_DEBITO: { name: "C. Débito", value: 0, icon: CreditCard, color: "text-blue-400", bg: "bg-blue-500/10" },
      CARTAO_CREDITO: { name: "C. Crédito", value: 0, icon: CreditCard, color: "text-indigo-400", bg: "bg-indigo-500/10" },
    };

    filteredSales.forEach((sale) => {
      const method = sale.paymentMethod as keyof typeof stats;
      if (stats[method]) {
        stats[method].value += sale.total;
      }
    });

    const totalCentavos = filteredSales.reduce((acc, sale) => acc + sale.total, 0) || 1;

    return Object.values(stats).map((item) => ({
      ...item,
      amount: item.value / 100,
      percentage: (item.value / totalCentavos) * 100,
    }));
  }, [filteredSales]);

  return (
    <div className="min-h-screen flex flex-col bg-[#050507] text-slate-100 p-6 relative overflow-x-hidden">
      
      {/* Luzes decorativas de fundo premium */}
      <div className="absolute top-10 right-10 w-96 h-96 bg-amber-500/5 rounded-full blur-[120px] pointer-events-none"></div>
      <div className="absolute bottom-10 left-10 w-96 h-96 bg-orange-600/5 rounded-full blur-[120px] pointer-events-none"></div>

      <div className="w-full max-w-7xl mx-auto z-10 flex-1 flex flex-col gap-6">
        
        {/* CABEÇALHO SUPERIOR */}
        <header className="flex flex-col md:flex-row md:items-center md:justify-between gap-4 border-b border-white/5 pb-6">
          <div className="flex items-center gap-3">
            <button
              onClick={handleBackToPdv}
              className="w-10 h-10 rounded-xl bg-white/[0.02] border border-white/5 flex items-center justify-center text-slate-400 hover:text-amber-400 hover:bg-white/5 hover:border-amber-500/20 transition cursor-pointer"
              title="Voltar para o PDV"
            >
              <ArrowLeft className="w-5 h-5" />
            </button>
            <div>
              <span className="text-[10px] font-bold text-amber-500 uppercase tracking-wider block">
                Painel do Proprietário • Acesso Restrito
              </span>
              <h1 className="text-2xl font-extrabold tracking-tight text-slate-100 flex items-center gap-2">
                <BarChart3 className="w-6 h-6 text-amber-500" />
                Dashboard Financeiro & Vendas
              </h1>
            </div>
          </div>

          <div className="flex flex-wrap items-center gap-3">
            <button
              onClick={handleGoToEstoque}
              className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer"
            >
              <Package className="w-4 h-4" />
              Estoque
            </button>
            <button
              onClick={handleResetFilters}
              className="w-9 h-9 rounded-xl bg-white/[0.02] border border-white/5 flex items-center justify-center text-slate-400 hover:text-amber-400 hover:bg-white/5 transition"
              title="Limpar Filtros"
            >
              <RefreshCw className="w-4 h-4" />
            </button>
          </div>
        </header>

        {/* FILTROS DE PESQUISA (CALENDÁRIO E FILIAL) */}
        <section className="glass rounded-3xl p-5 grid grid-cols-1 sm:grid-cols-3 gap-4 items-end">
          
          {/* Seletor de Loja (Filial) */}
          <div className="flex flex-col gap-1.5">
            <label className="text-[11px] font-bold text-slate-400 uppercase tracking-wider flex items-center gap-1.5">
              <StoreIcon className="w-3.5 h-3.5 text-amber-500/70" />
              Filial / Unidade
            </label>
            <select
              value={selectedStoreId}
              onChange={(e) => setSelectedStoreId(e.target.value)}
              className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 cursor-pointer font-semibold outline-none focus:border-amber-500/30 transition"
            >
              <option value="TODOS" className="bg-[#0f0f13] text-slate-100 font-semibold">
                ✨ Todas as Filiais (Rede)
              </option>
              {stores.map((store) => (
                <option key={store.id} value={store.id} className="bg-[#0f0f13] text-slate-100">
                  🏪 {store.name}
                </option>
              ))}
            </select>
          </div>

          {/* Calendário: Data Início */}
          <div className="flex flex-col gap-1.5">
            <label className="text-[11px] font-bold text-slate-400 uppercase tracking-wider flex items-center gap-1.5">
              <Calendar className="w-3.5 h-3.5 text-amber-500/70" />
              Data de Início (De)
            </label>
            <input
              type="date"
              value={startDate}
              onChange={(e) => setStartDate(e.target.value)}
              className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 cursor-pointer outline-none focus:border-amber-500/30 transition"
            />
          </div>

          {/* Calendário: Data Fim */}
          <div className="flex flex-col gap-1.5">
            <label className="text-[11px] font-bold text-slate-400 uppercase tracking-wider flex items-center gap-1.5">
              <Calendar className="w-3.5 h-3.5 text-amber-500/70" />
              Data Limite (Até)
            </label>
            <input
              type="date"
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
              className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 cursor-pointer outline-none focus:border-amber-500/30 transition"
            />
          </div>
        </section>

        {/* CARDS METRICOS ESTATISTICOS */}
        <section className="grid grid-cols-1 sm:grid-cols-3 gap-6">
          
          {/* Card Faturamento */}
          <div className="glass rounded-3xl p-6 flex items-center gap-5 relative overflow-hidden group hover:border-amber-500/20 transition duration-300">
            <div className="absolute top-0 right-0 w-32 h-32 bg-amber-500/5 rounded-full blur-2xl pointer-events-none group-hover:bg-amber-500/10 transition duration-300"></div>
            <div className="w-12 h-12 rounded-2xl bg-amber-500/10 border border-amber-500/20 flex items-center justify-center text-amber-500 shrink-0">
              <DollarSign className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div className="min-w-0">
              <span className="text-[10px] text-slate-400 font-extrabold block uppercase tracking-wider">Faturamento Líquido</span>
              <span className="text-2xl font-black text-slate-100 block truncate mt-0.5">
                {metrics.totalRevenue.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
              </span>
              <span className="text-[10px] text-slate-500 block mt-1 font-semibold">
                Bruto: {metrics.totalSubtotal.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
              </span>
            </div>
          </div>

          {/* Card Volume de Vendas */}
          <div className="glass rounded-3xl p-6 flex items-center gap-5 relative overflow-hidden group hover:border-orange-500/20 transition duration-300">
            <div className="absolute top-0 right-0 w-32 h-32 bg-orange-500/5 rounded-full blur-2xl pointer-events-none group-hover:bg-orange-500/10 transition duration-300"></div>
            <div className="w-12 h-12 rounded-2xl bg-orange-500/10 border border-orange-500/20 flex items-center justify-center text-orange-400 shrink-0">
              <TrendingUp className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div className="min-w-0">
              <span className="text-[10px] text-slate-400 font-extrabold block uppercase tracking-wider">Total de Vendas</span>
              <span className="text-2xl font-black text-slate-100 block truncate mt-0.5">
                {metrics.count} <span className="text-xs font-bold text-slate-500">atendimentos</span>
              </span>
              <span className="text-[10px] text-slate-500 block mt-1 font-semibold">
                Descontos: {metrics.totalDiscount.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
              </span>
            </div>
          </div>

          {/* Card Ticket Médio */}
          <div className="glass rounded-3xl p-6 flex items-center gap-5 relative overflow-hidden group hover:border-cyan-500/20 transition duration-300">
            <div className="absolute top-0 right-0 w-32 h-32 bg-cyan-500/5 rounded-full blur-2xl pointer-events-none group-hover:bg-cyan-500/10 transition duration-300"></div>
            <div className="w-12 h-12 rounded-2xl bg-cyan-500/10 border border-cyan-500/20 flex items-center justify-center text-cyan-400 shrink-0">
              <Coins className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div className="min-w-0">
              <span className="text-[10px] text-slate-400 font-extrabold block uppercase tracking-wider">Ticket Médio</span>
              <span className="text-2xl font-black text-slate-100 block truncate mt-0.5">
                {metrics.averageTicket.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
              </span>
              <span className="text-[10px] text-slate-500 block mt-1 font-semibold">
                Média por ticket no período
              </span>
            </div>
          </div>
        </section>

        {/* ESTATÍSTICAS DETALHADAS: PRODUTOS MAIS VENDIDOS E FORMAS DE PAGAMENTO */}
        <section className="grid grid-cols-1 lg:grid-cols-12 gap-6">
          
          {/* Top 5 Produtos (Quais saíram mais) - Column span 7 */}
          <div className="glass rounded-3xl p-6 lg:col-span-7 flex flex-col">
            <div className="flex items-center justify-between mb-5">
              <div>
                <h2 className="text-base font-black text-slate-100 flex items-center gap-2">
                  <Package className="w-4 h-4 text-amber-500" />
                  Produtos Mais Vendidos (Top 5)
                </h2>
                <p className="text-[11px] text-slate-400 font-medium">Ordenado pelo volume de unidades de saída</p>
              </div>
              <span className="text-[10px] font-bold text-amber-500 bg-amber-500/10 border border-amber-500/20 px-2 py-0.5 rounded-full uppercase tracking-wider">
                Volume
              </span>
            </div>

            {topProducts.length === 0 ? (
              <div className="flex-1 flex flex-col items-center justify-center text-slate-500 py-10">
                <Package className="w-8 h-8 text-slate-600 mb-2 stroke-[1.5]" />
                <span className="text-xs font-semibold">Nenhum produto vendido neste período.</span>
              </div>
            ) : (
              <div className="flex flex-col gap-4 flex-1 justify-center">
                {topProducts.map((prod, idx) => {
                  const percent = (prod.quantity / maxProductQty) * 100;
                  return (
                    <div key={idx} className="flex flex-col gap-1 group">
                      <div className="flex justify-between items-center text-xs">
                        <div className="flex items-center gap-2 min-w-0">
                          <span className="w-5 h-5 rounded-md bg-white/[0.03] border border-white/5 text-[10px] font-black text-slate-400 flex items-center justify-center shrink-0">
                            {idx + 1}
                          </span>
                          <span className="font-extrabold text-slate-200 truncate group-hover:text-amber-400 transition">
                            {prod.productName}
                          </span>
                          <span className="text-[9px] text-slate-500 font-bold uppercase tracking-wider px-1.5 py-0.2 bg-white/[0.02] rounded shrink-0">
                            {prod.categoryName}
                          </span>
                        </div>
                        <div className="text-right shrink-0 flex items-center gap-2">
                          <span className="font-black text-slate-100">
                            {prod.quantity} <span className="text-[9px] text-slate-400 font-bold">{prod.unitMeasure}</span>
                          </span>
                          <span className="text-[10px] text-slate-400 font-bold">
                            ({(prod.revenue / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })})
                          </span>
                        </div>
                      </div>
                      
                      {/* Barra de Progresso Luxuosa */}
                      <div className="h-2 w-full rounded-full bg-white/[0.03] border border-white/5 overflow-hidden">
                        <div
                          className="h-full rounded-full bg-gradient-to-r from-amber-500 via-amber-400 to-orange-500 transition-all duration-1000 group-hover:brightness-110"
                          style={{ width: `${percent}%` }}
                        ></div>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>

          {/* Formas de Pagamento e Participação - Column span 5 */}
          <div className="glass rounded-3xl p-6 lg:col-span-5 flex flex-col">
            <h2 className="text-base font-black text-slate-100 flex items-center gap-2 mb-5">
              <Coins className="w-4 h-4 text-cyan-400" />
              Métodos de Pagamento
            </h2>

            <div className="flex flex-col gap-4 flex-1 justify-center">
              {paymentStats.map((item, idx) => {
                const Icon = item.icon;
                return (
                  <div key={idx} className="flex items-center gap-4">
                    <div className={`w-10 h-10 rounded-xl ${item.bg} flex items-center justify-center ${item.color} shrink-0`}>
                      <Icon className="w-5 h-5" />
                    </div>
                    
                    <div className="flex-1 min-w-0">
                      <div className="flex justify-between items-center text-xs font-extrabold mb-1">
                        <span className="text-slate-300">{item.name}</span>
                        <span className="text-slate-100">
                          {item.amount.toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                        </span>
                      </div>
                      
                      <div className="flex items-center gap-3">
                        <div className="h-1.5 flex-1 rounded-full bg-white/[0.03] border border-white/5 overflow-hidden">
                          <div
                            className={`h-full rounded-full bg-gradient-to-r from-slate-500 to-slate-300 transition-all duration-1000`}
                            style={{ 
                              width: `${item.percentage}%`,
                              backgroundImage: item.name === "Pix" ? "linear-gradient(to right, #22d3ee, #06b6d4)" :
                                              item.name === "Dinheiro" ? "linear-gradient(to right, #34d399, #10b981)" :
                                              item.name === "C. Débito" ? "linear-gradient(to right, #60a5fa, #3b82f6)" :
                                              "linear-gradient(to right, #818cf8, #6366f1)"
                            }}
                          ></div>
                        </div>
                        <span className="text-[10px] font-black text-slate-400 w-8 text-right shrink-0">
                          {item.percentage.toFixed(0)}%
                        </span>
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        </section>

        {/* ESTATÍSTICA EXTRA: FATURAMENTO POR CATEGORIAS (MERCADINHO E PRODUÇÃO DA PADARIA) */}
        <section className="grid grid-cols-1 lg:grid-cols-12 gap-6">
          
          {/* Faturamento por Categorias */}
          <div className="glass rounded-3xl p-6 lg:col-span-12">
            <div className="flex items-center justify-between mb-5">
              <div>
                <h2 className="text-base font-black text-slate-100 flex items-center gap-2">
                  <Layers className="w-4 h-4 text-orange-500" />
                  Desempenho por Categoria de Produto
                </h2>
                <p className="text-[11px] text-slate-400 font-medium">Quais setores do mercadinho e padaria estão faturando mais</p>
              </div>
            </div>

            {categoryStats.length === 0 ? (
              <div className="text-center py-8 text-slate-500 font-semibold text-xs">
                Nenhum dado de categoria disponível.
              </div>
            ) : (
              <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-4">
                {categoryStats.map((cat, idx) => {
                  const percent = (cat.revenue / maxCategoryRevenue) * 100;
                  return (
                    <div key={idx} className="glass bg-white/[0.01] border border-white/5 rounded-2xl p-4 flex flex-col justify-between group hover:border-amber-500/10 transition">
                      <div className="flex justify-between items-start mb-2">
                        <span className="text-xs font-black text-slate-200 group-hover:text-amber-400 transition truncate pr-2">
                          {cat.categoryName}
                        </span>
                        <span className="text-[9px] font-bold text-slate-500 bg-white/[0.04] px-1.5 py-0.2 rounded shrink-0">
                          {cat.quantity} unidades
                        </span>
                      </div>
                      
                      <div className="mt-2">
                        <span className="text-lg font-black text-slate-100">
                          {(cat.revenue / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                        </span>
                        
                        <div className="h-1 w-full rounded-full bg-white/[0.03] overflow-hidden mt-2">
                          <div
                            className="h-full rounded-full bg-amber-500"
                            style={{ width: `${percent}%` }}
                          ></div>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        </section>

        {/* DETALHAMENTO DE ATENDIMENTOS NO PERÍODO */}
        <section className="glass rounded-3xl p-6 overflow-hidden flex flex-col flex-1">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-5">
            <div>
              <h2 className="text-base font-black text-slate-100 flex items-center gap-2">
                <StoreIcon className="w-4 h-4 text-amber-500" />
                Histórico de Vendas Consolidadas
              </h2>
              <p className="text-[11px] text-slate-400 font-medium">Lista detalhada de todas as vendas aprovadas no período selecionado</p>
            </div>
            <span className="text-[10px] font-bold text-slate-400 bg-white/[0.03] border border-white/5 px-2.5 py-1 rounded-xl uppercase shrink-0">
              {filteredSales.length} {filteredSales.length === 1 ? "venda encontrada" : "vendas encontradas"}
            </span>
          </div>

          {filteredSales.length === 0 ? (
            <div className="text-center py-12 text-slate-500 flex flex-col items-center justify-center">
              <StoreIcon className="w-10 h-10 text-slate-700 mb-2" />
              <span className="text-xs font-semibold">Nenhuma venda realizada neste intervalo de tempo.</span>
            </div>
          ) : (
            <div className="overflow-x-auto w-full">
              <table className="w-full text-left text-xs border-collapse">
                <thead>
                  <tr className="border-b border-white/5 text-slate-400 font-bold uppercase tracking-wider text-[9px]">
                    <th className="py-3 px-4">Horário / Data</th>
                    <th className="py-3 px-4">Filial</th>
                    <th className="py-3 px-4">Operador de Caixa</th>
                    <th className="py-3 px-4">Método de Pagamento</th>
                    <th className="py-3 px-4 text-right">Desconto</th>
                    <th className="py-3 px-4 text-right">Valor Líquido</th>
                    <th className="py-3 px-4 text-center">Ações</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-white/[0.02]">
                  {filteredSales.map((sale) => {
                    const isHovered = hoveredSaleId === sale.id;
                    const formattedDate = new Date(sale.saleDate).toLocaleDateString("pt-BR", {
                      day: "2-digit",
                      month: "2-digit",
                      year: "numeric",
                      hour: "2-digit",
                      minute: "2-digit"
                    });
                    
                    return (
                      <tr
                        key={sale.id}
                        onMouseEnter={() => setHoveredSaleId(sale.id)}
                        onMouseLeave={() => setHoveredSaleId(null)}
                        className={`hover:bg-white/[0.02] transition-colors relative ${
                          isHovered ? "bg-white/[0.01]" : ""
                        }`}
                      >
                        <td className="py-3 px-4 font-semibold text-slate-300">
                          {formattedDate}
                        </td>
                        <td className="py-3 px-4">
                          <span className="font-bold text-slate-200">{sale.storeName}</span>
                        </td>
                        <td className="py-3 px-4 text-slate-400">
                          <div className="flex items-center gap-1.5">
                            <User className="w-3.5 h-3.5 text-slate-500" />
                            <span>{sale.userName}</span>
                          </div>
                        </td>
                        <td className="py-3 px-4">
                          <span className={`px-2 py-0.5 rounded text-[10px] font-bold ${
                            sale.paymentMethod === "PIX" ? "bg-cyan-500/10 text-cyan-400" :
                            sale.paymentMethod === "DINHEIRO" ? "bg-emerald-500/10 text-emerald-400" :
                            "bg-indigo-500/10 text-indigo-400"
                          }`}>
                            {sale.paymentMethod === "DINHEIRO" ? "Dinheiro" :
                             sale.paymentMethod === "PIX" ? "Pix" :
                             sale.paymentMethod === "CARTAO_DEBITO" ? "C. Débito" : "C. Crédito"}
                          </span>
                        </td>
                        <td className="py-3 px-4 text-right font-medium text-red-400">
                          {sale.discount > 0 
                            ? `-${(sale.discount / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}`
                            : "—"
                          }
                        </td>
                        <td className="py-3 px-4 text-right font-black text-slate-100">
                          {(sale.total / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                        </td>
                        <td className="py-3 px-4 text-center">
                          {/* Tooltip colapsável de itens rápido */}
                          <div className="relative inline-block">
                            <button
                              className="px-3 py-1 rounded bg-white/[0.03] border border-white/5 text-[10px] font-bold text-slate-400 hover:text-amber-400 hover:border-amber-500/20 transition cursor-pointer"
                              title="Visualizar Itens"
                            >
                              Ver Itens
                            </button>
                            
                            {/* Hover tooltip premium para itens */}
                            {isHovered && (
                              <div className="absolute right-0 bottom-full mb-2 w-72 bg-[#0a0a0d] border border-white/10 rounded-2xl shadow-2xl p-4 z-20 text-left backdrop-blur-md">
                                <span className="text-[10px] font-bold uppercase tracking-wider text-amber-500 block mb-2 pb-1 border-b border-white/5">
                                  Produtos da Venda
                                </span>
                                <div className="flex flex-col gap-2 max-h-48 overflow-y-auto pr-1">
                                  {sale.items.map((item, i) => (
                                    <div key={i} className="flex justify-between items-start text-xs border-b border-white/[0.02] pb-1.5 last:border-0 last:pb-0">
                                      <div className="min-w-0 pr-2">
                                        <div className="font-extrabold text-slate-200 truncate">{item.productName}</div>
                                        <div className="text-[9px] text-slate-500">{item.quantity} {item.unitMeasure} x {(item.priceUnit / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}</div>
                                      </div>
                                      <span className="font-black text-slate-300 shrink-0 text-right">
                                        {(item.subtotal / 100).toLocaleString("pt-BR", { style: "currency", currency: "BRL" })}
                                      </span>
                                    </div>
                                  ))}
                                </div>
                                {sale.notes && (
                                  <div className="mt-2 pt-2 border-t border-white/5 text-[9px] text-slate-400 italic">
                                    Obs: {sale.notes}
                                  </div>
                                )}
                              </div>
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>

      </div>
    </div>
  );
}
