import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import PdvClient from "./pdv-client";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Caixa PDV - PADARIA",
};

export default async function PdvPage() {
  const session = await getSession();

  // Proteção de rotas
  if (!session) {
    redirect("/login");
  }

  // Dono precisa selecionar a loja antes
  if (session.role === "DONO" && !session.storeId) {
    redirect("/store-select");
  }

  if (!session.storeId) {
    redirect("/login");
  }

  const activeStoreId = session.storeId;

  // Executa todas as consultas ao banco de dados em paralelo
  const [userExists, storeExists, products, breadConfig] = await Promise.all([
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
    prisma.product.findMany({
      where: { 
        active: true,
        tenantId: session.tenantId,
      },
      include: {
        category: true,
        stores: {
          where: { storeId: activeStoreId },
        },
      },
      orderBy: { name: "asc" },
    }),
    prisma.breadConfig.findUnique({
      where: { storeId: activeStoreId },
    }),
  ]);

  // Validações de segurança após as consultas paralelas
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

  // Formata os produtos para o frontend simplificar o uso do estoque
  const formattedProducts = products.map((p) => {
    const storeProduct = p.stores[0];
    return {
      id: p.id,
      name: p.name,
      barcode: p.barcode,
      priceSale: p.priceSale,
      priceCost: p.priceCost,
      type: p.type, // "NORMAL", "PAO_FRANCES", "SALGADO", "BOLO"
      unitMeasure: p.unitMeasure,
      imageUrl: p.imageUrl,
      categoryName: p.category.name,
      stockQuantity: storeProduct ? storeProduct.quantity : 0,
      minStock: storeProduct ? storeProduct.minStock : 0,
    };
  });

  return (
    <PdvClient
      session={session}
      products={formattedProducts}
      breadConfig={breadConfig ? {
        priceUnit: breadConfig.priceUnit,
        brackets: JSON.parse(breadConfig.brackets) as { ate: number; qtd: number }[]
      } : null}
    />
  );
}
