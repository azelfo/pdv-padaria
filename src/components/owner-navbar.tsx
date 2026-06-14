"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { BarChart3, Package, Users, ShoppingCart, LogOut, ShieldAlert, Store } from "lucide-react";
import { logoutAction } from "@/app/pdv/actions";

interface OwnerNavbarProps {
  session: {
    id: string;
    name: string;
    email: string;
    role: string;
    tenantId: string;
    storeId: string | null;
    storeName?: string | null;
    tenantName?: string;
  };
}

export default function OwnerNavbar({ session }: OwnerNavbarProps) {
  const pathname = usePathname();

  // Exibe a Navbar apenas se o usuário for o Dono
  if (session.role !== "DONO") return null;

  const navItems = [
    { name: "Caixa PDV", href: "/pdv", icon: ShoppingCart },
    { name: "Dashboard", href: "/pdv/dashboard", icon: BarChart3 },
    { name: "Estoque", href: "/pdv/estoque", icon: Package },
    { name: "Funcionários", href: "/admin/users", icon: Users },
  ];

  return (
    <nav className="w-full bg-[#09090d]/85 border-b border-white/5 backdrop-blur-md sticky top-0 z-40 px-6 py-4">
      <div className="max-w-7xl mx-auto flex flex-col sm:flex-row items-center justify-between gap-4">
        {/* Identificação de Perfil */}
        <div className="flex items-center gap-2.5">
          <div className="w-8 h-8 rounded-xl bg-amber-500/10 border border-amber-500/20 flex items-center justify-center text-amber-500">
            <ShieldAlert className="w-4 h-4" />
          </div>
          <div>
            <span className="text-[10px] font-bold text-amber-500 uppercase tracking-wider block">
              Painel do Proprietário
            </span>
            <span className="text-xs font-semibold text-slate-300 flex items-center gap-1.5">
              <span>{session.name}</span>
              <span className="text-slate-500 font-normal">•</span>
              <span className="text-slate-400 flex items-center gap-1">
                <Store className="w-3 h-3 text-slate-500" />
                {session.storeName || "Rede de Lojas"}
              </span>
            </span>
          </div>
        </div>

        {/* Abas de Navegação */}
        <div className="flex items-center gap-1.5 overflow-x-auto w-full sm:w-auto pb-1 sm:pb-0 justify-center no-scrollbar">
          {navItems.map((item) => {
            const Icon = item.icon;
            const isActive = pathname === item.href;
            return (
              <Link
                key={item.href}
                href={item.href}
                className={`flex items-center gap-2 px-4 py-2.5 rounded-xl text-xs font-bold uppercase tracking-wider transition-all duration-300 border select-none ${
                  isActive
                    ? "bg-gradient-to-r from-amber-500/15 to-orange-500/15 border-amber-500/40 text-amber-400 shadow-[0_0_15px_rgba(245,158,11,0.05)] scale-[1.02]"
                    : "bg-transparent border-transparent text-slate-400 hover:text-slate-200 hover:bg-white/5"
                }`}
              >
                <Icon className="w-4 h-4" />
                <span>{item.name}</span>
              </Link>
            );
          })}
        </div>

        {/* Botão de Logout */}
        <button
          onClick={async () => {
            await logoutAction();
            window.location.href = "/login";
          }}
          className="flex items-center gap-1.5 px-4 py-2 rounded-xl text-xs font-bold uppercase tracking-wider bg-red-500/10 border border-red-500/20 text-red-400 hover:bg-red-500/20 transition cursor-pointer select-none"
        >
          <LogOut className="w-4 h-4" />
          Sair
        </button>
      </div>
    </nav>
  );
}
