"use server";

import { prisma } from "@/lib/prisma";
import { getSession } from "@/lib/auth";
import { revalidatePath } from "next/cache";

export interface UserActionResult {
  success: boolean;
  error?: string;
}

/**
 * Valida se o usuário da requisição é Dono e retorna a sua sessão ativa
 */
async function getDonoSession() {
  const session = await getSession();
  if (session && session.role === "DONO") {
    return session;
  }
  return null;
}

/**
 * Cria um novo funcionário associado ao mesmo Tenant SaaS do Dono
 */
export async function createUserAction(formData: FormData): Promise<UserActionResult> {
  try {
    const session = await getDonoSession();
    if (!session) {
      return { success: false, error: "Não autorizado. Apenas administradores podem criar usuários." };
    }

    const name = formData.get("name")?.toString();
    const email = formData.get("email")?.toString();
    const password = formData.get("password")?.toString();
    const role = formData.get("role")?.toString();
    const storeId = formData.get("storeId")?.toString();
    const tenantId = session.tenantId;

    if (!name || !email || !password || !role) {
      return { success: false, error: "Preencha todos os campos obrigatórios." };
    }

    // Verifica e-mail duplicado de forma global (e-mail de login precisa ser único em todo o SaaS)
    const existing = await prisma.user.findUnique({
      where: { email },
    });

    if (existing) {
      return { success: false, error: "Este endereço de e-mail já está sendo utilizado por outro usuário no sistema." };
    }

    // Cria o usuário vinculado ao Tenant ativo
    await prisma.user.create({
      data: {
        name,
        email,
        password,
        role,
        storeId: role === "DONO" ? null : (storeId || null),
        tenantId,
      },
    });

    revalidatePath("/admin/users");
    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action createUserAction:", error);
    return { success: false, error: "Erro interno no servidor ao criar usuário." };
  }
}

/**
 * Atualiza um funcionário existente com garantia de isolamento do Tenant SaaS
 */
export async function updateUserAction(id: string, formData: FormData): Promise<UserActionResult> {
  try {
    const session = await getDonoSession();
    if (!session) {
      return { success: false, error: "Não autorizado." };
    }

    const name = formData.get("name")?.toString();
    const email = formData.get("email")?.toString();
    const password = formData.get("password")?.toString();
    const role = formData.get("role")?.toString();
    const storeId = formData.get("storeId")?.toString();
    const tenantId = session.tenantId;

    if (!name || !email || !password || !role) {
      return { success: false, error: "Preencha todos os campos obrigatórios." };
    }

    // Verifica duplicidade de e-mail globalmente excluindo o usuário atual
    const existing = await prisma.user.findFirst({
      where: {
        email,
        NOT: { id },
      },
    });

    if (existing) {
      return { success: false, error: "Este endereço de e-mail já está sendo utilizado por outro usuário no sistema." };
    }

    // Atualiza o usuário garantindo que pertença ao mesmo Tenant SaaS (segurança estrita)
    await prisma.user.update({
      where: { 
        id,
        tenantId,
      },
      data: {
        name,
        email,
        password,
        role,
        storeId: role === "DONO" ? null : (storeId || null),
      },
    });

    revalidatePath("/admin/users");
    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action updateUserAction:", error);
    return { success: false, error: "Erro interno no servidor ao atualizar usuário." };
  }
}

/**
 * Alterna o status ativo do funcionário (Soft Delete / Reativação) com trava multitenant
 */
export async function toggleUserStatusAction(id: string, active: boolean): Promise<UserActionResult> {
  try {
    const session = await getDonoSession();
    if (!session) {
      return { success: false, error: "Não autorizado." };
    }

    if (session.id === id) {
      return { success: false, error: "Você não pode desativar seu próprio usuário logado." };
    }

    const tenantId = session.tenantId;

    // Atualiza o funcionário se for da mesma rede de padarias
    await prisma.user.update({
      where: { 
        id,
        tenantId,
      },
      data: { active },
    });

    revalidatePath("/admin/users");
    return { success: true };
  } catch (error) {
    console.error("Erro na Server Action toggleUserStatusAction:", error);
    return { success: false, error: "Erro ao alternar o status do usuário no banco de dados." };
  }
}
