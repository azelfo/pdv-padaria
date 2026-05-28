"use server";

import { prisma } from "@/lib/prisma";
import { getSession, setSession } from "@/lib/auth";

export interface StoreSelectResult {
  success: boolean;
  error?: string;
  redirectTo?: string;
}

export async function selectStoreAction(storeId: string): Promise<StoreSelectResult> {
  try {
    const session = await getSession();

    if (!session) {
      return { success: false, error: "Sessão expirada. Faça login novamente." };
    }

    if (session.role !== "DONO") {
      return { success: false, error: "Apenas administradores podem selecionar lojas livremente." };
    }

    const store = await prisma.store.findFirst({
      where: { 
        id: storeId, 
        active: true,
        tenantId: session.tenantId,
      },
    });

    if (!store) {
      return { success: false, error: "Loja não encontrada ou inativa nesta rede." };
    }

    // Atualiza a sessão com a loja escolhida e preserva o tenantId
    await setSession({
      id: session.id,
      name: session.name,
      email: session.email,
      role: session.role,
      storeId: store.id,
      tenantId: session.tenantId,
    });

    return { success: true, redirectTo: "/pdv" };
  } catch (error) {
    console.error("Erro na Server Action de Seleção de Loja:", error);
    return { success: false, error: "Ocorreu um erro no servidor." };
  }
}
