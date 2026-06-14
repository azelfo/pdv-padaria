"use server";

import { prisma } from "@/lib/prisma";
import { getSession } from "@/lib/auth";
import { revalidatePath } from "next/cache";

export interface DashboardActionResult {
  success: boolean;
  error?: string;
}

/**
 * Exclui uma venda, restabelece as quantidades dos produtos no estoque e limpa auditorias.
 * Apenas acessível para papel DONO.
 */
export async function deleteSaleAction(saleId: string): Promise<DashboardActionResult> {
  try {
    const session = await getSession();

    if (!session || session.role !== "DONO" || !session.tenantId) {
      return { success: false, error: "Apenas administradores de rede (Dono) podem excluir vendas." };
    }

    if (!saleId) {
      return { success: false, error: "O ID da venda é obrigatório." };
    }

    // 1. Busca a venda e valida o Tenant para isolamento de dados SaaS
    const sale = await prisma.sale.findUnique({
      where: { id: saleId },
      include: {
        items: true,
      },
    });

    if (!sale) {
      return { success: false, error: "Venda não encontrada." };
    }

    if (sale.tenantId !== session.tenantId) {
      return { success: false, error: "Operação não autorizada para este estabelecimento." };
    }

    const storeId = sale.storeId;

    // 2. Executa a reversão de estoque e exclusão em transação ACID
    await prisma.$transaction(async (tx) => {
      // Devolve cada item ao estoque correspondente da filial
      for (const item of sale.items) {
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
                increment: item.quantity,
              },
            },
          });
        }
      }

      // Remove as movimentações de estoque associadas a esta venda
      await tx.stockMovement.deleteMany({
        where: { saleId },
      });

      // Remove a venda (Cascade deletará os SaleItems)
      await tx.sale.delete({
        where: { id: saleId },
      });
    });

    // Revalida as rotas para atualizar o faturamento e estoques locais instantaneamente
    revalidatePath("/pdv");
    revalidatePath("/pdv/dashboard");
    revalidatePath("/pdv/estoque");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action deleteSaleAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao excluir venda.";
    return { success: false, error: errorMessage };
  }
}

/**
 * Corrige os metadados de uma venda (método de pagamento, desconto e observações).
 * Apenas acessível para papel DONO.
 */
export async function updateSaleAction(
  saleId: string,
  input: {
    paymentMethod: "DINHEIRO" | "PIX" | "CARTAO_DEBITO" | "CARTAO_CREDITO";
    discount: number; // em centavos
    notes?: string;
  }
): Promise<DashboardActionResult> {
  try {
    const session = await getSession();

    if (!session || session.role !== "DONO" || !session.tenantId) {
      return { success: false, error: "Apenas administradores de rede (Dono) podem editar vendas." };
    }

    const { paymentMethod, discount, notes } = input;

    if (!saleId || discount < 0) {
      return { success: false, error: "Parâmetros de edição de venda inválidos." };
    }

    // 1. Busca a venda e valida o Tenant
    const sale = await prisma.sale.findUnique({
      where: { id: saleId },
    });

    if (!sale) {
      return { success: false, error: "Venda não encontrada." };
    }

    if (sale.tenantId !== session.tenantId) {
      return { success: false, error: "Operação não autorizada para este estabelecimento." };
    }

    // Recalcula o total líquido com base no subtotal original
    const newTotal = Math.max(0, sale.subtotal - discount);

    // 2. Atualiza a venda
    await prisma.sale.update({
      where: { id: saleId },
      data: {
        paymentMethod,
        discount,
        total: newTotal,
        notes: notes || null,
      },
    });

    // Revalida as rotas
    revalidatePath("/pdv");
    revalidatePath("/pdv/dashboard");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action updateSaleAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao atualizar venda.";
    return { success: false, error: errorMessage };
  }
}
