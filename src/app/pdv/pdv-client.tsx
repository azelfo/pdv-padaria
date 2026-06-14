"use client";

import { useState, useTransition, useMemo, useEffect, useRef, useCallback } from "react";
import { useRouter } from "next/navigation";
import { 
  Search, 
  ShoppingBag, 
  Trash2, 
  User, 
  Store as StoreIcon, 
  LogOut, 
  ChefHat, 
  Coins, 
  Flame, 
  Plus, 
  Minus,
  Sparkles,
  TicketPercent,
  CheckCircle2,
  Printer,
  ChevronRight,
  Loader2,
  QrCode,
  CreditCard,
  AlertTriangle,
  RotateCcw,
  Zap,
  BarChart3
} from "lucide-react";
import { toast } from "react-hot-toast";
import { createSaleAction, logoutAction, verifyAdminPasswordAction } from "./actions";

interface Product {
  id: string;
  name: string;
  barcode: string | null;
  priceSale: number;
  priceCost: number;
  type: string; // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
  unitMeasure: string;
  imageUrl: string | null;
  categoryName: string;
  stockQuantity: number;
  minStock: number;
}

interface CartItem {
  id: string; // id único gerado para instâncias do carrinho
  productId: string;
  name: string;
  quantity: number;
  priceUnit: number; // centavos cobrados
  subtotal: number; // centavos
  type: string;
  variation: "NORMAL" | "INTEIRO" | "FATIA";
  details?: string;
}

interface SessionData {
  id: string;
  name: string;
  email: string;
  role: string;
  tenantId: string;
  storeId: string | null;
  storeName: string | null;
}

interface SaleReceiptItemProduct {
  name: string;
  unitMeasure: string;
}

interface SaleReceiptItem {
  id: string;
  productId: string;
  quantity: number;
  priceUnit: number;
  subtotal: number;
  product?: SaleReceiptItemProduct;
  details?: string | null;
}

interface SaleReceiptStore {
  name: string;
  address: string;
  phone: string;
  cnpj: string;
}

interface SaleReceiptUser {
  name: string;
  role: string;
}

interface SaleReceipt {
  id: string;
  storeId: string;
  store?: SaleReceiptStore;
  userId: string;
  user?: SaleReceiptUser;
  tenantId: string;
  saleDate: string | Date;
  subtotal: number;
  discount: number;
  total: number;
  paymentMethod: "DINHEIRO" | "PIX" | "CARTAO_DEBITO" | "CARTAO_CREDITO";
  paymentStatus: "PENDENTE" | "APROVADO" | "NEGADO" | "CANCELADO";
  receivedAmount?: number | null;
  changeAmount?: number | null;
  notes?: string | null;
  items?: SaleReceiptItem[];
  externalTxId?: string | null;
  nsuTx?: string | null;
  receiptUrl?: string | null;
}

interface OfflineSale {
  id: string;
  items: {
    productId: string;
    name: string;
    quantity: number;
    priceUnit: number;
    subtotal: number;
    type: string;
    details?: string;
  }[];
  paymentMethod: "DINHEIRO" | "PIX" | "CARTAO_DEBITO" | "CARTAO_CREDITO";
  receivedAmount?: number;
  changeAmount?: number;
  discount?: number;
  notes?: string;
}

interface PdvClientProps {
  session: SessionData;
  products: Product[];
  breadConfig: {
    priceUnit: number;
    brackets: { ate: number; qtd: number }[];
  } | null;
}

export default function PdvClient({ session, products, breadConfig }: PdvClientProps) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  // Estados do Modo Kiosk (Caixa Protegido)
  const [isKioskMode, setIsKioskMode] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("pdv_kiosk_mode") === "true";
    }
    return false;
  });
  const [isUnlockModalOpen, setIsUnlockModalOpen] = useState(false);
  const [kioskPasswordInput, setKioskPasswordInput] = useState("");
  const [kioskActionType, setKioskActionType] = useState<"logout" | "change_store" | "admin_page" | "unlock_kiosk" | null>(null);
  const [adminTargetUrl, setAdminTargetUrl] = useState("");
  const [showKioskFullscreenOverlay, setShowKioskFullscreenOverlay] = useState<boolean>(() => {
    if (typeof window !== "undefined" && typeof document !== "undefined") {
      const kiosk = localStorage.getItem("pdv_kiosk_mode") === "true";
      return kiosk && !document.fullscreenElement;
    }
    return false;
  });

  // Estados de Contingência Offline & Produtos Locais
  const [localProducts, setLocalProducts] = useState<Product[]>(products);
  const [prevProducts, setPrevProducts] = useState<Product[]>(products);
  if (products !== prevProducts) {
    setPrevProducts(products);
    setLocalProducts(products);
  }
  const [isOnline, setIsOnline] = useState<boolean>(() => {
    if (typeof window !== "undefined") {
      return navigator.onLine;
    }
    return true;
  });

  // Estados principais
  const [cart, setCart] = useState<CartItem[]>([]);
  const [searchQuery, setSearchQuery] = useState("");
  const [selectedCategory, setSelectedCategory] = useState<string>("TODOS");
  const [discount, setDiscount] = useState<number>(0); // em centavos

  // Modais de Venda
  const [isBreadModalOpen, setIsBreadModalOpen] = useState(false);
  const [breadValueInput, setBreadValueInput] = useState("");
  
  const [isCheckoutOpen, setIsCheckoutOpen] = useState(false);
  const [receivedAmountInput, setReceivedAmountInput] = useState("");
  const [paymentMethod, setPaymentMethod] = useState<"DINHEIRO" | "PIX" | "CARTAO_DEBITO" | "CARTAO_CREDITO">("DINHEIRO");

  // Modal de Status Eletrônico (Fase 2)
  const [isWaitingPayment, setIsWaitingPayment] = useState(false);
  const [activeSaleId, setActiveSaleId] = useState<string | null>(null);
  const [electronicCheckoutUrl, setElectronicCheckoutUrl] = useState<string | null>(null);
  const [electronicStatus, setElectronicStatus] = useState<"PENDENTE" | "APROVADO" | "NEGADO">("PENDENTE");

  // Modal de Recibo
  const [isReceiptOpen, setIsReceiptOpen] = useState(false);
  const [receiptData, setReceiptData] = useState<SaleReceipt | null>(null);

  // Referência para o timer de Polling
  const pollingRef = useRef<NodeJS.Timeout | null>(null);

  // Monitora a conectividade com a internet em tempo real
  useEffect(() => {
    const handleOnline = () => {
      setIsOnline(true);
      toast.success("Conexão de internet restaurada! Sincronizando vendas locais...");
    };

    const handleOffline = () => {
      setIsOnline(false);
      toast.error("Conexão perdida! Modo offline ativado. Vendas em dinheiro ativas localmente.");
    };

    window.addEventListener("online", handleOnline);
    window.addEventListener("offline", handleOffline);

    return () => {
      window.removeEventListener("online", handleOnline);
      window.removeEventListener("offline", handleOffline);
    };
  }, []);

  // Executa a sincronização de vendas offline gravadas no localStorage
  const syncOfflineSales = useCallback(async () => {
    try {
      const offlineSalesStr = localStorage.getItem("pdv_offline_sales");
      if (!offlineSalesStr) return;

      const offlineSales = JSON.parse(offlineSalesStr) as OfflineSale[];
      if (offlineSales.length === 0) return;

      console.log(`[Offline Sync] Sincronizando ${offlineSales.length} vendas pendentes...`);
      toast.loading(`Sincronizando ${offlineSales.length} venda(s) offline com a nuvem...`, { id: "sync-toast" });

      let successCount = 0;
      const remainingSales: OfflineSale[] = [];

      for (const sale of offlineSales) {
        try {
          const result = await createSaleAction({
            items: sale.items,
            paymentMethod: sale.paymentMethod,
            receivedAmount: sale.receivedAmount,
            changeAmount: sale.changeAmount,
            discount: sale.discount,
            notes: sale.notes || "Venda Offline Sincronizada",
          });

          if (result.success) {
            successCount++;
          } else {
            remainingSales.push(sale);
            console.warn("[Offline Sync] Falha ao sincronizar venda individual:", result.error);
          }
        } catch (err) {
          remainingSales.push(sale);
          console.error("[Offline Sync] Erro de rede ao sincronizar venda individual:", err);
        }
      }

      toast.dismiss("sync-toast");

      if (successCount > 0) {
        toast.success(`Sucesso! ${successCount} venda(s) offline sincronizada(s) com a nuvem.`);
        router.refresh();
      }

      if (remainingSales.length > 0) {
        localStorage.setItem("pdv_offline_sales", JSON.stringify(remainingSales));
        toast.error(`${remainingSales.length} venda(s) offline falharam e continuarão salvas localmente.`);
      } else {
        localStorage.removeItem("pdv_offline_sales");
      }
    } catch (error) {
      console.error("[Offline Sync] Erro geral na sincronização:", error);
      toast.dismiss("sync-toast");
    }
  }, [router]);

  // Efeito reativo para disparar a sincronização quando a internet voltar
  useEffect(() => {
    if (isOnline) {
      syncOfflineSales();
    }
  }, [isOnline, syncOfflineSales]);

  // Funções de controle de tela cheia
  const enterFullscreen = () => {
    if (typeof document !== "undefined" && document.documentElement.requestFullscreen) {
      document.documentElement.requestFullscreen().catch((err) => {
        console.warn("Erro ao ativar tela cheia:", err);
      });
    }
  };

  const exitFullscreen = () => {
    if (typeof document !== "undefined" && document.fullscreenElement && document.exitFullscreen) {
      document.exitFullscreen().catch((err) => {
        console.warn("Erro ao sair de tela cheia:", err);
      });
    }
  };

  // Entra em tela cheia ao ativar/iniciar o kiosk
  useEffect(() => {
    if (isKioskMode) {
      enterFullscreen();
    }
  }, [isKioskMode]);

  // Impede o fechamento acidental da janela
  useEffect(() => {
    if (!isKioskMode) return;
    const handleBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "O Modo Kiosk está ativo neste terminal. Por favor, destrave o terminal para sair do aplicativo.";
      return e.returnValue;
    };
    window.addEventListener("beforeunload", handleBeforeUnload);
    return () => window.removeEventListener("beforeunload", handleBeforeUnload);
  }, [isKioskMode]);

  // Monitora a saída indesejada de tela cheia pelo usuário (ex: apertar Esc)
  useEffect(() => {
    if (!isKioskMode) return;

    const handleFullscreenChange = () => {
      if (!document.fullscreenElement) {
        setShowKioskFullscreenOverlay(true);
      } else {
        setShowKioskFullscreenOverlay(false);
      }
    };

    document.addEventListener("fullscreenchange", handleFullscreenChange);

    return () => {
      document.removeEventListener("fullscreenchange", handleFullscreenChange);
    };
  }, [isKioskMode]);

  // Bloqueia teclas de escape e navegação como F11 no Modo Kiosk
  useEffect(() => {
    if (!isKioskMode) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      // 1. Bloqueia F11 (tela cheia do navegador)
      if (e.key === "F11") {
        e.preventDefault();
        e.stopPropagation();
        toast.error("O atalho F11 está bloqueado no Modo Kiosk para segurança.", { id: "kiosk-f11-error" });
      }

      // 2. Bloqueia a tecla Windows / Meta
      if (e.key === "Meta" || e.key === "OS") {
        e.preventDefault();
      }
    };

    window.addEventListener("keydown", handleKeyDown, true); // capturing phase para máxima prioridade
    return () => {
      window.removeEventListener("keydown", handleKeyDown, true);
    };
  }, [isKioskMode]);

  // Executa ações com proteção do Modo Kiosk
  const handleProtectedAction = (type: "logout" | "change_store" | "admin_page" | "unlock_kiosk", targetUrl?: string) => {
    if (isKioskMode) {
      setKioskActionType(type);
      setAdminTargetUrl(targetUrl || "");
      setIsUnlockModalOpen(true);
    } else {
      if (type === "logout") {
        handleLogout();
      } else if (type === "change_store") {
        handleChangeStore();
      } else if (type === "admin_page" && targetUrl) {
        router.push(targetUrl);
      } else if (type === "unlock_kiosk") {
        // Ativa o kiosk
        setIsKioskMode(true);
        localStorage.setItem("pdv_kiosk_mode", "true");
        enterFullscreen();
        toast.success("Modo Kiosk (Caixa Protegido) ativado com sucesso! Terminal travado.");
      }
    }
  };

  // Confirma senha de administrador e executa a ação solicitada
  const handleUnlockSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!kioskPasswordInput) {
      toast.error("Por favor, digite a senha.");
      return;
    }

    try {
      const result = await verifyAdminPasswordAction(kioskPasswordInput);
      if (result.success) {
        toast.success(`Terminal liberado por: ${result.adminName}`);
        
        const previousAction = kioskActionType;
        const previousUrl = adminTargetUrl;

        // Limpa os estados do modal
        setIsUnlockModalOpen(false);
        setKioskPasswordInput("");
        setKioskActionType(null);
        setAdminTargetUrl("");

        // Se a intenção era destravar o kiosk em si (sair do modo kiosk)
        if (previousAction === "unlock_kiosk" || previousAction === null) {
          setIsKioskMode(false);
          localStorage.setItem("pdv_kiosk_mode", "false");
          exitFullscreen();
          setShowKioskFullscreenOverlay(false);
          toast.success("Modo Kiosk desativado!");
        } else {
          // Executa a ação bloqueada que o usuário havia tentado
          if (previousAction === "logout") {
            handleLogout();
          } else if (previousAction === "change_store") {
            handleChangeStore();
          } else if (previousAction === "admin_page" && previousUrl) {
            router.push(previousUrl);
          }
        }
      } else {
        toast.error(result.error || "Senha administrativa inválida.");
      }
    } catch (err) {
      console.error("Erro ao validar desbloqueio:", err);
      toast.error("Falha ao processar autenticação de desbloqueio.");
    }
  };

  // Categorias únicas do catálogo baseadas em localProducts
  const categories = useMemo(() => {
    const cats = new Set(localProducts.map((p) => p.categoryName));
    return ["TODOS", ...Array.from(cats)];
  }, [localProducts]);

  // Filtro de produtos com base em localProducts
  const filteredProducts = useMemo(() => {
    return localProducts.filter((p) => {
      const matchesSearch = 
        p.name.toLowerCase().includes(searchQuery.toLowerCase()) || 
        (p.barcode && p.barcode.includes(searchQuery));
      const matchesCategory = selectedCategory === "TODOS" || p.categoryName === selectedCategory;
      return matchesSearch && matchesCategory;
    });
  }, [localProducts, searchQuery, selectedCategory]);

  // Cálculo da quantidade de pães franceses
  const calculatedBreadInfo = useMemo(() => {
    if (!breadConfig || !breadValueInput) return { quantity: 0, text: "" };
    
    const valueCents = Math.round(parseFloat(breadValueInput.replace(",", ".")) * 100);
    if (isNaN(valueCents) || valueCents <= 0) return { quantity: 0, text: "" };

    const reaisInteiros = Math.floor(valueCents / 100);
    const centavosRestantes = valueCents % 100;

    const paesDoReais = reaisInteiros * 3;
    const paesDosCentavos = centavosRestantes >= breadConfig.priceUnit ? 1 : 0;

    const totalDePaes = paesDoReais + paesDosCentavos;

    let textDetail = "";
    if (reaisInteiros > 0) {
      textDetail += `${reaisInteiros}x R$1,00 (${paesDoReais} pães)`;
    }
    if (centavosRestantes > 0 && paesDosCentavos > 0) {
      if (textDetail) textDetail += " + ";
      textDetail += `R$0,50 (${paesDosCentavos} pão)`;
    }

    return {
      quantity: totalDePaes,
      text: textDetail || `${totalDePaes} pão(s)`
    };
  }, [breadValueInput, breadConfig]);

  // Totais do carrinho
  const totals = useMemo(() => {
    const subtotal = cart.reduce((sum, item) => sum + item.subtotal, 0);
    const total = Math.max(0, subtotal - discount);
    return { subtotal, total };
  }, [cart, discount]);

  // Troco calculado em tempo real (Dinheiro)
  const changeInfo = useMemo(() => {
    if (!receivedAmountInput) return 0;
    const receivedCents = Math.round(parseFloat(receivedAmountInput.replace(",", ".")) * 100);
    if (isNaN(receivedCents)) return 0;
    return Math.max(0, receivedCents - totals.total);
  }, [receivedAmountInput, totals.total]);

  // Formata valor em reais
  const formatCurrency = (cents: number) => {
    return new Intl.NumberFormat("pt-BR", {
      style: "currency",
      currency: "BRL",
    }).format(cents / 100);
  };

  // Adiciona produto normal ao carrinho
  const addToCart = (product: Product) => {
    if (product.type === "PAO_FRANCES") {
      setIsBreadModalOpen(true);
      return;
    }

    if (product.stockQuantity <= 0) {
      toast.error("Estoque indisponível para este produto.");
      return;
    }

    const cartItemId = `${product.id}-NORMAL`;
    let added = false;
    let limitReached = false;

    setCart((prev) => {
      const existing = prev.find((item) => item.id === cartItemId);

      if (existing) {
        if (existing.quantity + 1 > product.stockQuantity) {
          limitReached = true;
          return prev;
        }
        added = true;
        return prev.map((item) =>
          item.id === cartItemId
            ? { ...item, quantity: item.quantity + 1, subtotal: (item.quantity + 1) * item.priceUnit }
            : item
        );
      }

      added = true;
      return [
        ...prev,
        {
          id: cartItemId,
          productId: product.id,
          name: product.name,
          quantity: 1,
          priceUnit: product.priceSale,
          subtotal: product.priceSale,
          type: product.type,
          variation: "NORMAL",
        },
      ];
    });

    if (limitReached) {
      toast.error("Limite de estoque atingido para esta venda.");
    } else if (added) {
      toast.success(`${product.name} adicionado!`);
    }
  };

  // Adiciona pão francês calculado
  const handleAddBreadToCart = (product: Product) => {
    const valueCents = Math.round(parseFloat(breadValueInput.replace(",", ".")) * 100);
    if (isNaN(valueCents) || valueCents <= 0) {
      toast.error("Digite um valor válido.");
      return;
    }

    const { quantity, text } = calculatedBreadInfo;
    if (quantity <= 0) {
      toast.error("Valor insuficiente para comprar pães.");
      return;
    }

    const cartItemId = `${product.id}-${valueCents}`;
    let isUpdate = false;

    setCart((prev) => {
      const existingIndex = prev.findIndex((item) => item.id === cartItemId);

      if (existingIndex > -1) {
        isUpdate = true;
        return prev.map((item, idx) =>
          idx === existingIndex
            ? {
                ...item,
                quantity: quantity,
                priceUnit: Math.round(valueCents / quantity),
                subtotal: valueCents,
                details: `Pão - R$ ${breadValueInput} (${text})`
              }
            : item
        );
      }

      return [
        ...prev,
        {
          id: cartItemId,
          productId: product.id,
          name: `Pão - R$ ${breadValueInput}`,
          quantity: quantity,
          priceUnit: Math.round(valueCents / quantity),
          subtotal: valueCents,
          type: "PAO_FRANCES",
          variation: "NORMAL",
          details: `${quantity} pães calculados (${text})`
        },
      ];
    });

    if (isUpdate) {
      toast.success("Valor de pão atualizado no carrinho!");
    } else {
      toast.success(`Pão (R$ ${breadValueInput}) adicionado!`);
    }

    setBreadValueInput("");
    setIsBreadModalOpen(false);
  };

  // Altera quantidade diretamente no carrinho
  const updateCartItemQuantity = (cartItemId: string, change: number) => {
    setCart((prev) => {
      return prev.map((item) => {
        if (item.id !== cartItemId) return item;

        const newQty = Math.max(1, item.quantity + change);
        
        const p = localProducts.find((prod) => prod.id === item.productId);
        if (p && change > 0 && newQty > p.stockQuantity) {
          toast.error("Estoque insuficiente.");
          return item;
        }

        return {
          ...item,
          quantity: newQty,
          subtotal: newQty * item.priceUnit,
        };
      });
    });
  };

  // Remove item
  const removeCartItem = (cartItemId: string) => {
    setCart((prev) => prev.filter((item) => item.id !== cartItemId));
    toast.success("Item removido");
  };

  // Variação de Salgados e Bolos
  const handleVariationChange = (cartItem: CartItem, newVar: "NORMAL" | "INTEIRO" | "FATIA") => {
    const originalProduct = localProducts.find((p) => p.id === cartItem.productId);
    if (!originalProduct) return;

    setCart((prev) => {
      return prev.map((item) => {
        if (item.id !== cartItem.id) return item;

        let priceUnit = originalProduct.priceSale;
        let quantity = item.quantity;
        let details = "";

        if (originalProduct.type === "BOLO") {
          if (newVar === "INTEIRO") {
            priceUnit = 5500; // R$ 55,00 inteiro
            quantity = 1;
            details = "Bolo Inteiro (R$ 55,00)";
          } else {
            priceUnit = originalProduct.priceSale;
            quantity = 1;
            details = "Fatia de Bolo";
          }
        }

        return {
          ...item,
          variation: newVar,
          quantity,
          priceUnit,
          subtotal: quantity * priceUnit,
          details
        };
      });
    });

    toast.success(`Variação alterada no carrinho!`);
  };

  // Polling para checar status do pagamento eletrônico
  const checkPaymentStatus = async (saleId: string) => {
    try {
      const res = await fetch(`/api/payment/status?saleId=${saleId}`);
      if (!res.ok) return;

      const data = await res.json();
      if (data.success && data.status === "APROVADO") {
        clearInterval(pollingRef.current!);
        pollingRef.current = null;

        toast.success("Pagamento aprovado pela InfinitePay!");
        setReceiptData(data.receiptData);
        setIsWaitingPayment(false);
        setIsReceiptOpen(true);
        
        // Reseta o caixa
        setCart([]);
        setDiscount(0);
        setActiveSaleId(null);
        setElectronicCheckoutUrl(null);
      } else if (data.status === "NEGADO" || data.status === "CANCELADO") {
        clearInterval(pollingRef.current!);
        pollingRef.current = null;
        toast.error("O pagamento foi negado ou cancelado.");
        setIsWaitingPayment(false);
        setActiveSaleId(null);
      }
    } catch (err) {
      console.error("Erro no polling de pagamento:", err);
    }
  };

  // Limpa o timer quando o componente desmonta
  useEffect(() => {
    return () => {
      if (pollingRef.current) clearInterval(pollingRef.current);
    };
  }, []);

  // Força uma checagem manual
  const handleManualCheck = () => {
    if (activeSaleId) {
      toast.loading("Verificando status na InfinitePay...", { duration: 1000 });
      checkPaymentStatus(activeSaleId);
    }
  };

  // Cancela operação de pagamento pendente
  const handleCancelElectronicPayment = () => {
    if (pollingRef.current) {
      clearInterval(pollingRef.current);
      pollingRef.current = null;
    }
    setIsWaitingPayment(false);
    setActiveSaleId(null);
    setElectronicCheckoutUrl(null);
    toast.error("Operação cancelada pelo operador.");
  };

  // Simulação local de Webhook de aprovação (Efeito UAU para homologação fácil)
  const handleSimulatePaymentApproval = async () => {
    if (!activeSaleId) return;

    toast.loading("Simulando webhook de aprovação da InfinitePay...", { duration: 1500 });

    try {
      const res = await fetch("/api/webhook/infinitepay", {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          order_nsu: activeSaleId,
          transaction_nsu: `MOCK-NSU-${Math.floor(100000 + Math.random() * 900000)}`,
          paid_amount: totals.total,
          capture_method: paymentMethod === "PIX" ? "pix" : "card",
          receipt_url: "https://comprovante.infinitepay.io/simulated-homologation",
        }),
      });

      if (res.ok) {
        // O webhook local processa no banco. O polling ativo de 3s detectará a mudança de imediato,
        // mas também podemos forçar uma verificação rápida para acelerar o feedback visual!
        setTimeout(() => {
          if (activeSaleId) checkPaymentStatus(activeSaleId);
        }, 800);
      } else {
        toast.error("Erro ao simular webhook no servidor.");
      }
    } catch (err) {
      console.error("Erro na simulação do webhook:", err);
      toast.error("Falha de conexão ao simular webhook.");
    }
  };

  // Finalização e Fechamento de Venda (Server Action)
  const handleCheckoutSubmit = () => {
    if (cart.length === 0) {
      toast.error("Seu carrinho está vazio.");
      return;
    }

    const isElectronic = paymentMethod === "PIX" || paymentMethod === "CARTAO_DEBITO" || paymentMethod === "CARTAO_CREDITO";
    
    let receivedCents = 0;
    let changeCents = 0;

    if (paymentMethod === "DINHEIRO") {
      if (!receivedAmountInput) {
        toast.error("Digite o valor recebido.");
        return;
      }
      receivedCents = Math.round(parseFloat(receivedAmountInput.replace(",", ".")) * 100);
      if (isNaN(receivedCents) || receivedCents < totals.total) {
        toast.error("Valor recebido é menor que o total da compra.");
        return;
      }
      changeCents = receivedCents - totals.total;
    }

    startTransition(async () => {
      const itemsInput = cart.map((item) => ({
        productId: item.productId,
        name: item.name,
        quantity: item.quantity,
        priceUnit: item.priceUnit,
        subtotal: item.subtotal,
        type: item.type,
        details: item.details,
      }));

      // Lógica Offline-First: desvio preventivo em caso de perda de internet detectada
      if (!isOnline) {
        if (isElectronic) {
          toast.error("Pagamentos eletrônicos indisponíveis no modo offline. Por favor, finalize em Dinheiro.");
          return;
        }

        const offlineSaleId = `off_${Math.random().toString(36).substring(2, 11)}`;
        const localSale = {
          id: offlineSaleId,
          items: itemsInput,
          paymentMethod: paymentMethod,
          receivedAmount: receivedCents > 0 ? receivedCents : totals.total,
          changeAmount: changeCents,
          discount: discount,
          notes: "Venda Híbrida Offline (Gravada no Navegador)",
        };

        try {
          const existingOfflineStr = localStorage.getItem("pdv_offline_sales");
          const existingOffline = existingOfflineStr ? JSON.parse(existingOfflineStr) : [];
          existingOffline.push(localSale);
          localStorage.setItem("pdv_offline_sales", JSON.stringify(existingOffline));

          // Atualiza reativamente o estoque local em memória do navegador
          setLocalProducts((prev) =>
            prev.map((prod) => {
              const item = itemsInput.find((it) => it.productId === prod.id);
              return item ? { ...prod, stockQuantity: Math.max(0, prod.stockQuantity - item.quantity) } : prod;
            })
          );

          // Renderiza o recibo digital offline simulando dados da API
          const mockReceiptData: SaleReceipt = {
            id: offlineSaleId,
            storeId: session.storeId || "",
            store: {
              name: session.storeName || "Filial Local",
              address: "Venda Offline (Local)",
              phone: "-",
              cnpj: "-",
            },
            userId: session.id,
            user: {
              name: session.name,
              role: session.role,
            },
            tenantId: session.tenantId,
            saleDate: new Date().toISOString(),
            subtotal: totals.subtotal,
            discount: discount,
            total: totals.total,
            paymentMethod: paymentMethod,
            paymentStatus: "APROVADO",
            receivedAmount: receivedCents > 0 ? receivedCents : totals.total,
            changeAmount: changeCents,
            notes: "Venda Offline. Será sincronizada automaticamente ao restabelecer a internet.",
            items: itemsInput.map((it, idx) => ({
              id: `item_off_${idx}`,
              productId: it.productId,
              product: { name: it.name, unitMeasure: "UN" },
              quantity: it.quantity,
              priceUnit: it.priceUnit,
              subtotal: it.subtotal,
              type: it.type,
              details: it.details,
            })),
          };

          toast.success("Caixa offline operante! Venda gravada localmente com sucesso.");
          setReceiptData(mockReceiptData);
          setIsCheckoutOpen(false);
          setIsReceiptOpen(true);
          setCart([]);
          setDiscount(0);
          setReceivedAmountInput("");
        } catch (storageErr) {
          console.error("[Offline Save Error] Falha ao gravar no localStorage:", storageErr);
          toast.error("Falha ao gravar venda offline no armazenamento local.");
        }
        return;
      }

      // Fluxo Online Tradicional
      try {
        const result = await createSaleAction({
          items: itemsInput,
          paymentMethod: paymentMethod,
          receivedAmount: receivedCents > 0 ? receivedCents : totals.total,
          changeAmount: changeCents,
          discount: discount,
        });

        if (result.success && result.saleId) {
          if (!isElectronic) {
            // FLUXO DINHEIRO: Concluído direto
            toast.success("Venda finalizada com sucesso!");
            setReceiptData(result.receiptData as SaleReceipt);
            setIsCheckoutOpen(false);
            setIsReceiptOpen(true);
            setCart([]);
            setDiscount(0);
            setReceivedAmountInput("");
          } else {
            // FLUXO ELETRÔNICO (Fase 2 - Pix / Cartão):
            // 2. Chama a API Route para obter cobrança/deeplink da InfinitePay
            try {
              const payRes = await fetch("/api/payment/create", {
                method: "POST",
                headers: {
                  "Content-Type": "application/json",
                },
                body: JSON.stringify({
                  saleId: result.saleId,
                  total: totals.total,
                  paymentMethod: paymentMethod,
                }),
              });

              if (payRes.ok) {
                const payData = await payRes.json();
                
                setActiveSaleId(result.saleId);
                setElectronicCheckoutUrl(payData.checkoutUrl);
                setIsCheckoutOpen(false);
                setIsWaitingPayment(true);

                // 3. Dispara Polling a cada 3 segundos para aguardar o Webhook de aprovação
                pollingRef.current = setInterval(() => {
                  checkPaymentStatus(result.saleId!);
                }, 3000);

                toast.success("Cobrança gerada na InfinitePay!");

                // Fluxo especial de Cartão via Tap/Deeplink:
                if (paymentMethod === "CARTAO_DEBITO" || paymentMethod === "CARTAO_CREDITO") {
                  const handle = session.storeName ? "padaria-ouro" : "test";
                  const methodParam = paymentMethod === "CARTAO_CREDITO" ? "credit" : "debit";
                  const deeplink = `infinitepay://payment?handle=${handle}&amount=${totals.total}&payment_method=${methodParam}&order_id=${result.saleId}`;
                  console.log("[InfiniteTap] Abrindo Deeplink:", deeplink);
                  
                  if (navigator.userAgent.match(/Android|iPhone|iPad/i)) {
                    window.location.href = deeplink;
                  }
                }
              } else {
                toast.error("Erro na comunicação com a API da InfinitePay.");
              }
            } catch (payErr) {
              console.error(payErr);
              toast.error("Falha ao comunicar com a InfinitePay.");
            }
          }
        } else {
          toast.error(result.error || "Ocorreu um erro ao registrar a venda.");
        }
      } catch (networkError) {
        // Fallback de contingência caso a chamada de rede online lance exceção de rede de repente!
        console.warn("[Network Error] Falha de conexão na Server Action, desviando para offline:", networkError);
        
        if (isElectronic) {
          toast.error("Erro de rede! Pagamentos eletrônicos exigem conexão. Por favor, selecione Dinheiro.");
          return;
        }

        // Salva localmente em contingência de emergência
        const offlineSaleId = `off_net_${Math.random().toString(36).substring(2, 11)}`;
        const localSale = {
          id: offlineSaleId,
          items: itemsInput,
          paymentMethod: paymentMethod,
          receivedAmount: receivedCents > 0 ? receivedCents : totals.total,
          changeAmount: changeCents,
          discount: discount,
          notes: "Venda Híbrida Offline (Recuperada de Falha de Rede)",
        };

        try {
          const existingOfflineStr = localStorage.getItem("pdv_offline_sales");
          const existingOffline = existingOfflineStr ? JSON.parse(existingOfflineStr) : [];
          existingOffline.push(localSale);
          localStorage.setItem("pdv_offline_sales", JSON.stringify(existingOffline));

          // Baixa o estoque reativamente
          setLocalProducts((prev) =>
            prev.map((prod) => {
              const item = itemsInput.find((it) => it.productId === prod.id);
              return item ? { ...prod, stockQuantity: Math.max(0, prod.stockQuantity - item.quantity) } : prod;
            })
          );

          const mockReceiptData: SaleReceipt = {
            id: offlineSaleId,
            storeId: session.storeId || "",
            store: {
              name: session.storeName || "Filial Local",
              address: "Venda Offline (Local)",
              phone: "-",
              cnpj: "-",
            },
            userId: session.id,
            user: {
              name: session.name,
              role: session.role,
            },
            tenantId: session.tenantId,
            saleDate: new Date().toISOString(),
            subtotal: totals.subtotal,
            discount: discount,
            total: totals.total,
            paymentMethod: paymentMethod,
            paymentStatus: "APROVADO",
            receivedAmount: receivedCents > 0 ? receivedCents : totals.total,
            changeAmount: changeCents,
            notes: "Conexão instável. Venda offline gravada com sucesso e protegida contra quedas.",
            items: itemsInput.map((it, idx) => ({
              id: `item_off_${idx}`,
              productId: it.productId,
              product: { name: it.name, unitMeasure: "UN" },
              quantity: it.quantity,
              priceUnit: it.priceUnit,
              subtotal: it.subtotal,
              type: it.type,
              details: it.details,
            })),
          };

          toast.success("Queda de sinal detectada! Venda protegida e gravada localmente.");
          setReceiptData(mockReceiptData);
          setIsCheckoutOpen(false);
          setIsReceiptOpen(true);
          setCart([]);
          setDiscount(0);
          setReceivedAmountInput("");
        } catch (storageErr) {
          console.error("[Offline Save Error] Falha ao gravar no localStorage:", storageErr);
          toast.error("Falha ao gravar venda offline no armazenamento local.");
        }
      }
    });
  };

  const handleLogout = async () => {
    await logoutAction();
    router.push("/login");
    toast.success("Caixa fechado com sucesso!");
  };

  const handleChangeStore = () => {
    router.push("/store-select");
  };

  return (
    <div className="min-h-screen flex flex-col bg-[#050507] text-slate-100">
      
      {/* HEADER PRINCIPAL */}
      <header className="glass border-b border-white/5 py-4 px-6 flex flex-col md:flex-row md:items-center md:justify-between gap-4">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-tr from-amber-500 to-orange-600 flex items-center justify-center shadow-lg shadow-amber-500/10 animate-pulse-subtle">
            <ChefHat className="w-5 h-5 text-black" />
          </div>
          <div>
            <h1 className="text-lg font-bold tracking-tight bg-gradient-to-r from-amber-300 to-orange-400 bg-clip-text text-transparent">
              PADARIA
            </h1>
            <div className="flex items-center gap-2 text-slate-400 text-xs mt-0.5 font-medium">
              <StoreIcon className="w-3.5 h-3.5 text-amber-500/80" />
              <span>{session.storeName || "Operando Geral"}</span>
              <span className="w-1 h-1 rounded-full bg-slate-600"></span>
              <User className="w-3.5 h-3.5 text-amber-500/80" />
              <span>{session.name} ({session.role})</span>
              <span className="w-1 h-1 rounded-full bg-slate-600"></span>
              {isOnline ? (
                <span className="text-[10px] bg-emerald-500/10 border border-emerald-500/20 text-emerald-400 font-extrabold px-2 py-0.5 rounded-xl flex items-center gap-1 select-none shrink-0">
                  <span className="w-1.5 h-1.5 rounded-full bg-emerald-400"></span>
                  🟢 SINCRONIZADO
                </span>
              ) : (
                <span className="text-[10px] bg-red-500/10 border border-red-500/20 text-red-400 font-extrabold px-2 py-0.5 rounded-xl flex items-center gap-1 animate-pulse select-none shrink-0">
                  <span className="w-1.5 h-1.5 rounded-full bg-red-500"></span>
                  🔴 CAIXA OFFLINE
                </span>
              )}
            </div>
          </div>
        </div>

        {/* Ações de Caixa */}
        <div className="flex items-center gap-3 self-end md:self-auto">
          {/* Botão do Modo Kiosk (Caixa Protegido) */}
          <button
            onClick={() => handleProtectedAction("unlock_kiosk")}
            className={`flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider transition cursor-pointer ${
              isKioskMode
                ? "bg-amber-500/10 border border-amber-500/30 text-amber-400 hover:bg-amber-500/20 animate-pulse-subtle"
                : "bg-white/[0.02] border border-white/5 text-slate-400 hover:bg-white/5 hover:text-amber-400"
            }`}
            title={
              isKioskMode
                ? "Terminal Protegido. Clique para desativar (Requer senha de Administrador)"
                : "Ativar Modo Caixa Protegido (Trava o terminal em tela cheia e impede saídas)"
            }
          >
            {isKioskMode ? (
              <>
                <span className="w-1.5 h-1.5 rounded-full bg-amber-400 animate-ping"></span>
                🔒 Caixa Protegido
              </>
            ) : (
              <>
                <span>🔓</span>
                Modo Kiosk
              </>
            )}
          </button>

          {(session.role === "DONO" || session.role === "GERENTE") && (
            <button
              onClick={() => handleProtectedAction("admin_page", "/pdv/estoque")}
              className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer"
            >
              <ShoppingBag className="w-4 h-4" />
              Estoque
            </button>
          )}

          {session.role === "DONO" && (
            <button
              onClick={() => handleProtectedAction("admin_page", "/pdv/dashboard")}
              className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer"
            >
              <BarChart3 className="w-4 h-4" />
              Dashboard
            </button>
          )}

          {session.role === "DONO" && (
            <button
              onClick={() => handleProtectedAction("admin_page", "/admin/users")}
              className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer"
            >
              <User className="w-4 h-4" />
              Funcionários
            </button>
          )}

          {session.role === "DONO" && (
            <button
              onClick={() => handleProtectedAction("change_store")}
              className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-white/[0.02] border border-white/5 text-slate-300 hover:bg-white/5 hover:text-amber-400 transition cursor-pointer"
            >
              <StoreIcon className="w-4 h-4" />
              Trocar Loja
            </button>
          )}

          <button
            onClick={() => handleProtectedAction("logout")}
            className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-red-500/10 border border-red-500/20 text-red-400 hover:bg-red-500/20 transition cursor-pointer"
          >
            <LogOut className="w-4 h-4" />
            Fechar Caixa
          </button>
        </div>
      </header>

      {/* PAINEL CENTRAL DO PDV */}
      <main className="flex-1 grid grid-cols-1 lg:grid-cols-12 overflow-hidden h-[calc(100vh-73px)]">
        
        {/* COLUNA ESQUERDA: CATÁLOGO E BUSCA */}
        <section className="lg:col-span-7 flex flex-col p-6 overflow-y-auto border-r border-white/5">
          
          {/* Barra de Busca e Atalho Pão */}
          <div className="grid grid-cols-1 sm:grid-cols-12 gap-4 mb-6">
            <div className="sm:col-span-8 relative">
              <span className="absolute inset-y-0 left-0 pl-3.5 flex items-center text-slate-500 pointer-events-none">
                <Search className="w-4 h-4" />
              </span>
              <input
                type="text"
                placeholder="Busque por nome ou bipe o código de barras..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="w-full pl-10 pr-4 py-3.5 text-sm rounded-2xl glass-input text-slate-100 placeholder-slate-500"
              />
            </div>
            
            <button
              onClick={() => setIsBreadModalOpen(true)}
              className="sm:col-span-4 flex items-center justify-center gap-2 py-3.5 px-4 rounded-2xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-sm hover:from-amber-400 hover:to-orange-400 transition-all duration-300 shadow-lg shadow-amber-500/10 cursor-pointer select-none active:scale-[0.98] animate-pulse-subtle"
            >
              <span>🍞</span>
              Pão R$
            </button>
          </div>

          {/* Abas de Categorias */}
          <div className="flex items-center gap-2 overflow-x-auto pb-4 mb-6 border-b border-white/5 no-scrollbar">
            {categories.map((cat) => {
              const isSelected = selectedCategory === cat;
              return (
                <button
                  key={cat}
                  onClick={() => setSelectedCategory(cat)}
                  className={`px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider whitespace-nowrap transition cursor-pointer ${
                    isSelected
                      ? "bg-amber-500 text-black shadow-md shadow-amber-500/10"
                      : "bg-white/[0.02] border border-white/5 text-slate-400 hover:text-slate-200"
                  }`}
                >
                  {cat}
                </button>
              );
            })}
          </div>

          {/* Grid de Produtos */}
          <div className="grid grid-cols-2 sm:grid-cols-3 gap-4">
            {filteredProducts.map((p) => {
              const isLowStock = p.stockQuantity <= p.minStock && p.stockQuantity > 0;
              const isOutStock = p.stockQuantity <= 0;

              return (
                <div
                  key={p.id}
                  onClick={() => !isOutStock && addToCart(p)}
                  className={`group relative glass rounded-2xl p-4 flex flex-col justify-between h-[155px] cursor-pointer glass-hover select-none ${
                    isOutStock ? "opacity-40 cursor-not-allowed" : ""
                  }`}
                >
                  <div>
                    <div className="flex items-start justify-between gap-1 mb-1">
                      <span className="text-[10px] font-bold text-slate-500 uppercase tracking-wider bg-white/[0.03] px-2 py-0.5 rounded-md border border-white/5">
                        {p.categoryName}
                      </span>

                      {isOutStock ? (
                        <span className="text-[9px] font-extrabold text-red-500 bg-red-500/10 border border-red-500/20 px-1.5 py-0.5 rounded">
                          ESGOTADO
                        </span>
                      ) : isLowStock ? (
                        <span className="text-[9px] font-extrabold text-orange-400 bg-orange-400/10 border border-orange-400/20 px-1.5 py-0.5 rounded">
                          BAIXO
                        </span>
                      ) : (
                        <span className="text-[9px] font-bold text-slate-400">
                          Est: {p.stockQuantity} {p.unitMeasure}
                        </span>
                      )}
                    </div>

                    <h4 className="text-sm font-bold text-slate-200 group-hover:text-amber-400 transition-colors line-clamp-2 mt-2">
                      {p.name}
                    </h4>
                  </div>

                  <div className="flex items-center justify-between mt-3 pt-2 border-t border-white/5">
                    <span className="text-base font-extrabold text-slate-100 group-hover:text-amber-300">
                      {formatCurrency(p.priceSale)}
                    </span>
                    <span className="text-[10px] text-slate-500 font-semibold uppercase">
                      /{p.unitMeasure}
                    </span>
                  </div>
                </div>
              );
            })}

            {filteredProducts.length === 0 && (
              <div className="col-span-full py-16 text-center text-slate-500 font-medium">
                Nenhum produto encontrado neste filtro.
              </div>
            )}
          </div>
        </section>

        {/* COLUNA DIREITA: CARRINHO E TOTALIZADOR */}
        <section className="lg:col-span-5 flex flex-col justify-between bg-black/25">
          <div className="p-6 flex flex-col flex-1 overflow-y-auto border-b border-white/5">
            <div className="flex items-center justify-between mb-5">
              <div className="flex items-center gap-2">
                <ShoppingBag className="w-5 h-5 text-amber-500 animate-float" />
                <h3 className="font-bold text-slate-200">Carrinho de Compras</h3>
              </div>
              <span className="text-xs font-bold bg-white/[0.04] px-2.5 py-1 rounded-full text-slate-400 border border-white/5">
                {cart.length} itens
              </span>
            </div>

            <div className="space-y-4 flex-1">
              {cart.map((item) => {
                return (
                  <div 
                    key={item.id}
                    className="glass rounded-2xl p-4 flex flex-col justify-between gap-3 relative overflow-hidden group/item"
                  >
                    <div className="flex items-start justify-between gap-4">
                      <div>
                        <h4 className="text-sm font-bold text-slate-200">{item.name}</h4>
                        <div className="flex items-center gap-2 text-xs text-slate-500 mt-1">
                          <span>{formatCurrency(item.priceUnit)} unid.</span>
                          {item.details && (
                            <>
                              <span className="w-1 h-1 rounded-full bg-slate-700"></span>
                              <span className="text-amber-500/80 font-medium">{item.details}</span>
                            </>
                          )}
                        </div>
                      </div>

                      <button
                        onClick={() => removeCartItem(item.id)}
                        className="text-slate-500 hover:text-red-400 p-1.5 rounded-lg hover:bg-red-500/10 transition cursor-pointer self-start shrink-0"
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
                    </div>

                    <div className="flex items-center justify-between pt-2 border-t border-white/5">
                      {item.type === "BOLO" ? (
                        <div className="flex bg-white/[0.02] border border-white/5 rounded-lg p-0.5">
                          <button
                            onClick={() => handleVariationChange(item, "NORMAL")}
                            type="button"
                            className={`px-2 py-1 rounded-md text-[10px] font-bold uppercase transition cursor-pointer ${
                              item.variation === "NORMAL"
                                ? "bg-amber-500 text-black"
                                : "text-slate-400 hover:text-slate-200"
                            }`}
                          >
                            Fatia
                          </button>
                          <button
                            onClick={() => handleVariationChange(item, "INTEIRO")}
                            type="button"
                            className={`px-2 py-1 rounded-md text-[10px] font-bold uppercase transition cursor-pointer ${
                              item.variation === "INTEIRO"
                                ? "bg-amber-500 text-black animate-pulse"
                                : "text-slate-400 hover:text-slate-200"
                            }`}
                          >
                            Bolo Inteiro
                          </button>
                        </div>
                      ) : (
                        <div className="text-[10px] text-slate-500 font-bold uppercase tracking-wider">
                          Qtd: {item.quantity}
                        </div>
                      )}

                      {item.type !== "PAO_FRANCES" ? (
                        <div className="flex items-center gap-1.5 bg-white/[0.02] border border-white/5 rounded-xl p-0.5">
                          <button
                            onClick={() => updateCartItemQuantity(item.id, -1)}
                            type="button"
                            className="w-7 h-7 flex items-center justify-center text-slate-400 hover:text-slate-200 hover:bg-white/5 rounded-lg transition cursor-pointer"
                          >
                            <Minus className="w-3.5 h-3.5" />
                          </button>
                          <span className="text-sm font-extrabold px-2 min-w-[20px] text-center text-slate-200">
                            {item.quantity}
                          </span>
                          <button
                            onClick={() => updateCartItemQuantity(item.id, 1)}
                            type="button"
                            className="w-7 h-7 flex items-center justify-center text-slate-400 hover:text-slate-200 hover:bg-white/5 rounded-lg transition cursor-pointer"
                          >
                            <Plus className="w-3.5 h-3.5" />
                          </button>
                        </div>
                      ) : null}

                      <span className="text-sm font-black text-slate-200 self-center">
                        {formatCurrency(item.subtotal)}
                      </span>
                    </div>
                  </div>
                );
              })}

              {cart.length === 0 && (
                <div className="flex flex-col items-center justify-center h-full text-slate-600 py-16 gap-3 select-none">
                  <ShoppingBag className="w-12 h-12 stroke-[1.2]" />
                  <span className="text-sm font-semibold">O caixa está vazio</span>
                </div>
              )}
            </div>
          </div>

          {/* TOTALIZADOR E FINALIZADOR */}
          <div className="p-6 bg-[#09090c] border-t border-white/5">
            <div className="space-y-3 mb-6 text-sm font-medium">
              <div className="flex items-center justify-between text-slate-400">
                <span>Subtotal</span>
                <span>{formatCurrency(totals.subtotal)}</span>
              </div>
              
              <div className="flex items-center justify-between text-slate-400">
                <div className="flex items-center gap-1">
                  <span>Desconto</span>
                  <button 
                    onClick={() => {
                      const desc = prompt("Valor de Desconto em R$:");
                      if (desc) {
                        const val = Math.round(parseFloat(desc.replace(",", ".")) * 100);
                        if (!isNaN(val) && val >= 0) {
                          if (val > totals.subtotal) {
                            toast.error("Desconto maior que o subtotal!");
                            return;
                          }
                          setDiscount(val);
                          toast.success("Desconto aplicado!");
                        }
                      }
                    }}
                    className="text-amber-500 hover:text-amber-400 text-xs underline cursor-pointer"
                  >
                    (ajustar)
                  </button>
                </div>
                <span className="text-red-400">-{formatCurrency(discount)}</span>
              </div>

              <div className="flex items-center justify-between pt-3 border-t border-white/5">
                <span className="text-base font-extrabold text-slate-200">TOTAL DA VENDA</span>
                <span className="text-2xl font-black bg-gradient-to-r from-amber-300 via-amber-400 to-orange-500 bg-clip-text text-transparent drop-shadow-md animate-pulse-subtle">
                  {formatCurrency(totals.total)}
                </span>
              </div>
            </div>

            <button
              onClick={() => cart.length > 0 && setIsCheckoutOpen(true)}
              disabled={cart.length === 0 || isPending}
              className="w-full py-4 rounded-2xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold hover:from-amber-400 hover:to-orange-400 transition-all duration-300 shadow-xl shadow-amber-500/10 cursor-pointer disabled:opacity-50 select-none active:scale-[0.99] flex items-center justify-center gap-2"
            >
              <Coins className="w-5 h-5 text-black" />
              FINALIZAR VENDA (F8)
            </button>
          </div>
        </section>
      </main>

      {/* 1. MODAL DO PÃO */}
      {isBreadModalOpen && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-md relative overflow-hidden">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-3">
              <span>🍞</span>
              Venda Especial de Pão
            </h3>
            
            <p className="text-slate-400 text-xs mb-6">
              Digite o valor total em reais pedido pelo cliente. O sistema calculará a quantidade e aplicará a promoção.
            </p>

            <div className="space-y-6">
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Valor Pago (R$)
                </label>
                <div className="relative">
                  <span className="absolute inset-y-0 left-0 pl-4 flex items-center text-lg font-black text-amber-500 pointer-events-none">
                    R$
                  </span>
                  <input
                    type="text"
                    required
                    autoFocus
                    placeholder="0,00"
                    value={breadValueInput}
                    onChange={(e) => setBreadValueInput(e.target.value)}
                    className="w-full pl-12 pr-4 py-4 text-3xl font-black rounded-xl glass-input text-slate-100 placeholder-slate-700 focus:outline-none"
                  />
                </div>
              </div>

              <div className="grid grid-cols-4 gap-2">
                {["0,50", "1,00", "1,50", "2,00", "2,50", "3,00", "5,00", "10,00"].map((val) => (
                  <button
                    key={val}
                    onClick={() => setBreadValueInput(val)}
                    className="py-2 rounded-lg glass-input text-xs font-bold text-slate-300 hover:text-amber-400 hover:bg-white/5 transition cursor-pointer"
                  >
                    R$ {val}
                  </button>
                ))}
              </div>

              {calculatedBreadInfo.quantity > 0 && (
                <div className="glass-accent rounded-2xl p-4 flex flex-col gap-1.5">
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-slate-400 font-semibold">Quantidade Estimada:</span>
                    <span className="text-xl font-black text-amber-400">
                      {calculatedBreadInfo.quantity} pães
                    </span>
                  </div>
                  <span className="text-[10px] text-slate-500 font-medium">
                    Detalhe: {calculatedBreadInfo.text}
                  </span>
                </div>
              )}

              <div className="flex gap-3 pt-2">
                <button
                  onClick={() => {
                    setIsBreadModalOpen(false);
                    setBreadValueInput("");
                  }}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  onClick={() => {
                    const p = localProducts.find((prod) => prod.type === "PAO_FRANCES");
                    if (p) handleAddBreadToCart(p);
                  }}
                  disabled={calculatedBreadInfo.quantity <= 0}
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer disabled:opacity-40"
                >
                  Confirmar e Adicionar
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* 2. MODAL DE CHECKOUT (Fase 2 - Pix e Cartão Habilitados) */}
      {isCheckoutOpen && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-lg relative overflow-hidden">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-4">
              <Coins className="w-5 h-5 text-amber-500 animate-float" />
              Finalizar Venda - Selecionar Pagamento
            </h3>

            <div className="space-y-6">
              
              {/* Formas de Pagamento Eletrônicas e Físicas */}
              <div>
                <span className="block text-[10px] font-bold text-slate-500 uppercase tracking-wider mb-2.5">
                  Escolha o método de pagamento
                </span>
                <div className="grid grid-cols-2 sm:grid-cols-4 gap-2.5">
                  <button
                    onClick={() => setPaymentMethod("DINHEIRO")}
                    className={`py-3.5 px-1 rounded-xl border font-bold text-xs transition cursor-pointer flex flex-col items-center justify-center gap-1.5 ${
                      paymentMethod === "DINHEIRO"
                        ? "border-amber-500 bg-amber-500/5 text-amber-400 shadow-lg shadow-amber-500/5"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <span>💵</span>
                    Dinheiro
                  </button>
                  
                  <button
                    onClick={() => setPaymentMethod("PIX")}
                    className={`py-3.5 px-1 rounded-xl border font-bold text-xs transition cursor-pointer flex flex-col items-center justify-center gap-1.5 ${
                      paymentMethod === "PIX"
                        ? "border-amber-500 bg-amber-500/5 text-amber-400 shadow-lg shadow-amber-500/5"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <span>⚡</span>
                    Pix (Infinite)
                  </button>

                  <button
                    onClick={() => setPaymentMethod("CARTAO_DEBITO")}
                    className={`py-3.5 px-1 rounded-xl border font-bold text-xs transition cursor-pointer flex flex-col items-center justify-center gap-1.5 ${
                      paymentMethod === "CARTAO_DEBITO"
                        ? "border-amber-500 bg-amber-500/5 text-amber-400 shadow-lg shadow-amber-500/5"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <span>💳</span>
                    Débito (Tap)
                  </button>

                  <button
                    onClick={() => setPaymentMethod("CARTAO_CREDITO")}
                    className={`py-3.5 px-1 rounded-xl border font-bold text-xs transition cursor-pointer flex flex-col items-center justify-center gap-1.5 ${
                      paymentMethod === "CARTAO_CREDITO"
                        ? "border-amber-500 bg-amber-500/5 text-amber-400 shadow-lg shadow-amber-500/5"
                        : "border-white/5 bg-white/[0.02] text-slate-400 hover:text-slate-200"
                    }`}
                  >
                    <span>💳</span>
                    Crédito (Tap)
                  </button>
                </div>
              </div>

              {/* Totalizador no Checkout */}
              <div className="flex items-center justify-between p-4 rounded-2xl bg-white/[0.02] border border-white/5">
                <span className="text-xs text-slate-400 font-semibold uppercase tracking-wider">Total a Receber</span>
                <span className="text-2xl font-black text-amber-400">{formatCurrency(totals.total)}</span>
              </div>

              {/* Lógica Dinheiro */}
              {paymentMethod === "DINHEIRO" && (
                <div className="space-y-4">
                  <div>
                    <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                      Valor Entregue pelo Cliente (R$)
                    </label>
                    <div className="relative">
                      <span className="absolute inset-y-0 left-0 pl-4 flex items-center text-lg font-black text-slate-400 pointer-events-none">
                        R$
                      </span>
                      <input
                        type="text"
                        required
                        autoFocus
                        placeholder="0,00"
                        value={receivedAmountInput}
                        onChange={(e) => setReceivedAmountInput(e.target.value)}
                        className="w-full pl-12 pr-4 py-3.5 text-2xl font-black rounded-xl glass-input text-slate-100 placeholder-slate-700 focus:outline-none"
                      />
                    </div>
                  </div>

                  <div>
                    <span className="block text-[10px] font-bold text-slate-500 uppercase tracking-wider mb-2">
                      Notas Rápidas
                    </span>
                    <div className="flex gap-2">
                      <button
                        onClick={() => setReceivedAmountInput((totals.total / 100).toFixed(2))}
                        className="flex-1 py-2 px-1.5 rounded-lg glass-input text-[11px] font-bold text-amber-500 hover:bg-white/5 transition cursor-pointer"
                      >
                        Exato
                      </button>
                      {[10, 20, 50, 100].map((nota) => (
                        <button
                          key={nota}
                          onClick={() => setReceivedAmountInput(nota.toFixed(2))}
                          className="flex-1 py-2 px-1.5 rounded-lg glass-input text-[11px] font-bold text-slate-300 hover:bg-white/5 transition cursor-pointer"
                        >
                          R$ {nota},00
                        </button>
                      ))}
                    </div>
                  </div>

                  {changeInfo > 0 && (
                    <div className="bg-emerald-500/5 border border-emerald-500/15 rounded-2xl p-4 flex items-center justify-between">
                      <span className="text-xs text-slate-400 font-semibold uppercase tracking-wider">Troco Devolver:</span>
                      <span className="text-xl font-black text-emerald-400 animate-pulse">
                        {formatCurrency(changeInfo)}
                      </span>
                    </div>
                  )}
                </div>
              )}

              {/* Informações Auxiliares Eletrônicas */}
              {paymentMethod !== "DINHEIRO" && (
                <div className="glass-accent rounded-2xl p-4 flex gap-3 text-slate-300 text-xs">
                  <Zap className="w-5 h-5 text-amber-500 shrink-0 mt-0.5 animate-pulse" />
                  <div>
                    <span className="font-bold text-slate-200 block mb-1">
                      Conexão Direta InfinitePay
                    </span>
                    <span>
                      {paymentMethod === "PIX" 
                        ? "Um QR Code dinâmico será gerado na tela do caixa para leitura instantânea."
                        : "Prepare o aplicativo InfinitePay no seu dispositivo móvel para aproximação."}
                    </span>
                  </div>
                </div>
              )}

              {/* Botões */}
              <div className="flex gap-3 pt-2">
                <button
                  onClick={() => {
                    setIsCheckoutOpen(false);
                    setReceivedAmountInput("");
                  }}
                  className="flex-1 py-3.5 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  onClick={handleCheckoutSubmit}
                  disabled={isPending || (paymentMethod === "DINHEIRO" && (!receivedAmountInput || changeInfo < 0 && Math.round(parseFloat(receivedAmountInput.replace(",", ".")) * 100) < totals.total))}
                  className="flex-1 py-3.5 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer disabled:opacity-45"
                >
                  {paymentMethod === "DINHEIRO" ? "Registrar Venda (F10)" : "Enviar para Maquininha"}
                </button>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* 3. MODAL DE AGUARDANDO PAGAMENTO ELETRÔNICO (Pix/Cartão + SIMULADOR SANDBOX INTEGRADO) */}
      {isWaitingPayment && (
        <div className="fixed inset-0 bg-black/85 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-md relative overflow-hidden flex flex-col items-center text-center">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <div className="w-12 h-12 rounded-2xl bg-amber-500/10 flex items-center justify-center text-amber-500 mb-4 animate-pulse-subtle">
              {paymentMethod === "PIX" ? <QrCode className="w-6 h-6" /> : <CreditCard className="w-6 h-6" />}
            </div>

            <h3 className="text-lg font-bold text-slate-100 mb-1">
              {paymentMethod === "PIX" ? "Aguardando Pix do Cliente" : "Aproximação de Cartão Ativa"}
            </h3>
            
            <p className="text-slate-400 text-xs mb-6">
              Venda: <span className="font-mono text-amber-500">{activeSaleId?.slice(0, 8).toUpperCase()}</span> • Valor: <span className="font-extrabold">{formatCurrency(totals.total)}</span>
            </p>

            {/* Simulação visual do QR Code para Pix */}
            {paymentMethod === "PIX" ? (
              <div className="bg-white p-4 rounded-2xl mb-6 shadow-xl shadow-amber-500/5 flex flex-col items-center">
                {/* QR Code Simulado com Divs/Efeito */}
                <div className="w-40 h-40 bg-zinc-900 rounded-xl flex items-center justify-center relative overflow-hidden border-4 border-white">
                  <QrCode className="w-32 h-32 text-white" />
                  <div className="absolute inset-0 bg-gradient-to-tr from-amber-500/20 to-transparent"></div>
                </div>
                <span className="text-[10px] text-zinc-500 font-bold tracking-wider mt-3 uppercase">
                  Escaneie para Pagar
                </span>
              </div>
            ) : (
              <div className="glass rounded-2xl p-5 mb-6 w-full flex flex-col items-center gap-3">
                <Loader2 className="w-8 h-8 text-amber-500 animate-spin" />
                <span className="text-xs text-slate-300 font-medium">
                  Aproxime o cartão na maquininha ou tablet NFC
                </span>
                <span className="text-[10px] text-slate-500">
                  ID Externo da Transação: {activeSaleId?.slice(0, 10)}
                </span>
              </div>
            )}

            {/* Status do Caixa */}
            <div className="flex items-center gap-2 text-xs text-slate-400 mb-6 bg-white/[0.02] border border-white/5 px-4 py-2 rounded-xl">
              <Loader2 className="w-4 h-4 animate-spin text-amber-500" />
              <span>Sincronizando com a maquininha em tempo real...</span>
            </div>

            {/* Ações Técnicas e Polling */}
            <div className="flex gap-2.5 w-full mb-6">
              <button
                onClick={handleCancelElectronicPayment}
                className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
              >
                Cancelar Venda
              </button>
              <button
                onClick={handleManualCheck}
                className="flex-1 py-3 rounded-xl bg-white/[0.03] border border-white/5 text-slate-300 font-bold text-xs hover:bg-white/10 transition cursor-pointer"
              >
                Checar Manual
              </button>
            </div>

            {/* 🛠️ SEÇÃO DO SIMULADOR INTEGRADO DE HOMOLOGAÇÃO LOCAL (Efeito UAU) */}
            <div className="w-full pt-4 border-t border-dashed border-white/5">
              <div className="glass-accent rounded-2xl p-4 flex flex-col gap-3 text-left">
                <div className="flex items-center justify-between">
                  <span className="text-[10px] font-black uppercase text-amber-500 tracking-wider flex items-center gap-1.5">
                    <Sparkles className="w-3.5 h-3.5 stroke-[2]" />
                    Simulador de Homologação Local
                  </span>
                  <span className="text-[9px] bg-amber-500/10 text-amber-400 border border-amber-500/25 px-1.5 py-0.5 rounded font-black">
                    SANDBOX
                  </span>
                </div>
                
                <p className="text-[10px] text-slate-400 leading-relaxed font-medium">
                  Para validar os webhooks da InfinitePay localmente sem credenciais reais de produção, clique no botão abaixo para simular o pagamento aprovado via servidor!
                </p>

                <button
                  onClick={handleSimulatePaymentApproval}
                  className="w-full py-2.5 rounded-xl bg-gradient-to-r from-emerald-500 to-teal-500 text-black font-extrabold text-xs hover:from-emerald-400 hover:to-teal-400 transition cursor-pointer flex items-center justify-center gap-1.5 shadow-lg shadow-emerald-500/10 active:scale-[0.98]"
                >
                  <CheckCircle2 className="w-4 h-4 text-black" />
                  Simular Pagamento Aprovado
                </button>
              </div>
            </div>

          </div>
        </div>
      )}

      {/* 4. MODAL DE RECIBO DIGITAL (ESTILO CUPOM TÉRMICO) */}
      {isReceiptOpen && receiptData && (
        <div className="fixed inset-0 bg-black/90 flex items-center justify-center p-4 z-50 overflow-y-auto">
          <div className="w-full max-w-sm flex flex-col gap-6">
            
            <div className="bg-zinc-100 text-zinc-900 rounded-3xl p-6 shadow-2xl relative font-mono text-xs">
              <div className="absolute -top-1 left-0 right-0 h-1 bg-[radial-gradient(ellipse_at_center,_var(--tw-gradient-stops))] from-zinc-200 to-transparent"></div>

              {/* Cabeçalho */}
              <div className="text-center border-b border-dashed border-zinc-400 pb-4 mb-4 space-y-1">
                <h3 className="font-black text-sm tracking-tight">{receiptData.store?.name}</h3>
                <p className="text-[10px] text-zinc-500">{receiptData.store?.address}</p>
                <p className="text-[10px] text-zinc-500">CNPJ: {receiptData.store?.cnpj}</p>
                <p className="text-[10px] text-zinc-500">FONE: {receiptData.store?.phone}</p>
              </div>

              {/* Info Venda */}
              <div className="border-b border-dashed border-zinc-400 pb-2 mb-3 space-y-1 text-[10px]">
                <div className="flex justify-between">
                  <span>CUPOM: {receiptData.id.slice(0, 8).toUpperCase()}</span>
                  <span>{new Date(receiptData.saleDate).toLocaleDateString()} {new Date(receiptData.saleDate).toLocaleTimeString()}</span>
                </div>
                <div className="flex justify-between">
                  <span>CAIXA: {receiptData.user?.name}</span>
                  <span>PERFIL: {receiptData.user?.role}</span>
                </div>
              </div>

              {/* Itens */}
              <div className="space-y-2 border-b border-dashed border-zinc-400 pb-3 mb-3">
                <div className="flex font-black border-b border-zinc-300 pb-1 text-[10px]">
                  <span className="w-1/2">DESCRIÇÃO</span>
                  <span className="w-1/6 text-center">QTD</span>
                  <span className="w-1/6 text-right">VL.UN</span>
                  <span className="w-1/6 text-right">TOTAL</span>
                </div>

                {receiptData.items?.map((item: SaleReceiptItem) => (
                  <div key={item.id} className="flex text-[10px] leading-tight">
                    <div className="w-1/2 flex flex-col">
                      <span className="font-bold">{item.product?.name}</span>
                      {item.details && (
                        <span className="text-[9px] text-zinc-500 font-medium italic">
                          ({item.details})
                        </span>
                      )}
                    </div>
                    <span className="w-1/6 text-center">{item.quantity}</span>
                    <span className="w-1/6 text-right">{(item.priceUnit / 100).toFixed(2)}</span>
                    <span className="w-1/6 text-right font-bold">{(item.subtotal / 100).toFixed(2)}</span>
                  </div>
                ))}
              </div>

              {/* Totais */}
              <div className="space-y-1.5 border-b border-dashed border-zinc-400 pb-3 mb-3 text-[10px]">
                <div className="flex justify-between">
                  <span>SUBTOTAL:</span>
                  <span>{(receiptData.subtotal / 100).toFixed(2)}</span>
                </div>
                {receiptData.discount > 0 && (
                  <div className="flex justify-between text-red-600 font-bold">
                    <span>DESCONTO:</span>
                    <span>-{(receiptData.discount / 100).toFixed(2)}</span>
                  </div>
                )}
                <div className="flex justify-between font-black text-sm pt-1 border-t border-zinc-300">
                  <span>VALOR PAGO:</span>
                  <span>{(receiptData.total / 100).toFixed(2)}</span>
                </div>
              </div>

              {/* Forma de Pagamento e Detalhes Eletrônicos */}
              <div className="space-y-1 text-[10px] text-zinc-700">
                <div className="flex justify-between font-bold">
                  <span>PAGAMENTO:</span>
                  <span>{receiptData.paymentMethod}</span>
                </div>
                {receiptData.paymentMethod === "DINHEIRO" ? (
                  <>
                    <div className="flex justify-between">
                      <span>VALOR RECEBIDO:</span>
                      <span>{((receiptData.receivedAmount || 0) / 100).toFixed(2)}</span>
                    </div>
                    <div className="flex justify-between font-black text-zinc-900 border-t border-zinc-200 pt-1">
                      <span>TROCO DEVOLVIDO:</span>
                      <span>{((receiptData.changeAmount || 0) / 100).toFixed(2)}</span>
                    </div>
                  </>
                ) : (
                  <>
                    <div className="flex justify-between">
                      <span>NSU TRANS.:</span>
                      <span className="font-mono">{receiptData.nsuTx || "SIM-NSU-123456"}</span>
                    </div>
                    <div className="flex justify-between">
                      <span>ID EXTERNO:</span>
                      <span className="font-mono">{receiptData.externalTxId?.slice(0, 16)}</span>
                    </div>
                    {receiptData.receiptUrl && (
                      <div className="text-[9px] text-zinc-500 font-medium italic mt-1 break-all">
                        Comprovante: {receiptData.receiptUrl}
                      </div>
                    )}
                  </>
                )}
              </div>

              {/* Rodapé */}
              <div className="text-center pt-4 border-t border-dashed border-zinc-400 mt-4 space-y-1">
                <p className="font-bold text-[10px]">OBRIGADO PELA PREFERÊNCIA!</p>
                <p className="text-[9px] text-zinc-500 font-bold">Volte Sempre!</p>
              </div>
            </div>

            {/* Ações */}
            <div className="flex gap-3">
              <button
                onClick={() => {
                  window.print();
                }}
                className="flex-1 py-3.5 rounded-xl border border-white/5 bg-white/[0.03] text-slate-300 font-bold text-xs hover:bg-white/10 transition cursor-pointer flex items-center justify-center gap-1.5"
              >
                <Printer className="w-4 h-4" />
                Imprimir Cupom
              </button>
              
              <button
                onClick={() => {
                  setIsReceiptOpen(false);
                  setReceiptData(null);
                }}
                className="flex-1 py-3.5 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1.5"
              >
                <CheckCircle2 className="w-4 h-4 text-black" />
                Nova Venda
              </button>
            </div>
          </div>
        </div>
      )}

      {/* 5. OVERLAY DO MODO KIOSK (TELA CHEIA SAÍDA ACIDENTAL) */}
      {showKioskFullscreenOverlay && (
        <div className="fixed inset-0 bg-black/95 backdrop-blur-md flex flex-col items-center justify-center p-6 z-[9999] animate-fade-in select-none text-center">
          <div className="glass rounded-3xl p-8 max-w-md w-full border-amber-500/20 shadow-2xl shadow-amber-500/5 flex flex-col items-center gap-6">
            <div className="w-16 h-16 rounded-full bg-amber-500/10 border border-amber-500/20 flex items-center justify-center animate-bounce-slow">
              <AlertTriangle className="w-8 h-8 text-amber-400" />
            </div>
            
            <div className="space-y-2">
              <h3 className="text-xl font-black text-slate-100 tracking-tight">
                TERMINAL EM MODO RESTRITO
              </h3>
              <p className="text-slate-400 text-xs leading-relaxed">
                O Modo Kiosk está ativo neste caixa para segurança operacional. Por favor, retorne o terminal à tela cheia ou peça a liberação do administrador.
              </p>
            </div>

            <div className="flex flex-col gap-3 w-full">
              <button
                onClick={() => {
                  enterFullscreen();
                  setShowKioskFullscreenOverlay(false);
                }}
                className="w-full py-3.5 rounded-2xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition-all cursor-pointer shadow-lg shadow-amber-500/10 active:scale-[0.98] flex items-center justify-center gap-1.5"
              >
                <Zap className="w-4 h-4 text-black" />
                RESTAURAR TELA CHEIA
              </button>

              <button
                onClick={() => {
                  setKioskActionType("unlock_kiosk");
                  setAdminTargetUrl("");
                  setIsUnlockModalOpen(true);
                }}
                className="w-full py-3 rounded-2xl bg-white/[0.02] border border-white/5 text-slate-400 font-bold text-xs hover:bg-white/5 hover:text-slate-200 transition cursor-pointer"
              >
                Destravar com Senha Admin
              </button>
            </div>
          </div>
        </div>
      )}

      {/* 6. MODAL DE DESBLOQUEIO DE SEGURANÇA KIOSK */}
      {isUnlockModalOpen && (
        <div className="fixed inset-0 bg-black/80 backdrop-blur-sm flex items-center justify-center p-4 z-[10000]">
          <div className="glass rounded-3xl p-6 w-full max-w-sm relative overflow-hidden border-white/10 shadow-2xl">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            <div className="flex flex-col items-center gap-4 text-center mb-6">
              <div className="w-12 h-12 rounded-2xl bg-amber-500/10 border border-amber-500/20 flex items-center justify-center">
                <Zap className="w-5 h-5 text-amber-500 animate-pulse-subtle" />
              </div>
              <div>
                <h3 className="text-base font-black text-slate-100">
                  Liberação do Terminal
                </h3>
                <p className="text-slate-500 text-[10px] uppercase font-bold tracking-wider mt-1">
                  {kioskActionType === "logout" && "Ação Bloqueada: Fechar Caixa"}
                  {kioskActionType === "change_store" && "Ação Bloqueada: Trocar Loja"}
                  {kioskActionType === "admin_page" && "Ação Bloqueada: Acesso Gerencial"}
                  {kioskActionType === "unlock_kiosk" && "Desativar Modo Kiosk (Tela Cheia)"}
                  {(!kioskActionType || kioskActionType === null) && "Ação Protegida"}
                </p>
              </div>
            </div>

            <form onSubmit={handleUnlockSubmit} className="space-y-5">
              <div>
                <label className="block text-[10px] font-black text-slate-400 uppercase tracking-widest mb-2 text-left">
                  Senha do Dono / Gerente
                </label>
                <input
                  type="password"
                  required
                  autoFocus
                  placeholder="••••••••"
                  value={kioskPasswordInput}
                  onChange={(e) => setKioskPasswordInput(e.target.value)}
                  className="w-full px-4 py-3 rounded-xl glass-input text-center text-xl font-bold tracking-widest text-slate-100 placeholder-slate-700 focus:outline-none"
                />
              </div>

              <p className="text-[10px] text-slate-500 leading-relaxed text-center">
                Esta tela está protegida. Apenas a senha de um Dono ou Gerente ativo pode destravar esta ação ou liberar o terminal.
              </p>

              <div className="flex gap-3 pt-2">
                <button
                  type="button"
                  onClick={() => {
                    setIsUnlockModalOpen(false);
                    setKioskPasswordInput("");
                    setKioskActionType(null);
                    setAdminTargetUrl("");
                  }}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-bold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1"
                >
                  Confirmar 🔓
                </button>
              </div>
            </form>
          </div>
        </div>
      )}

    </div>
  );
}
