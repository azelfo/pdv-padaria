import { prisma } from "@/lib/prisma";
import { NextResponse } from "next/server";

export const dynamic = "force-dynamic";

export async function POST(request: Request) {
  try {
    const { tenantId, storeId } = await request.json();

    if (!tenantId || !storeId) {
      return NextResponse.json(
        { error: "tenantId e storeId são obrigatórios." },
        { status: 400 }
      );
    }

    // Busca categorias, produtos ativos, usuários e a configuração do pão em paralelo
    const [categories, products, users, breadConfig] = await Promise.all([
      prisma.category.findMany({
        where: { tenantId },
        orderBy: { name: "asc" },
      }),
      prisma.product.findMany({
        where: { tenantId, active: true },
        include: {
          category: true,
          stores: {
            where: { storeId },
          },
        },
        orderBy: { name: "asc" },
      }),
      prisma.user.findMany({
        where: { tenantId, active: true },
        select: {
          id: true,
          name: true,
          email: true,
          password: true, // Necessário hash de senha para login offline-first
          role: true,
          tenantId: true,
          storeId: true,
          active: true,
        },
      }),
      prisma.breadConfig.findUnique({
        where: { storeId },
      }),
    ]);

    return NextResponse.json({
      success: true,
      categories,
      products,
      users,
      breadConfig,
    });
  } catch (error: any) {
    console.error("Erro no Sync Pull:", error);
    return NextResponse.json(
      { error: "Erro interno no servidor.", details: error.message },
      { status: 500 }
    );
  }
}
