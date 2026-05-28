import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import UsersClient from "./users-client";

export const metadata = {
  title: "Gestão de Funcionários - PADARIA",
};

export default async function AdminUsersPage() {
  const session = await getSession();

  // Apenas o Dono pode acessar o gerenciamento de funcionários
  if (!session || session.role !== "DONO") {
    redirect("/pdv");
  }

  // Busca todos os usuários vinculados ao Tenant do Dono ordenados por nome
  const users = await prisma.user.findMany({
    where: { 
      tenantId: session.tenantId,
    },
    include: {
      store: {
        select: { name: true },
      },
    },
    orderBy: { name: "asc" },
  });

  // Busca todas as lojas ativas exclusivas deste Tenant SaaS para preenchimento de selects no formulário
  const stores = await prisma.store.findMany({
    where: { 
      active: true,
      tenantId: session.tenantId,
    },
    select: { id: true, name: true },
    orderBy: { name: "asc" },
  });

  // Formata os dados para o componente cliente
  const formattedUsers = users.map((u) => ({
    id: u.id,
    name: u.name,
    email: u.email,
    password: u.password,
    role: u.role,
    active: u.active,
    storeId: u.storeId,
    storeName: u.store?.name || null,
  }));

  return (
    <UsersClient
      session={session}
      users={formattedUsers}
      stores={stores}
    />
  );
}
