"use server";

import { prisma } from "@/lib/prisma";
import { getSession } from "@/lib/auth";
import { revalidatePath } from "next/cache";

export interface StockAdjustmentResult {
  success: boolean;
  error?: string;
}

/**
 * Executa o ajuste manual de estoque com auditoria compulsória
 */
export async function adjustStockAction(input: {
  productId: string;
  quantity: number; // A quantidade a ser movimentada (ex: 10, -5, etc. Faremos positivo com o type controlando a operação)
  type: "ENTRADA" | "SAIDA" | "AJUSTE";
  reason: "REPOSICAO" | "PERDA" | "AJUSTE_MANUAL";
}): Promise<StockAdjustmentResult> {
  try {
    const session = await getSession();

    if (!session || !session.storeId || !session.tenantId) {
      return { success: false, error: "Sessão, loja ou inquilino SaaS inválidos." };
    }

    // Apenas Gerentes ou Donos podem efetuar ajustes manuais de estoque
    if (session.role !== "DONO" && session.role !== "GERENTE") {
      return { success: false, error: "Apenas gerentes ou administradores podem ajustar o estoque." };
    }

    const { productId, quantity, type, reason } = input;

    if (!productId || quantity <= 0 || !type || !reason) {
      return { success: false, error: "Parâmetros de ajuste de estoque inválidos." };
    }

    const storeId = session.storeId;
    const userId = session.id;

    // Executa a alteração em bloco de transação ACID do banco
    await prisma.$transaction(async (tx) => {
      // 1. Localiza o estoque do produto na loja
      const storeProduct = await tx.storeProduct.findUnique({
        where: {
          storeId_productId: {
            storeId,
            productId,
          },
        },
      });

      if (!storeProduct) {
        throw new Error("Produto não está cadastrado no estoque da loja selecionada.");
      }

      // 2. Calcula a nova quantidade
      let adjustmentDelta = quantity;
      if (type === "SAIDA") {
        adjustmentDelta = -quantity;
      }

      const newQuantity = Math.max(0, storeProduct.quantity + adjustmentDelta);

      // 3. Atualiza a quantidade do produto na loja
      await tx.storeProduct.update({
        where: { id: storeProduct.id },
        data: {
          quantity: newQuantity,
        },
      });

      // 4. Cria a movimentação de auditoria na StockMovement
      await tx.stockMovement.create({
        data: {
          productId,
          storeId,
          userId,
          tenantId: session.tenantId,
          type,
          quantity,
          reason,
        },
      });
    });

    // Força a revalidação das rotas para o operador ver as atualizações em tempo real
    revalidatePath("/pdv");
    revalidatePath("/pdv/estoque");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action adjustStockAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao realizar ajuste de estoque.";
    return { success: false, error: errorMessage };
  }
}

export interface AddProductResult {
  success: boolean;
  error?: string;
}

/**
 * Cadastra um novo produto globalmente e o associa a todas as filiais
 */
export async function addProductAction(input: {
  name: string;
  barcode: string | null;
  priceSale: number; // em centavos
  priceCost: number; // em centavos
  categoryName: string;
  unitMeasure: string;
  minStock: number;
  initialStock: number;
  type: string; // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
}): Promise<AddProductResult> {
  try {
    const session = await getSession();

    if (!session || !session.storeId || !session.tenantId) {
      return { success: false, error: "Sessão, loja ou inquilino SaaS inválidos." };
    }

    if (session.role !== "DONO" && session.role !== "GERENTE") {
      return { success: false, error: "Apenas gerentes ou administradores podem cadastrar produtos." };
    }

    const { name, barcode, priceSale, priceCost, categoryName, unitMeasure, minStock, initialStock, type } = input;

    if (!name || priceSale < 0 || priceCost < 0 || !categoryName || !unitMeasure || minStock < 0 || initialStock < 0 || !type) {
      return { success: false, error: "Dados do produto inválidos." };
    }

    const tenantId = session.tenantId;

    // Se informou código de barras, garante que ele é único dentro do mesmo Tenant SaaS
    if (barcode && barcode.trim() !== "") {
      const existingProduct = await prisma.product.findFirst({
        where: { 
          barcode: barcode.trim(),
          tenantId,
        },
      });
      if (existingProduct) {
        return { success: false, error: "Já existe um produto cadastrado com este código de barras nesta rede." };
      }
    }

    const storeId = session.storeId;
    const userId = session.id;

    // Executa a transação atômica
    await prisma.$transaction(async (tx) => {
      // 1. Localiza ou cria a categoria para este Tenant específico
      const category = await tx.category.upsert({
        where: { 
          tenantId_name: {
            tenantId,
            name: categoryName.trim(),
          }
        },
        update: {},
        create: { 
          name: categoryName.trim(),
          tenantId,
        },
      });

      // 2. Cria o produto na tabela global vinculado ao Tenant
      const product = await tx.product.create({
        data: {
          name: name.trim(),
          barcode: barcode && barcode.trim() !== "" ? barcode.trim() : null,
          priceSale,
          priceCost,
          type,
          unitMeasure,
          categoryId: category.id,
          tenantId,
        },
      });

      // 3. Busca todas as lojas ativas exclusivas deste Tenant SaaS
      const stores = await tx.store.findMany({
        where: { 
          active: true,
          tenantId,
        },
      });

      // 4. Cria a associação do produto para cada loja
      for (const st of stores) {
        const isCurrentStore = st.id === storeId;
        
        await tx.storeProduct.create({
          data: {
            storeId: st.id,
            productId: product.id,
            quantity: isCurrentStore ? initialStock : 0,
            minStock: isCurrentStore ? minStock : 0,
          },
        });

        // 5. Se for a loja ativa e o estoque inicial for superior a 0, lança auditoria de entrada
        if (isCurrentStore && initialStock > 0) {
          await tx.stockMovement.create({
            data: {
              productId: product.id,
              storeId: st.id,
              userId,
              tenantId,
              type: "ENTRADA",
              quantity: initialStock,
              reason: "REPOSICAO",
            },
          });
        }
      }
    });

    revalidatePath("/pdv");
    revalidatePath("/pdv/estoque");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action addProductAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao cadastrar produto.";
    return { success: false, error: errorMessage };
  }
}

export interface DeleteProductResult {
  success: boolean;
  error?: string;
}

/**
 * Efetua a remoção lógica (soft delete) do produto
 */
export async function deleteProductAction(productId: string): Promise<DeleteProductResult> {
  try {
    const session = await getSession();

    if (!session) {
      return { success: false, error: "Sessão inválida." };
    }

    if (session.role !== "DONO" && session.role !== "GERENTE") {
      return { success: false, error: "Apenas gerentes ou administradores podem excluir produtos." };
    }

    if (!productId) {
      return { success: false, error: "ID do produto inválido." };
    }

    // Trava de segurança: impede excluir o Pão (PAO_FRANCES)
    const product = await prisma.product.findUnique({
      where: { id: productId },
    });

    if (!product) {
      return { success: false, error: "Produto não encontrado." };
    }

    if (product.type === "PAO_FRANCES") {
      return { success: false, error: "O produto principal Pão não pode ser excluído." };
    }

    // Soft delete: atualiza active: false na tabela global Product
    await prisma.product.update({
      where: { id: productId },
      data: { active: false },
    });

    revalidatePath("/pdv");
    revalidatePath("/pdv/estoque");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action deleteProductAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao excluir produto.";
    return { success: false, error: errorMessage };
  }
}

export interface UpdateProductResult {
  success: boolean;
  error?: string;
}

/**
 * Atualiza os dados cadastrais globais de um produto
 */
export async function updateProductAction(input: {
  id: string;
  name: string;
  barcode: string | null;
  priceSale: number; // em centavos
  priceCost: number; // em centavos
  categoryName: string;
  unitMeasure: string;
  type: string; // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
}): Promise<UpdateProductResult> {
  try {
    const session = await getSession();

    if (!session) {
      return { success: false, error: "Sessão inválida." };
    }

    if (session.role !== "DONO" && session.role !== "GERENTE") {
      return { success: false, error: "Apenas gerentes ou administradores podem editar produtos." };
    }

    const { id, name, barcode, priceSale, priceCost, categoryName, unitMeasure, type } = input;
    const tenantId = session.tenantId;

    if (!id || !name || priceSale < 0 || priceCost < 0 || !categoryName || !unitMeasure || !type) {
      return { success: false, error: "Parâmetros de edição de produto inválidos." };
    }

    // Se informou código de barras, garante que ele é único no mesmo Tenant (excluindo o próprio produto)
    if (barcode && barcode.trim() !== "") {
      const existingProduct = await prisma.product.findFirst({
        where: { 
          barcode: barcode.trim(),
          tenantId,
          id: { not: id }
        },
      });
      if (existingProduct) {
        return { success: false, error: "Já existe outro produto cadastrado com este código de barras nesta rede." };
      }
    }

    // Executa a transação para atualizar a categoria e o produto
    await prisma.$transaction(async (tx) => {
      // 1. Localiza ou cria a nova categoria sob o Tenant
      const category = await tx.category.upsert({
        where: { 
          tenantId_name: {
            tenantId,
            name: categoryName.trim(),
          }
        },
        update: {},
        create: { 
          name: categoryName.trim(),
          tenantId,
        },
      });

      // 2. Atualiza o produto globalmente
      await tx.product.update({
        where: { id },
        data: {
          name: name.trim(),
          barcode: barcode && barcode.trim() !== "" ? barcode.trim() : null,
          priceSale,
          priceCost,
          type,
          unitMeasure,
          categoryId: category.id,
        },
      });
    });

    revalidatePath("/pdv");
    revalidatePath("/pdv/estoque");

    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action updateProductAction:", error);
    const errorMessage = error instanceof Error ? error.message : "Erro interno ao atualizar produto.";
    return { success: false, error: errorMessage };
  }
}
