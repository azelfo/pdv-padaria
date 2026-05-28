"use client";

import { useState, useTransition, useMemo } from "react";
import { useRouter } from "next/navigation";
import { 
  ArrowLeft, 
  Plus, 
  Minus, 
  RotateCcw, 
  AlertTriangle, 
  Package, 
  TrendingUp, 
  TrendingDown, 
  Calendar, 
  User, 
  CheckCircle2, 
  Loader2, 
  Sparkles,
  SlidersHorizontal,
  X,
  Search,
  Trash2,
  BarChart3,
  Edit2
} from "lucide-react";
import { toast } from "react-hot-toast";
import { adjustStockAction, addProductAction, deleteProductAction, updateProductAction } from "./actions";

interface ProductData {
  id: string;
  name: string;
  barcode: string | null;
  priceSale: number;
  priceCost: number;
  type: string;
  unitMeasure: string;
  categoryName: string;
  quantity: number;
  minStock: number;
}

interface MovementData {
  id: string;
  productName: string;
  unitMeasure: string;
  userName: string;
  type: string; // "ENTRADA", "SAIDA", "AJUSTE"
  quantity: number;
  reason: string; // "VENDA", "REPOSICAO", "PERDA", "AJUSTE_MANUAL"
  createdAt: string;
}

interface EstoqueClientProps {
  session: any;
  products: ProductData[];
  movements: MovementData[];
}

export default function EstoqueClient({ session, products, movements }: EstoqueClientProps) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  // Estados do Modal de Ajuste
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedProduct, setSelectedProduct] = useState<ProductData | null>(null);

  const [adjustType, setAdjustType] = useState<"ENTRADA" | "SAIDA">("ENTRADA");
  const [adjustQuantityInput, setAdjustQuantityInput] = useState("");
  const [adjustReason, setAdjustReason] = useState<"REPOSICAO" | "PERDA" | "AJUSTE_MANUAL">("REPOSICAO");

  // Estados do Modal de Cadastro (Add)
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [newName, setNewName] = useState("");
  const [newBarcode, setNewBarcode] = useState("");
  const [newPriceCost, setNewPriceCost] = useState("");
  const [newPriceSale, setNewPriceSale] = useState("");
  const [newCategorySelect, setNewCategorySelect] = useState("");
  const [newCategoryName, setNewCategoryName] = useState("");
  const [newUnitMeasure, setNewUnitMeasure] = useState("UN");
  const [newType, setNewType] = useState("NORMAL");
  const [newMinStock, setNewMinStock] = useState("");
  const [newInitialStock, setNewInitialStock] = useState("");

  // Estados do Modal de Exclusão (Delete)
  const [isDeleteModalOpen, setIsDeleteModalOpen] = useState(false);
  const [productToDelete, setProductToDelete] = useState<ProductData | null>(null);

  // Estados do Modal de Edição (Edit)
  const [isEditModalOpen, setIsEditModalOpen] = useState(false);
  const [productToEdit, setProductToEdit] = useState<ProductData | null>(null);
  const [editName, setEditName] = useState("");
  const [editBarcode, setEditBarcode] = useState("");
  const [editPriceCost, setEditPriceCost] = useState("");
  const [editPriceSale, setEditPriceSale] = useState("");
  const [editCategoryName, setEditCategoryName] = useState("");
  const [editUnitMeasure, setEditUnitMeasure] = useState("UN");
  const [editType, setEditType] = useState("NORMAL");

  // Filtro de buscas e categorias de produtos no estoque
  const [searchQuery, setSearchQuery] = useState("");
  const [selectedCategory, setSelectedCategory] = useState("TODOS");

  // Lista de categorias únicas ordenadas
  const categories = useMemo(() => {
    const list = new Set(products.map((p) => p.categoryName));
    return ["TODOS", ...Array.from(list).sort()];
  }, [products]);

  // Contagem de itens por categoria
  const categoryCounts = useMemo(() => {
    const counts: Record<string, number> = { TODOS: products.length };
    products.forEach((p) => {
      counts[p.categoryName] = (counts[p.categoryName] || 0) + 1;
    });
    return counts;
  }, [products]);

  const filteredProducts = useMemo(() => {
    return products.filter((p) => {
      const matchesSearch = p.name.toLowerCase().includes(searchQuery.toLowerCase()) || 
        (p.barcode && p.barcode.includes(searchQuery));
      const matchesCategory = selectedCategory === "TODOS" || p.categoryName === selectedCategory;
      return matchesSearch && matchesCategory;
    });
  }, [products, searchQuery, selectedCategory]);

  // Estatísticas consolidadas
  const stats = useMemo(() => {
    const totalItems = products.length;
    const lowStockItems = products.filter((p) => p.quantity <= p.minStock && p.quantity > 0).length;
    const outOfStockItems = products.filter((p) => p.quantity <= 0).length;
    return { totalItems, lowStockItems, outOfStockItems };
  }, [products]);

  const formatCurrency = (cents: number) => {
    return new Intl.NumberFormat("pt-BR", {
      style: "currency",
      currency: "BRL",
    }).format(cents / 100);
  };

  const handleOpenAdjustModal = (product: ProductData) => {
    setSelectedProduct(product);
    setAdjustType("ENTRADA");
    setAdjustQuantityInput("");
    setAdjustReason("REPOSICAO");
    setIsModalOpen(true);
  };

  const handleAdjustSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!selectedProduct) return;

    const qty = parseFloat(adjustQuantityInput.replace(",", "."));
    if (isNaN(qty) || qty <= 0) {
      toast.error("Digite uma quantidade válida superior a zero.");
      return;
    }

    startTransition(async () => {
      const result = await adjustStockAction({
        productId: selectedProduct.id,
        quantity: qty,
        type: adjustType,
        reason: adjustReason,
      });

      if (result.success) {
        toast.success("Movimentação de estoque lançada!");
        setIsModalOpen(false);
        setSelectedProduct(null);
        setAdjustQuantityInput("");
      } else {
        toast.error(result.error || "Erro ao realizar movimentação.");
      }
    });
  };

  // Extrai categorias existentes para o select do cadastro (exclui TODOS)
  const existingCategories = useMemo(() => {
    return categories.filter((c) => c !== "TODOS");
  }, [categories]);

  const handleOpenAddModal = () => {
    setNewName("");
    setNewBarcode("");
    setNewPriceCost("");
    setNewPriceSale("");
    setNewCategorySelect("");
    setNewCategoryName("");
    setNewUnitMeasure("UN");
    setNewType("NORMAL");
    setNewMinStock("");
    setNewInitialStock("");
    setIsAddModalOpen(true);
  };

  const handleAddSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();

    const priceCostCents = Math.round(parseFloat(newPriceCost.replace(",", ".")) * 100);
    const priceSaleCents = Math.round(parseFloat(newPriceSale.replace(",", ".")) * 100);
    const minStockVal = parseFloat(newMinStock.replace(",", "."));
    const initialStockVal = parseFloat(newInitialStock.replace(",", "."));

    const finalCategory = newCategorySelect === "NEW" || !newCategorySelect 
      ? newCategoryName 
      : newCategorySelect;

    if (!newName.trim()) {
      toast.error("O nome do produto é obrigatório.");
      return;
    }
    if (isNaN(priceSaleCents) || priceSaleCents < 0) {
      toast.error("O preço de venda é inválido.");
      return;
    }
    if (isNaN(priceCostCents) || priceCostCents < 0) {
      toast.error("O preço de custo é inválido.");
      return;
    }
    if (!finalCategory.trim()) {
      toast.error("A categoria do produto é obrigatória.");
      return;
    }
    if (isNaN(minStockVal) || minStockVal < 0) {
      toast.error("O estoque mínimo deve ser superior ou igual a zero.");
      return;
    }
    if (isNaN(initialStockVal) || initialStockVal < 0) {
      toast.error("O estoque inicial deve ser superior ou igual a zero.");
      return;
    }

    startTransition(async () => {
      const result = await addProductAction({
        name: newName,
        barcode: newBarcode.trim() || null,
        priceSale: priceSaleCents,
        priceCost: priceCostCents,
        categoryName: finalCategory,
        unitMeasure: newUnitMeasure,
        minStock: minStockVal,
        initialStock: initialStockVal,
        type: newType,
      });

      if (result.success) {
        toast.success("Produto cadastrado com sucesso!");
        setIsAddModalOpen(false);
      } else {
        toast.error(result.error || "Erro ao cadastrar produto.");
      }
    });
  };

  const handleOpenDeleteModal = (product: ProductData) => {
    setProductToDelete(product);
    setIsDeleteModalOpen(true);
  };

  const handleDeleteSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!productToDelete) return;

    startTransition(async () => {
      const result = await deleteProductAction(productToDelete.id);

      if (result.success) {
        toast.success("Produto removido com sucesso!");
        setIsDeleteModalOpen(false);
        setProductToDelete(null);
      } else {
        toast.error(result.error || "Erro ao remover produto.");
      }
    });
  };

  const handleOpenEditModal = (product: ProductData) => {
    setProductToEdit(product);
    setEditName(product.name);
    setEditBarcode(product.barcode || "");
    setEditPriceCost((product.priceCost / 100).toFixed(2).replace(".", ","));
    setEditPriceSale((product.priceSale / 100).toFixed(2).replace(".", ","));
    setEditCategoryName(product.categoryName);
    setEditUnitMeasure(product.unitMeasure);
    setEditType(product.type);
    setIsEditModalOpen(true);
  };

  const handleEditSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!productToEdit) return;

    const priceSaleVal = Math.round(parseFloat(editPriceSale.replace(",", ".")) * 100);
    const priceCostVal = Math.round(parseFloat(editPriceCost.replace(",", ".")) * 100);

    if (!editName || isNaN(priceSaleVal) || priceSaleVal < 0 || isNaN(priceCostVal) || priceCostVal < 0 || !editCategoryName) {
      toast.error("Preencha todos os campos obrigatórios com valores válidos.");
      return;
    }

    startTransition(async () => {
      const result = await updateProductAction({
        id: productToEdit.id,
        name: editName,
        barcode: editBarcode.trim() !== "" ? editBarcode.trim() : null,
        priceSale: priceSaleVal,
        priceCost: priceCostVal,
        categoryName: editCategoryName,
        unitMeasure: editUnitMeasure,
        type: editType,
      });

      if (result.success) {
        toast.success("Produto atualizado com sucesso!");
        setIsEditModalOpen(false);
        setProductToEdit(null);
      } else {
        toast.error(result.error || "Erro ao atualizar produto.");
      }
    });
  };

  const handleBackToPdv = () => {
    router.push("/pdv");
  };

  return (
    <div className="min-h-screen flex flex-col bg-[#050507] text-slate-100 p-6">
      
      {/* Luzes decorativas */}
      <div className="absolute top-10 right-10 w-96 h-96 bg-amber-500/5 rounded-full blur-[100px] pointer-events-none"></div>

      <div className="w-full max-w-7xl mx-auto z-10 flex-1 flex flex-col">
        
        {/* CABEÇALHO */}
        <header className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-8">
          <div className="flex items-center gap-3">
            <button
              onClick={handleBackToPdv}
              className="w-10 h-10 rounded-xl bg-white/[0.02] border border-white/5 flex items-center justify-center text-slate-400 hover:text-amber-400 hover:bg-white/5 hover:border-amber-500/20 transition cursor-pointer"
            >
              <ArrowLeft className="w-5 h-5" />
            </button>
            <div>
              <span className="text-[10px] font-bold text-amber-500 uppercase tracking-wider block">
                Filial: {session.storeName || "Loja Ativa"}
              </span>
              <h1 className="text-2xl font-extrabold tracking-tight text-slate-100">
                Painel de Controle de Estoque
              </h1>
            </div>
          </div>

          <div className="flex items-center gap-3 w-full sm:w-auto">
            <div className="relative w-full sm:w-64">
              <span className="absolute inset-y-0 left-0 pl-3 flex items-center text-slate-500 pointer-events-none">
                <Search className="w-4 h-4" />
              </span>
              <input
                type="text"
                placeholder="Buscar no estoque..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full pl-9 pr-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-500"
              />
            </div>

            {session.role === "DONO" && (
              <button
                onClick={() => router.push("/pdv/dashboard")}
                type="button"
                className="flex items-center gap-1.5 px-4 py-2.5 rounded-xl bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer text-xs font-bold uppercase tracking-wider shrink-0 select-none"
              >
                <BarChart3 className="w-4 h-4" />
                Dashboard
              </button>
            )}

            {session.role === "DONO" || session.role === "GERENTE" ? (
              <button
                onClick={handleOpenAddModal}
                type="button"
                className="flex items-center gap-1.5 px-4 py-2.5 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer shadow-lg shadow-amber-500/10 shrink-0 select-none active:scale-[0.98]"
              >
                <Plus className="w-4 h-4 text-black stroke-[2.5]" />
                Adicionar Produto
              </button>
            ) : null}
          </div>
        </header>

        {/* CARDS RESUMO ESTATÍSTICOS */}
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-6 mb-8">
          {/* Card Total */}
          <div className="glass rounded-3xl p-5 flex items-center gap-4 relative overflow-hidden">
            <div className="w-12 h-12 rounded-2xl bg-amber-500/10 flex items-center justify-center text-amber-500">
              <Package className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div>
              <span className="text-xs text-slate-400 font-semibold block uppercase tracking-wider">Produtos Totais</span>
              <span className="text-2xl font-black text-slate-100">{stats.totalItems}</span>
            </div>
          </div>

          {/* Card Baixo Estoque */}
          <div className="glass rounded-3xl p-5 flex items-center gap-4 relative overflow-hidden">
            <div className={`w-12 h-12 rounded-2xl flex items-center justify-center ${
              stats.lowStockItems > 0 
                ? "bg-orange-500/20 text-orange-400 animate-pulse" 
                : "bg-white/[0.02] text-slate-500"
            }`}>
              <AlertTriangle className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div>
              <span className="text-xs text-slate-400 font-semibold block uppercase tracking-wider">Estoque Crítico</span>
              <span className={`text-2xl font-black ${
                stats.lowStockItems > 0 ? "text-orange-400" : "text-slate-100"
              }`}>{stats.lowStockItems}</span>
            </div>
          </div>

          {/* Card Esgotados */}
          <div className="glass rounded-3xl p-5 flex items-center gap-4 relative overflow-hidden">
            <div className={`w-12 h-12 rounded-2xl flex items-center justify-center ${
              stats.outOfStockItems > 0 
                ? "bg-red-500/20 text-red-400" 
                : "bg-white/[0.02] text-slate-500"
            }`}>
              <AlertTriangle className="w-6 h-6 stroke-[1.8]" />
            </div>
            <div>
              <span className="text-xs text-slate-400 font-semibold block uppercase tracking-wider">Itens Esgotados</span>
              <span className={`text-2xl font-black ${
                stats.outOfStockItems > 0 ? "text-red-400 font-black" : "text-slate-100"
              }`}>{stats.outOfStockItems}</span>
            </div>
          </div>
        </div>

        {/* LAYOUT DUAS COLUNAS: ESQUERDA TABELA, DIREITA AUDITORIA */}
        <div className="grid grid-cols-1 lg:grid-cols-12 gap-8 flex-1">
          
          {/* COLUNA ESQUERDA: LISTA DE ESTOQUES (8 colunas) */}
          <div className="lg:col-span-8 flex flex-col justify-between glass rounded-3xl overflow-hidden shadow-2xl min-h-[450px]">
            
            {/* Abas horizontais de Categorias */}
            <div className="p-4 border-b border-white/5 bg-white/[0.01] flex items-center gap-2 overflow-x-auto no-scrollbar scroll-smooth">
              {categories.map((cat) => {
                const isActive = selectedCategory === cat;
                const count = categoryCounts[cat] || 0;
                return (
                  <button
                    key={cat}
                    onClick={() => setSelectedCategory(cat)}
                    type="button"
                    className={`px-4 py-2.5 rounded-2xl text-xs font-bold transition-all duration-300 flex items-center gap-2 whitespace-nowrap cursor-pointer border ${
                      isActive
                        ? "bg-gradient-to-r from-amber-500/20 to-orange-500/20 border-amber-500/40 text-amber-400 shadow-[0_0_20px_rgba(245,158,11,0.1)] scale-[1.02]"
                        : "bg-white/[0.01] border-white/5 text-slate-400 hover:text-slate-200 hover:bg-white/5 hover:border-white/10"
                    }`}
                  >
                    {cat === "TODOS" ? "📦 Todos os Produtos" : cat}
                    <span className={`text-[10px] px-1.5 py-0.5 rounded-md font-extrabold transition-all duration-300 ${
                      isActive 
                        ? "bg-amber-500/20 text-amber-300 border border-amber-500/30" 
                        : "bg-white/5 text-slate-500 border border-white/5"
                    }`}>
                      {count}
                    </span>
                  </button>
                );
              })}
            </div>

            <div className="overflow-x-auto max-h-[550px] overflow-y-auto scroll-smooth">
              <table className="w-full text-left border-collapse">
                <thead className="sticky top-0 z-10 bg-[#07070a]/95 backdrop-blur-sm">
                  <tr className="border-b border-white/5 bg-[#07070a]/50">
                    <th className="p-4 pl-6 text-xs font-bold uppercase tracking-wider text-slate-400">Produto</th>
                    <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Categoria</th>
                    <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Estoque Atual</th>
                    <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Estoque Mínimo</th>
                    <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Preço Venda</th>
                    <th className="p-4 pr-6 text-xs font-bold uppercase tracking-wider text-slate-400 text-right">Ação</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-white/5 text-sm font-medium">
                  {filteredProducts.map((p) => {
                    const isLow = p.quantity <= p.minStock && p.quantity > 0;
                    const isOut = p.quantity <= 0;

                    return (
                      <tr 
                        key={p.id}
                        className={`hover:bg-white/[0.01] transition-colors ${
                          isOut ? "bg-red-500/[0.01]" : isLow ? "bg-orange-500/[0.01]" : ""
                        }`}
                      >
                        {/* Nome do Produto */}
                        <td className="p-4 pl-6">
                          <div>
                            <span className="font-bold text-slate-200 block">{p.name}</span>
                            <span className="text-[10px] text-slate-500 font-mono">Cód: {p.barcode || "N/A"}</span>
                          </div>
                        </td>

                        {/* Categoria */}
                        <td className="p-4">
                          <span className="text-xs font-semibold text-slate-400 uppercase tracking-wider bg-white/[0.03] px-2 py-0.5 rounded border border-white/5">
                            {p.categoryName}
                          </span>
                        </td>

                        {/* Quantidade em estoque */}
                        <td className="p-4">
                          <div className="flex items-center gap-2">
                            <span className={`text-base font-black ${
                              isOut 
                                ? "text-red-400" 
                                : isLow 
                                  ? "text-orange-400" 
                                  : "text-slate-200"
                            }`}>
                              {p.quantity}
                            </span>
                            <span className="text-[10px] text-slate-500 font-bold uppercase shrink-0">
                              {p.unitMeasure}
                            </span>
                          </div>
                        </td>

                        {/* Mínimo */}
                        <td className="p-4 text-slate-400">
                          {p.minStock} {p.unitMeasure}
                        </td>

                        {/* Preço de Venda */}
                        <td className="p-4 text-slate-300 font-extrabold">
                          {formatCurrency(p.priceSale)}
                        </td>

                        {/* Ações */}
                        <td className="p-4 pr-6 text-right whitespace-nowrap">
                          <div className="flex items-center justify-end gap-2">
                            <button
                              onClick={() => handleOpenAdjustModal(p)}
                              type="button"
                              className="flex items-center gap-1 px-3 py-2 rounded-xl border border-white/5 bg-white/[0.02] hover:bg-amber-500/10 hover:border-amber-500/20 text-slate-400 hover:text-amber-400 text-xs font-bold transition cursor-pointer"
                            >
                              <SlidersHorizontal className="w-3.5 h-3.5" />
                              Ajustar
                            </button>

                            {session.role === "DONO" || session.role === "GERENTE" ? (
                              <>
                                <button
                                  onClick={() => handleOpenEditModal(p)}
                                  type="button"
                                  className="flex items-center justify-center w-8 h-8 rounded-xl border border-white/5 bg-white/[0.02] hover:bg-amber-500/10 hover:border-amber-500/20 text-slate-500 hover:text-amber-400 transition cursor-pointer animate-fade-in"
                                  title="Editar produto"
                                >
                                  <Edit2 className="w-3.5 h-3.5" />
                                </button>

                                {p.type !== "PAO_FRANCES" ? (
                                  <button
                                    onClick={() => handleOpenDeleteModal(p)}
                                    type="button"
                                    className="flex items-center justify-center w-8 h-8 rounded-xl border border-white/5 bg-white/[0.02] hover:bg-red-500/10 hover:border-red-500/20 text-slate-500 hover:text-red-400 transition cursor-pointer animate-fade-in"
                                    title="Excluir produto"
                                  >
                                    <Trash2 className="w-4 h-4" />
                                  </button>
                                ) : (
                                  <div 
                                    className="flex items-center justify-center w-8 h-8 rounded-xl border border-white/5 bg-white/[0.01] text-slate-600 cursor-not-allowed"
                                    title="Produto protegido - Não pode ser excluído"
                                  >
                                    <Trash2 className="w-4 h-4 opacity-30" />
                                  </div>
                                )}
                              </>
                            ) : null}
                          </div>
                        </td>
                      </tr>
                    );
                  })}

                  {filteredProducts.length === 0 && (
                    <tr>
                      <td colSpan={6} className="p-16 text-center text-slate-500 font-medium">
                        Nenhum produto correspondente no estoque.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>

            <div className="p-4 border-t border-white/5 bg-white/[0.01] flex items-center justify-between text-xs text-slate-500">
              <span>Auditoria de estoque ativa vinculada ao usuário: {session.name}</span>
            </div>
          </div>

          {/* COLUNA DIREITA: TIMELINE HISTÓRICA (4 colunas) */}
          <div className="lg:col-span-4 flex flex-col justify-between glass rounded-3xl p-6 shadow-2xl min-h-[450px]">
            <div>
              <div className="flex items-center justify-between pb-4 border-b border-white/5 mb-5">
                <h3 className="font-bold text-slate-200">Histórico de Lançamentos</h3>
                <span className="text-[9px] bg-amber-500/15 text-amber-500 border border-amber-500/20 px-2 py-0.5 rounded uppercase font-black">
                  Logs Ativos
                </span>
              </div>

              {/* Log Timeline */}
              <div className="space-y-4 max-h-[360px] overflow-y-auto pr-1">
                {movements.map((move) => {
                  const isEntrada = move.type === "ENTRADA";
                  const isVenda = move.reason === "VENDA";
                  
                  return (
                    <div 
                      key={move.id} 
                      className="text-xs p-3 rounded-2xl bg-white/[0.01] border border-white/5 flex gap-3 relative overflow-hidden"
                    >
                      {/* Indicador visual de tipo */}
                      <div className={`w-8 h-8 rounded-xl flex items-center justify-center shrink-0 ${
                        isEntrada 
                          ? "bg-emerald-500/10 text-emerald-400" 
                          : isVenda 
                            ? "bg-amber-500/10 text-amber-500" 
                            : "bg-red-500/10 text-red-400"
                      }`}>
                        {isEntrada ? (
                          <TrendingUp className="w-4 h-4" />
                        ) : (
                          <TrendingDown className="w-4 h-4" />
                        )}
                      </div>

                      {/* Conteúdo textual */}
                      <div className="space-y-1">
                        <span className="font-bold text-slate-200 block leading-tight">
                          {move.productName}
                        </span>
                        
                        <p className="text-slate-400 font-medium leading-normal">
                          {isEntrada ? "Entrada de" : "Saída de"}{" "}
                          <span className="font-bold text-slate-300">
                            {move.quantity} {move.unitMeasure}
                          </span>{" "}
                          ({move.reason === "REPOSICAO" ? "Reposição" : move.reason === "PERDA" ? "Perda" : move.reason === "VENDA" ? "Venda Caixa" : "Ajuste manual"})
                        </p>
                        
                        <div className="flex items-center gap-3 text-[10px] text-slate-500 mt-2 font-semibold">
                          <span className="flex items-center gap-1">
                            <User className="w-3.5 h-3.5 text-slate-600" />
                            {move.userName}
                          </span>
                          <span className="flex items-center gap-1">
                            <Calendar className="w-3.5 h-3.5 text-slate-600" />
                            {new Date(move.createdAt).toLocaleDateString()}
                          </span>
                        </div>
                      </div>
                    </div>
                  );
                })}

                {movements.length === 0 && (
                  <div className="text-center py-16 text-slate-600 text-xs font-medium">
                    Nenhuma movimentação lançada recentemente.
                  </div>
                )}
              </div>
            </div>

            <p className="text-[10px] text-slate-500 italic mt-6 border-t border-white/5 pt-4 text-center">
              Histórico rastreado sob auditoria judicial multi-lojas.
            </p>
          </div>

        </div>

      </div>

      {/* 🛠️ MODAL DE AJUSTE MANUAL DE ESTOQUE GLASS */}
      {isModalOpen && selectedProduct && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-md relative overflow-hidden">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <button
              onClick={() => {
                setIsModalOpen(false);
                setSelectedProduct(null);
              }}
              className="absolute top-4 right-4 text-slate-500 hover:text-slate-200 transition cursor-pointer"
            >
              <X className="w-5 h-5" />
            </button>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-2">
              <SlidersHorizontal className="w-5 h-5 text-amber-500" />
              Ajuste de Estoque Manual
            </h3>
            
            <p className="text-slate-400 text-xs mb-5">
              Produto: <span className="font-bold text-slate-300">{selectedProduct.name}</span> • Estoque Atual: <span className="font-extrabold text-amber-500">{selectedProduct.quantity} {selectedProduct.unitMeasure}</span>
            </p>

            <form onSubmit={handleAdjustSubmit} className="space-y-4">
              
              {/* Tipo de Ajuste */}
              <div>
                <span className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Tipo de Movimentação
                </span>
                <div className="grid grid-cols-2 gap-3">
                  <button
                    type="button"
                    onClick={() => {
                      setAdjustType("ENTRADA");
                      setAdjustReason("REPOSICAO");
                    }}
                    className={`py-3 rounded-xl border font-bold text-xs transition cursor-pointer flex items-center justify-center gap-1.5 ${
                      adjustType === "ENTRADA"
                        ? "border-emerald-500 bg-emerald-500/5 text-emerald-400"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <Plus className="w-4 h-4" />
                    Entrada (Somar)
                  </button>

                  <button
                    type="button"
                    onClick={() => {
                      setAdjustType("SAIDA");
                      setAdjustReason("PERDA");
                    }}
                    className={`py-3 rounded-xl border font-bold text-xs transition cursor-pointer flex items-center justify-center gap-1.5 ${
                      adjustType === "SAIDA"
                        ? "border-red-500 bg-red-500/5 text-red-400"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <Minus className="w-4 h-4" />
                    Saída (Subtrair)
                  </button>
                </div>
              </div>

              {/* Quantidade */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Quantidade a Lançar ({selectedProduct.unitMeasure})
                </label>
                <input
                  type="text"
                  required
                  autoFocus
                  placeholder="0.00"
                  value={adjustQuantityInput}
                  onChange={(e) => setAdjustQuantityInput(e.target.value)}
                  className="w-full px-4 py-2.5 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-700 focus:outline-none font-bold"
                />
              </div>

              {/* Motivo da Movimentação */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Motivo da Movimentação
                </label>
                <select
                  value={adjustReason}
                  onChange={(e) => setAdjustReason(e.target.value as any)}
                  className="w-full px-3 py-2.5 text-sm rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                >
                  {adjustType === "ENTRADA" ? (
                    <>
                      <option value="REPOSICAO" className="bg-[#121217]">REPOSIÇÃO (Novas Mercadorias)</option>
                      <option value="AJUSTE_MANUAL" className="bg-[#121217]">AJUSTE MANUAL (Correção/Acerto)</option>
                    </>
                  ) : (
                    <>
                      <option value="PERDA" className="bg-[#121217]">PERDA (Vencimento/Quebra/Roubo)</option>
                      <option value="AJUSTE_MANUAL" className="bg-[#121217]">AJUSTE MANUAL (Correção/Acerto)</option>
                    </>
                  )}
                </select>
              </div>

              {/* Botões */}
              <div className="flex gap-3 pt-4">
                <button
                  type="button"
                  onClick={() => {
                    setIsModalOpen(false);
                    setSelectedProduct(null);
                  }}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={isPending}
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1.5"
                >
                  {isPending ? (
                    <Loader2 className="w-4 h-4 animate-spin text-black" />
                  ) : (
                    "Lançar Ajuste"
                  )}
                </button>
              </div>

            </form>
          </div>
        </div>
      )}

      {/* ➕ MODAL DE CADASTRAR PRODUTO GLASS */}
      {isAddModalOpen && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50 overflow-y-auto">
          <div className="glass rounded-3xl p-6 w-full max-w-xl relative overflow-hidden my-8">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <button
              onClick={() => setIsAddModalOpen(false)}
              type="button"
              className="absolute top-4 right-4 text-slate-500 hover:text-slate-200 transition cursor-pointer"
            >
              <X className="w-5 h-5" />
            </button>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-4">
              <Package className="w-5 h-5 text-amber-500" />
              Cadastrar Novo Produto no Estoque
            </h3>

            <form onSubmit={handleAddSubmit} className="space-y-4">
              
              {/* Nome e Código de Barras */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Nome do Produto *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="Ex: Pão de Queijo Caseiro"
                    value={newName}
                    onChange={(e) => setNewName(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold"
                  />
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Código de Barras (Opcional)
                  </label>
                  <input
                    type="text"
                    placeholder="Bipe ou digite o código..."
                    value={newBarcode}
                    onChange={(e) => setNewBarcode(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-mono"
                  />
                </div>
              </div>

              {/* Categorias & Unidade de Medida */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Categoria *
                  </label>
                  <div className="space-y-2">
                    <select
                      value={newCategorySelect}
                      required
                      onChange={(e) => setNewCategorySelect(e.target.value)}
                      className="w-full px-3 py-2.5 text-xs rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                    >
                      <option value="" className="bg-[#121217]">-- Selecione uma Categoria --</option>
                      {existingCategories.map((cat) => (
                        <option key={cat} value={cat} className="bg-[#121217]">{cat}</option>
                      ))}
                      <option value="NEW" className="bg-[#121217] text-amber-400 font-bold">+ Criar Nova Categoria</option>
                    </select>

                    {(newCategorySelect === "NEW" || existingCategories.length === 0) && (
                      <input
                        type="text"
                        required
                        placeholder="Digite o nome da nova categoria..."
                        value={newCategoryName}
                        onChange={(e) => setNewCategoryName(e.target.value)}
                        className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold animate-fade-in"
                      />
                    )}
                  </div>
                </div>

                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                      Unidade de Medida
                    </label>
                    <select
                      value={newUnitMeasure}
                      onChange={(e) => setNewUnitMeasure(e.target.value)}
                      className="w-full px-3 py-2.5 text-xs rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                    >
                      <option value="UN" className="bg-[#121217]">UN (Unidade)</option>
                      <option value="KG" className="bg-[#121217]">KG (Quilo)</option>
                      <option value="FATIA" className="bg-[#121217]">FATIA (Bolo/Torta)</option>
                      <option value="MEIA_DUZIA" className="bg-[#121217]">MEIA DÚZIA</option>
                    </select>
                  </div>

                  <div>
                    <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                      Tipo de Produto
                    </label>
                    <select
                      value={newType}
                      onChange={(e) => setNewType(e.target.value)}
                      className="w-full px-3 py-2.5 text-xs rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                    >
                      <option value="NORMAL" className="bg-[#121217]">NORMAL</option>
                      <option value="SALGADO" className="bg-[#121217]">SALGADO</option>
                      <option value="BOLO" className="bg-[#121217]">BOLO</option>
                      <option value="PAO_FRANCES" className="bg-[#121217]">PÃO FRANCÊS</option>
                    </select>
                  </div>
                </div>
              </div>

              {/* Preços de Custo e Venda */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Preço de Custo (R$) *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0,00"
                    value={newPriceCost}
                    onChange={(e) => setNewPriceCost(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold"
                  />
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Preço de Venda (R$) *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0,00"
                    value={newPriceSale}
                    onChange={(e) => setNewPriceSale(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold text-amber-400"
                  />
                </div>
              </div>

              {/* Saldos de Estoque e Mínimo */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Estoque Inicial *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0"
                    value={newInitialStock}
                    onChange={(e) => setNewInitialStock(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold"
                  />
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Estoque Mínimo (Alerta) *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0"
                    value={newMinStock}
                    onChange={(e) => setNewMinStock(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold text-orange-400"
                  />
                </div>
              </div>

              <div className="text-[10px] text-slate-500 italic pt-1">
                * O produto será criado de forma global e inicializado com estoque nesta filial. Em outras filiais, iniciará zerado.
              </div>

              {/* Botões */}
              <div className="flex gap-3 pt-4 border-t border-white/5">
                <button
                  type="button"
                  onClick={() => setIsAddModalOpen(false)}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={isPending}
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1.5"
                >
                  {isPending ? (
                    <Loader2 className="w-4 h-4 animate-spin text-black" />
                  ) : (
                    "Cadastrar Produto"
                  )}
                </button>
              </div>

            </form>
          </div>
        </div>
      )}

      {/* 🗑️ MODAL DE CONFIRMAÇÃO DE EXCLUSÃO (SOFT DELETE) GLASS */}
      {isDeleteModalOpen && productToDelete && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-md relative overflow-hidden text-center flex flex-col items-center">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-red-500 to-transparent"></div>

            <button
              onClick={() => {
                setIsDeleteModalOpen(false);
                setProductToDelete(null);
              }}
              type="button"
              className="absolute top-4 right-4 text-slate-500 hover:text-slate-200 transition cursor-pointer"
            >
              <X className="w-5 h-5" />
            </button>

            <div className="w-12 h-12 rounded-2xl bg-red-500/10 flex items-center justify-center text-red-500 mb-4 animate-pulse-subtle">
              <Trash2 className="w-6 h-6 stroke-[1.8]" />
            </div>

            <h3 className="text-lg font-bold text-slate-100 mb-2">
              Remover Produto do Catálogo?
            </h3>
            
            <p className="text-slate-400 text-xs leading-relaxed mb-6 px-2">
              Você tem certeza que deseja excluir o produto <span className="font-extrabold text-slate-200">{productToDelete.name}</span>? 
              <br/><br/>
              Ele deixará de ser listado para novas vendas no caixa e na tabela de estoques. O histórico de movimentações e vendas passadas <span className="font-bold text-slate-300">continuará arquivado intacto</span> no banco de dados.
            </p>

            <form onSubmit={handleDeleteSubmit} className="w-full flex gap-3">
              <button
                type="button"
                onClick={() => {
                  setIsDeleteModalOpen(false);
                  setProductToDelete(null);
                }}
                className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
              >
                Cancelar
              </button>
              <button
                type="submit"
                disabled={isPending}
                className="flex-1 py-3 rounded-xl bg-red-500 text-white font-extrabold text-xs hover:bg-red-400 transition cursor-pointer flex items-center justify-center gap-1.5 shadow-lg shadow-red-500/10"
              >
                {isPending ? (
                  <Loader2 className="w-4 h-4 animate-spin text-white" />
                ) : (
                  "Confirmar Exclusão"
                )}
              </button>
            </form>
          </div>
        </div>
      )}

      {/* 📝 MODAL DE EDITAR PRODUTO GLASS */}
      {isEditModalOpen && productToEdit && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50 overflow-y-auto">
          <div className="glass rounded-3xl p-6 w-full max-w-xl relative overflow-hidden my-8">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <button
              onClick={() => {
                setIsEditModalOpen(false);
                setProductToEdit(null);
              }}
              type="button"
              className="absolute top-4 right-4 text-slate-500 hover:text-slate-200 transition cursor-pointer"
            >
              <X className="w-5 h-5" />
            </button>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-2">
              <Edit2 className="w-5 h-5 text-amber-500" />
              Editar Informações do Produto
            </h3>

            <p className="text-slate-400 text-xs mb-6">
              Atualize as informações de cadastro do produto de forma global no sistema.
            </p>

            <form onSubmit={handleEditSubmit} className="space-y-4">
              
              {/* Nome */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Nome do Produto *
                </label>
                <input
                  type="text"
                  required
                  placeholder="Ex: Coca-Cola Lata 350ml"
                  value={editName}
                  onChange={(e) => setEditName(e.target.value)}
                  className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold"
                />
              </div>

              {/* Código de Barras e Categoria */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Código de Barras (Opcional)
                  </label>
                  <input
                    type="text"
                    placeholder="Bipe ou digite o código..."
                    value={editBarcode}
                    onChange={(e) => setEditBarcode(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold text-amber-400"
                  />
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Categoria *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="Ex: Bebidas"
                    value={editCategoryName}
                    onChange={(e) => setEditCategoryName(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold"
                  />
                </div>
              </div>

              {/* Unidade de Medida e Tipo de Produto */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Unidade de Medida
                  </label>
                  <select
                    value={editUnitMeasure}
                    onChange={(e) => setEditUnitMeasure(e.target.value)}
                    className="w-full px-3 py-2.5 text-xs rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                  >
                    <option value="UN" className="bg-[#121217]">UNIDADE (UN)</option>
                    <option value="KG" className="bg-[#121217]">QUILOGRAMA (KG)</option>
                    <option value="FATIA" className="bg-[#121217]">FATIA</option>
                    <option value="MEIA_DUZIA" className="bg-[#121217]">MEIA DÚZIA</option>
                  </select>
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Tipo de Produto
                  </label>
                  <select
                    value={editType}
                    onChange={(e) => setEditType(e.target.value)}
                    className="w-full px-3 py-2.5 text-xs rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer font-semibold"
                    disabled={productToEdit.type === "PAO_FRANCES"}
                  >
                    <option value="NORMAL" className="bg-[#121217]">NORMAL</option>
                    <option value="SALGADO" className="bg-[#121217]">SALGADO</option>
                    <option value="BOLO" className="bg-[#121217]">BOLO</option>
                    <option value="PAO_FRANCES" className="bg-[#121217]">PÃO</option>
                  </select>
                  {productToEdit.type === "PAO_FRANCES" && (
                    <span className="text-[10px] text-slate-500 mt-1 block">O tipo do pão especial não pode ser alterado.</span>
                  )}
                </div>
              </div>

              {/* Preços de Custo e Venda */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Preço de Custo (R$) *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0,00"
                    value={editPriceCost}
                    onChange={(e) => setEditPriceCost(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold text-emerald-400"
                  />
                </div>

                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Preço de Venda (R$) *
                  </label>
                  <input
                    type="text"
                    required
                    placeholder="0,00"
                    value={editPriceSale}
                    onChange={(e) => setEditPriceSale(e.target.value)}
                    className="w-full px-4 py-2.5 text-xs rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-bold text-amber-400"
                  />
                </div>
              </div>

              <div className="text-[10px] text-slate-500 italic pt-1">
                * As alterações de valores, códigos e nomes serão aplicadas imediatamente a toda a rede de lojas.
              </div>

              {/* Botões */}
              <div className="flex gap-3 pt-4 border-t border-white/5">
                <button
                  type="button"
                  onClick={() => {
                    setIsEditModalOpen(false);
                    setProductToEdit(null);
                  }}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={isPending}
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1.5"
                >
                  {isPending ? (
                    <Loader2 className="w-4 h-4 animate-spin text-black" />
                  ) : (
                    "Salvar Alterações"
                  )}
                </button>
              </div>

            </form>
          </div>
        </div>
      )}

    </div>
  );
}
