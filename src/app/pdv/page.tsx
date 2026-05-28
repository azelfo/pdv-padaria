import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import PdvClient from "./pdv-client";

export const metadata = {
  title: "Caixa PDV - PADARIA",
};

export default async function PdvPage() {
  const session = await getSession();

  // Proteção de rotas
  if (!session) {
    redirect("/login");
  }

  // Validação de segurança: se o usuário logado no cookie não existir mais no banco (ex: pós-seed reset)
  const userExists = await prisma.user.findUnique({
    where: { id: session.id, active: true },
  });

  if (!userExists) {
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

  // Validação de segurança: se a loja correspondente ao cookie não existir ou for de outro inquilino SaaS
  const storeExists = await prisma.store.findFirst({
    where: { 
      id: activeStoreId, 
      active: true,
      tenantId: session.tenantId,
    },
  });

  if (!storeExists) {
    if (session.role === "DONO") {
      redirect("/store-select");
    } else {
      redirect("/login");
    }
  }


  // Busca todos os produtos ativos vinculados ao estoque da loja ativa do Tenant ativo
  const products = await prisma.product.findMany({
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
  });

  // Busca a configuração do pão francês para a loja ativa
  const breadConfig = await prisma.breadConfig.findUnique({
    where: { storeId: activeStoreId },
  });

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
