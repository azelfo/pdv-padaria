import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import EstoqueClient from "./estoque-client";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Painel de Estoque - PADARIA",
};

export default async function PdvEstoquePage() {
  const session = await getSession();

  if (!session) {
    redirect("/login");
  }

  // Se for o Dono e não tiver loja vinculada, redireciona para a seleção de lojas
  if (session.role === "DONO" && !session.storeId) {
    redirect("/store-select");
  }

  if (!session.storeId) {
    redirect("/login");
  }

  if (session.role !== "DONO" && session.role !== "GERENTE") {
    redirect("/pdv");
  }

  const activeStoreId = session.storeId;

  // Executa todas as consultas ao banco de dados em paralelo
  const [userExists, storeExists, storeProducts, recentMovements] = await Promise.all([
    prisma.user.findUnique({
      where: { id: session.id, active: true },
    }),
    prisma.store.findFirst({
      where: { 
        id: activeStoreId, 
        active: true,
        tenantId: session.tenantId,
      },
    }),
    prisma.storeProduct.findMany({
      where: {
        storeId: activeStoreId,
        product: {
          active: true,
        },
      },
      include: {
        product: {
          include: {
            category: true,
          },
        },
      },
      orderBy: {
        product: {
          name: "asc",
        },
      },
    }),
    prisma.stockMovement.findMany({
      where: { storeId: activeStoreId },
      include: {
        product: {
          select: { name: true, unitMeasure: true },
        },
        user: {
          select: { name: true },
        },
      },
      orderBy: {
        createdAt: "desc",
      },
      take: 20,
    }),
  ]);

  // Validações pós-busca paralela
  if (!userExists) {
    redirect("/login");
  }

  if (!storeExists) {
    if (session.role === "DONO") {
      redirect("/store-select");
    } else {
      redirect("/login");
    }
  }

  // Formata os produtos para o Client Component
  const formattedProducts = storeProducts.map((sp) => ({
    id: sp.product.id,
    name: sp.product.name,
    barcode: sp.product.barcode,
    priceSale: sp.product.priceSale,
    priceCost: sp.product.priceCost,
    type: sp.product.type,
    unitMeasure: sp.product.unitMeasure,
    categoryName: sp.product.category.name,
    quantity: sp.quantity,
    minStock: sp.minStock,
  }));

  // Formata o histórico de movimentações
  const formattedMovements = recentMovements.map((m) => ({
    id: m.id,
    productName: m.product.name,
    unitMeasure: m.product.unitMeasure,
    userName: m.user.name,
    type: m.type, // "ENTRADA", "SAIDA", "AJUSTE"
    quantity: m.quantity,
    reason: m.reason, // "VENDA", "REPOSICAO", "PERDA", "AJUSTE_MANUAL"
    createdAt: m.createdAt.toISOString(),
  }));

  return (
    <EstoqueClient
      session={session}
      products={formattedProducts}
      movements={formattedMovements}
    />
  );
}
