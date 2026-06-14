import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import DashboardClient from "./dashboard-client";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Dashboard Financeiro - PADARIA",
};

export default async function PdvDashboardPage() {
  const session = await getSession();

  // Trava de segurança: apenas o Dono pode acessar o Dashboard
  if (!session || session.role !== "DONO") {
    redirect("/pdv");
  }

  // Se o Dono não tiver loja vinculada no cookie, redireciona para a seleção de lojas
  if (!session.storeId) {
    redirect("/store-select");
  }

  // Executa todas as consultas ao banco de dados em paralelo
  const [userExists, storeExists, stores, sales] = await Promise.all([
    prisma.user.findUnique({
      where: { id: session.id, active: true },
    }),
    prisma.store.findFirst({
      where: { 
        id: session.storeId, 
        active: true,
        tenantId: session.tenantId,
      },
    }),
    prisma.store.findMany({
      where: { 
        active: true,
        tenantId: session.tenantId,
      },
      select: { id: true, name: true },
      orderBy: { name: "asc" },
    }),
    prisma.sale.findMany({
      where: {
        paymentStatus: "APROVADO",
        tenantId: session.tenantId,
      },
      include: {
        store: {
          select: { name: true },
        },
        user: {
          select: { name: true },
        },
        items: {
          include: {
            product: {
              include: {
                category: true,
              },
            },
          },
        },
      },
      orderBy: {
        saleDate: "desc",
      },
    }),
  ]);

  // Validações pós-busca paralela
  if (!userExists) {
    redirect("/login");
  }

  if (!storeExists) {
    redirect("/store-select");
  }

  // Formata as vendas para enviar ao Client Component de forma segura (sem objetos de data)
  const formattedSales = sales.map((sale) => ({
    id: sale.id,
    storeId: sale.storeId,
    storeName: sale.store.name,
    userId: sale.userId,
    userName: sale.user.name,
    saleDate: sale.saleDate.toISOString(),
    subtotal: sale.subtotal,
    discount: sale.discount,
    total: sale.total,
    paymentMethod: sale.paymentMethod,
    paymentStatus: sale.paymentStatus,
    receivedAmount: sale.receivedAmount,
    changeAmount: sale.changeAmount,
    notes: sale.notes,
    items: sale.items.map((item) => ({
      id: item.id,
      productId: item.productId,
      productName: item.product?.name || "Produto Removido",
      categoryName: item.product?.category?.name || "Sem Categoria",
      quantity: item.quantity,
      priceUnit: item.priceUnit,
      subtotal: item.subtotal,
      type: item.type,
      details: item.details,
      unitMeasure: item.product?.unitMeasure || "UN",
    })),
  }));

  return (
    <DashboardClient
      session={session}
      sales={formattedSales}
      stores={stores}
    />
  );
}
