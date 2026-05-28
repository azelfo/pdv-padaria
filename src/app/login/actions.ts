"use server";

import { prisma } from "@/lib/prisma";
import { setSession } from "@/lib/auth";

export interface LoginResult {
  success: boolean;
  error?: string;
  redirectTo?: string;
}

export async function loginAction(formData: FormData): Promise<LoginResult> {
  const email = formData.get("email")?.toString();
  const password = formData.get("password")?.toString();

  if (!email || !password) {
    return { success: false, error: "Preencha todos os campos obrigatórios." };
  }

  try {
    const user = await prisma.user.findUnique({
      where: { email },
      include: { store: true },
    });

    if (!user || !user.active) {
      return { success: false, error: "Usuário não encontrado ou inativo." };
    }

    // Validação de senha clássica simplificada
    if (user.password !== password) {
      return { success: false, error: "Credenciais inválidas. Verifique sua senha." };
    }

    // Configura a sessão
    await setSession({
      id: user.id,
      name: user.name,
      email: user.email,
      role: user.role,
      storeId: user.storeId,
      tenantId: user.tenantId,
    });

    // Dono deve selecionar qual loja deseja acessar
    if (user.role === "DONO") {
      return { success: true, redirectTo: "/store-select" };
    }

    // Atendente e Gerente vão direto para o PDV da sua loja vinculada
    return { success: true, redirectTo: "/pdv" };
  } catch (error) {
    console.error("Erro na Server Action de Login:", error);
    return { success: false, error: "Ocorreu um erro interno no servidor." };
  }
}
