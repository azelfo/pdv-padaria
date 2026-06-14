"use server";

import { prisma } from "@/lib/prisma";
import { getSession, destroySession } from "@/lib/auth";
import { revalidatePath } from "next/cache";

export interface SaleItemInput {
  productId: string;
  name: string;
  quantity: number;
  priceUnit: number;
  subtotal: number;
  type: string; // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
  details?: string; // JSON String ou texto com detalhes
}

export interface CreateSaleInput {
  items: SaleItemInput[];
  paymentMethod: "DINHEIRO" | "PIX" | "CARTAO_DEBITO" | "CARTAO_CREDITO";
  receivedAmount?: number; // em centavos
  changeAmount?: number; // em centavos
  discount?: number; // em centavos
  notes?: string;
}

export interface CreateSaleResult {
  success: boolean;
  error?: string;
  saleId?: string;
  receiptData?: unknown;
}

/**
 * Cria uma venda no banco de dados e desconta o estoque em transação atômica.
 */
export async function createSaleAction(input: CreateSaleInput): Promise<CreateSaleResult> {
  try {
    const session = await getSession();

    if (!session || !session.storeId || !session.tenantId) {
      return { success: false, error: "Sessão, loja ou inquilino SaaS não configurados." };
    }

    const { items, paymentMethod, receivedAmount, changeAmount, discount = 0, notes } = input;

    if (!items || items.length === 0) {
      return { success: false, error: "Não há itens no carrinho para finalizar a venda." };
    }

    const storeId = session.storeId;
    const userId = session.id;
    const tenantId = session.tenantId;

    // Calcula os totais com base nos itens
    let subtotal = 0;
    for (const item of items) {
      subtotal += item.subtotal;
    }

    const total = Math.max(0, subtotal - discount);

    // Executa a transação atômica
    const result = await prisma.$transaction(async (tx) => {
      // 1. Cria a venda sob o Tenant SaaS ativo
      const sale = await tx.sale.create({
        data: {
          storeId,
          userId,
          tenantId,
          subtotal,
          discount,
          total,
          paymentMethod,
          paymentStatus: (paymentMethod === "PIX" || paymentMethod === "CARTAO_DEBITO" || paymentMethod === "CARTAO_CREDITO") ? "PENDENTE" : "APROVADO",
          receivedAmount,
          changeAmount,
          notes,
        },
      });

      // 2. Processa cada item da venda
      for (const item of items) {
        await tx.saleItem.create({
          data: {
            saleId: sale.id,
            productId: item.productId,
            quantity: item.quantity,
            priceUnit: item.priceUnit,
            subtotal: item.subtotal,
            type: item.type,
            details: item.details,
          },
        });

        // 3. Deduz o estoque (StoreProduct) para a loja ativa
        const storeProduct = await tx.storeProduct.findUnique({
          where: {
            storeId_productId: {
              storeId,
              productId: item.productId,
            },
          },
        });

        if (storeProduct) {
          await tx.storeProduct.update({
            where: { id: storeProduct.id },
            data: {
              quantity: {
                decrement: item.quantity,
              },
            },
          });
        }

        // 4. Registra a movimentação de estoque sob o Tenant ativo
        await tx.stockMovement.create({
          data: {
            productId: item.productId,
            storeId,
            userId,
            tenantId,
            type: "SAIDA",
            quantity: item.quantity,
            reason: "VENDA",
            saleId: sale.id,
          },
        });
      }

      // Busca a venda completa com itens e informações de usuário/loja para o recibo
      const fullSale = await tx.sale.findUnique({
        where: { id: sale.id },
        include: {
          store: true,
          user: true,
          items: {
            include: {
              product: true,
            },
          },
        },
      });

      return fullSale;
    });

    // Revalida a página do PDV para atualizar o catálogo de produtos e estoque em tempo real
    revalidatePath("/pdv");

    return {
      success: true,
      saleId: result?.id,
      receiptData: result,
    };
  } catch (error) {
    console.error("Erro ao criar venda no servidor:", error);
    return { success: false, error: "Falha interna ao registrar a venda no banco de dados." };
  }
}

/**
 * Destrói a sessão e redireciona (Server Action helper)
 */
export async function logoutAction(): Promise<void> {
  await destroySession();
}

/**
 * Valida a senha de um administrador (Dono ou Gerente) para destravar o Modo Kiosk
 */
export async function verifyAdminPasswordAction(password: string): Promise<{ success: boolean; error?: string; adminName?: string }> {
  try {
    const session = await getSession();
    if (!session || !session.tenantId) {
      return { success: false, error: "Sessão ou inquilino SaaS não configurados." };
    }
    const tenantId = session.tenantId;

    if (!password) {
      return { success: false, error: "A senha é obrigatória." };
    }

    // Busca todos os donos ou gerentes ativos específicos deste Tenant SaaS!
    const admins = await prisma.user.findMany({
      where: {
        role: { in: ["DONO", "GERENTE"] },
        active: true,
        tenantId, // Garantia estrita de isolamento de segurança
      },
    });

    // Procura por algum que tenha a senha correspondente (validação clássica em plain-text neste sistema)
    const matchingAdmin = admins.find((admin) => admin.password === password);

    if (matchingAdmin) {
      return { success: true, adminName: matchingAdmin.name };
    }

    return { success: false, error: "Senha administrativa inválida." };
  } catch (error) {
    console.error("Erro ao validar senha administrativa:", error);
    return { success: false, error: "Erro interno ao processar validação." };
  }
}
