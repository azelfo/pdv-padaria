"use client";

import { useTransition, useState } from "react";
import { loginAction } from "./actions";
import { useRouter } from "next/navigation";
import { KeyRound, Mail, Loader2, Store, ChefHat } from "lucide-react";
import { toast } from "react-hot-toast";

export default function LoginPage() {
  const [isPending, startTransition] = useTransition();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const router = useRouter();

  // Atalho para preencher rápido credenciais de homologação
  const handleQuickFill = (role: "ATENDENTE" | "GERENTE" | "DONO") => {
    if (role === "ATENDENTE") {
      setEmail("atendente.centro@padaria.com.br");
      setPassword("123");
      toast.success("Preenchido: Aline Caixa (Atendente)");
    } else if (role === "GERENTE") {
      setEmail("gerente.centro@padaria.com.br");
      setPassword("123");
      toast.success("Preenchido: Carlos Gerente");
    } else if (role === "DONO") {
      setEmail("dono@padaria.com.br");
      setPassword("123");
      toast.success("Preenchido: Marcelo Dono");
    }
  };

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const formData = new FormData(e.currentTarget);

    startTransition(async () => {
      const result = await loginAction(formData);
      if (result.success && result.redirectTo) {
        toast.success("Login efetuado com sucesso!");
        router.push(result.redirectTo);
      } else {
        toast.error(result.error || "Erro ao efetuar login.");
      }
    });
  };

  return (
    <div className="relative min-h-screen flex items-center justify-center overflow-hidden bg-[#050507] px-4">
      {/* Luzes de fundo / Efeito forno de brasas */}
      <div className="absolute top-1/4 left-1/4 -translate-x-1/2 -translate-y-1/2 w-96 h-96 bg-amber-600/10 rounded-full blur-[100px] pointer-events-none animate-pulse-subtle"></div>
      <div className="absolute bottom-1/4 right-1/4 translate-x-1/2 translate-y-1/2 w-[450px] h-[450px] bg-orange-600/10 rounded-full blur-[120px] pointer-events-none"></div>

      <div className="w-full max-w-md z-10">
        {/* Logotipo / Ícone Animado */}
        <div className="flex flex-col items-center justify-center mb-8 animate-float">
          <div className="w-16 h-16 rounded-2xl bg-gradient-to-tr from-amber-500 to-orange-600 flex items-center justify-center shadow-lg shadow-amber-500/20 ring-1 ring-white/10 mb-4">
            <ChefHat className="w-9 h-9 text-black stroke-[1.8]" />
          </div>
          <h1 className="text-3xl font-extrabold tracking-tight bg-gradient-to-r from-amber-200 via-amber-400 to-orange-500 bg-clip-text text-transparent">
            PADARIA
          </h1>
          <p className="text-slate-400 text-sm mt-1 font-medium tracking-wide">
            Sistema PDV & Gestão de Redes
          </p>
        </div>

        {/* Card de Login Glass */}
        <div className="glass rounded-3xl p-8 shadow-2xl relative overflow-hidden">
          <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500/30 to-transparent"></div>

          <h2 className="text-xl font-semibold text-slate-100 mb-6 text-center">
            Acesse sua conta
          </h2>

          <form onSubmit={handleSubmit} className="space-y-5">
            {/* Input E-mail */}
            <div>
              <label className="block text-xs font-semibold text-slate-300 uppercase tracking-wider mb-2">
                E-mail de Acesso
              </label>
              <div className="relative">
                <span className="absolute inset-y-0 left-0 pl-3.5 flex items-center text-slate-400 pointer-events-none">
                  <Mail className="w-4 h-4" />
                </span>
                <input
                  type="email"
                  name="email"
                  required
                  placeholder="seuemail@padaria.com.br"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="w-full pl-10 pr-4 py-3 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-500 focus:outline-none"
                />
              </div>
            </div>

            {/* Input Senha */}
            <div>
              <div className="flex items-center justify-between mb-2">
                <label className="block text-xs font-semibold text-slate-300 uppercase tracking-wider">
                  Senha
                </label>
                <span className="text-xs text-amber-500/80 hover:text-amber-500 cursor-pointer transition">
                  Esqueceu a senha?
                </span>
              </div>
              <div className="relative">
                <span className="absolute inset-y-0 left-0 pl-3.5 flex items-center text-slate-400 pointer-events-none">
                  <KeyRound className="w-4 h-4" />
                </span>
                <input
                  type="password"
                  name="password"
                  required
                  placeholder="••••••••"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="w-full pl-10 pr-4 py-3 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-500 focus:outline-none"
                />
              </div>
            </div>

            {/* Botão Entrar */}
            <button
              type="submit"
              disabled={isPending}
              className="w-full relative mt-2 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-semibold text-sm hover:from-amber-400 hover:to-orange-400 transition-all duration-300 focus:outline-none focus:ring-2 focus:ring-amber-500/40 cursor-pointer shadow-lg shadow-amber-500/15 active:scale-[0.98] disabled:opacity-50 flex items-center justify-center gap-2"
            >
              {isPending ? (
                <>
                  <Loader2 className="w-4 h-4 animate-spin text-black" />
                  Autenticando...
                </>
              ) : (
                "Entrar no Sistema"
              )}
            </button>
          </form>

          {/* Divisor */}
          <div className="relative my-6">
            <div className="absolute inset-0 flex items-center">
              <div className="w-full border-t border-white/5"></div>
            </div>
            <div className="relative flex justify-center text-xs uppercase">
              <span className="bg-[#121217] px-3 text-slate-500 font-semibold tracking-wider">
                Homologação Rápida
              </span>
            </div>
          </div>

          {/* Atalhos de Login */}
          <div className="grid grid-cols-3 gap-2">
            <button
              onClick={() => handleQuickFill("ATENDENTE")}
              className="py-2.5 px-1 rounded-xl glass-input text-[11px] font-semibold text-amber-500/90 hover:text-amber-400 hover:bg-white/5 transition flex flex-col items-center justify-center gap-1 cursor-pointer"
            >
              <Store className="w-4 h-4 stroke-[1.8]" />
              Caixa
            </button>
            <button
              onClick={() => handleQuickFill("GERENTE")}
              className="py-2.5 px-1 rounded-xl glass-input text-[11px] font-semibold text-amber-500/90 hover:text-amber-400 hover:bg-white/5 transition flex flex-col items-center justify-center gap-1 cursor-pointer"
            >
              <ChefHat className="w-4 h-4 stroke-[1.8]" />
              Gerente
            </button>
            <button
              onClick={() => handleQuickFill("DONO")}
              className="py-2.5 px-1 rounded-xl glass-input text-[11px] font-semibold text-amber-500/90 hover:text-amber-400 hover:bg-white/5 transition flex flex-col items-center justify-center gap-1 cursor-pointer"
            >
              <KeyRound className="w-4 h-4 stroke-[1.8]" />
              Dono
            </button>
          </div>
        </div>

        <p className="text-center text-xs text-slate-500 mt-6 font-medium">
          Dúvidas ou suporte? Entre em contato com a TI da Rede.
        </p>
      </div>
    </div>
  );
}
