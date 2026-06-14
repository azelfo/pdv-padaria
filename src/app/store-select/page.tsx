import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";
import { redirect } from "next/navigation";
import { LogOut } from "lucide-react";
import StoreSelectClient from "./store-select-client";
import { destroySession } from "@/lib/auth";

export const dynamic = "force-dynamic";

export const metadata = {
  title: "Selecionar Loja - PADARIA",
};

export default async function StoreSelectPage() {
  const session = await getSession();

  // Proteção de rotas
  if (!session) {
    redirect("/login");
  }

  // Busca em paralelo o usuário logado e as lojas ativas exclusivas do Tenant SaaS
  const [userExists, stores] = await Promise.all([
    prisma.user.findUnique({
      where: { id: session.id, active: true },
    }),
    prisma.store.findMany({
      where: { 
        active: true,
        tenantId: session.tenantId,
      },
      orderBy: { name: "asc" },
    }),
  ]);

  if (!userExists) {
    redirect("/login");
  }

  // Se não for Dono e já tiver uma loja vinculada, vai direto para o PDV
  if (session.role !== "DONO" && session.storeId) {
    redirect("/pdv");
  }

  // Função para fazer logout
  async function handleLogout() {
    "use server";
    await destroySession();
    redirect("/login");
  }

  return (
    <div className="relative min-h-screen flex flex-col items-center justify-center overflow-hidden bg-[#050507] px-4 py-12">
      {/* Luzes decorativas */}
      <div className="absolute top-10 right-10 w-96 h-96 bg-amber-500/5 rounded-full blur-[100px] pointer-events-none"></div>
      <div className="absolute bottom-10 left-10 w-96 h-96 bg-orange-500/5 rounded-full blur-[100px] pointer-events-none"></div>

      <div className="w-full max-w-4xl z-10 flex flex-col items-center">
        {/* Cabeçalho */}
        <div className="text-center mb-10">
          <p className="text-amber-500 text-xs font-bold uppercase tracking-wider mb-2">
            Painel Administrativo
          </p>
          <h1 className="text-3xl sm:text-4xl font-extrabold tracking-tight bg-gradient-to-r from-slate-100 to-slate-300 bg-clip-text text-transparent">
            Olá, {session.name}
          </h1>
          <p className="text-slate-400 text-sm sm:text-base mt-2 max-w-md mx-auto">
            Selecione qual filial você deseja gerenciar e operar neste momento.
          </p>
        </div>

        {/* Client Component para interatividade dos Cards */}
        <StoreSelectClient stores={stores} />

        {/* Ações inferiores */}
        <form action={handleLogout} className="mt-10">
          <button
            type="submit"
            className="flex items-center gap-2 px-5 py-2.5 rounded-xl border border-white/5 bg-white/[0.02] hover:bg-red-500/10 hover:border-red-500/20 text-slate-400 hover:text-red-400 text-sm font-semibold transition-all duration-300 cursor-pointer"
          >
            <LogOut className="w-4 h-4" />
            Sair do Painel
          </button>
        </form>
      </div>
    </div>
  );
}
