import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import UsersClient from "./users-client";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Gestão de Funcionários - PADARIA",
};

export default async function AdminUsersPage() {
  const session = await getSession();

  // Apenas o Dono pode acessar o gerenciamento de funcionários
  if (!session || session.role !== "DONO") {
    redirect("/pdv");
  }

  // Busca todos os usuários e lojas ativas vinculadas ao Tenant do Dono em paralelo
  const [users, stores] = await Promise.all([
    prisma.user.findMany({
      where: { 
        tenantId: session.tenantId,
      },
      include: {
        store: {
          select: { name: true },
        },
      },
      orderBy: { name: "asc" },
    }),
    prisma.store.findMany({
      where: { 
        active: true,
        tenantId: session.tenantId,
      },
      select: { id: true, name: true },
      orderBy: { name: "asc" },
    }),
  ]);

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
