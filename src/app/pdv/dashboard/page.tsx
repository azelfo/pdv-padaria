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

  // Validação de segurança: se o usuário logado no cookie não existir mais no banco
  const userExists = await prisma.user.findUnique({
    where: { id: session.id, active: true },
  });

  if (!userExists) {
    redirect("/login");
  }

  // Se o Dono não tiver loja vinculada no cookie, redireciona para a seleção de lojas
  if (!session.storeId) {
    redirect("/store-select");
  }

  // Validação de segurança: se a loja correspondente ao cookie não existir ou for de outro inquilino SaaS
  const storeExists = await prisma.store.findFirst({
    where: { 
      id: session.storeId, 
      active: true,
      tenantId: session.tenantId,
    },
  });

  if (!storeExists) {
    redirect("/store-select");
  }

  // Busca todas as lojas ativas exclusivas deste Tenant SaaS para o filtro do dashboard
  const stores = await prisma.store.findMany({
    where: { 
      active: true,
      tenantId: session.tenantId,
    },
    select: { id: true, name: true },
    orderBy: { name: "asc" },
  });

  // Busca todas as vendas APROVADAS exclusivas do Tenant ativo no banco
  const sales = await prisma.sale.findMany({
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
  });

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
