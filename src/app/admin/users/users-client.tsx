"use client";

import { useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { 
  UserPlus, 
  Edit2, 
  UserX, 
  UserCheck, 
  Store, 
  ChefHat, 
  KeyRound, 
  Mail, 
  ArrowLeft, 
  Loader2, 
  ShieldCheck, 
  X,
  User as UserIcon
} from "lucide-react";
import { toast } from "react-hot-toast";
import { createUserAction, updateUserAction, toggleUserStatusAction } from "./actions";

interface UserData {
  id: string;
  name: string;
  email: string;
  password: string;
  role: string;
  active: boolean;
  storeId: string | null;
  storeName: string | null;
}

interface SessionData {
  id: string;
  name: string;
  email: string;
  role: string;
  tenantId: string;
  storeId: string | null;
  storeName?: string | null;
}

interface StoreData {
  id: string;
  name: string;
}

interface UsersClientProps {
  session: SessionData;
  users: UserData[];
  stores: StoreData[];
}

export default function UsersClient({ session, users, stores }: UsersClientProps) {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();

  // Estados dos modais e formulários
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editUserId, setEditUserId] = useState<string | null>(null);

  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("ATENDENTE");
  const [storeId, setStoreId] = useState("");

  const handleOpenAddModal = () => {
    setEditUserId(null);
    setName("");
    setEmail("");
    setPassword("");
    setRole("ATENDENTE");
    setStoreId(stores[0]?.id || "");
    setIsModalOpen(true);
  };

  const handleOpenEditModal = (user: UserData) => {
    setEditUserId(user.id);
    setName(user.name);
    setEmail(user.email);
    setPassword(user.password);
    setRole(user.role);
    setStoreId(user.storeId || stores[0]?.id || "");
    setIsModalOpen(true);
  };

  const handleSubmit = (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const formData = new FormData();
    formData.append("name", name);
    formData.append("email", email);
    formData.append("password", password);
    formData.append("role", role);
    formData.append("storeId", role === "DONO" ? "" : storeId);

    startTransition(async () => {
      let result;
      if (editUserId) {
        result = await updateUserAction(editUserId, formData);
      } else {
        result = await createUserAction(formData);
      }

      if (result.success) {
        toast.success(editUserId ? "Funcionário atualizado!" : "Funcionário cadastrado!");
        setIsModalOpen(false);
      } else {
        toast.error(result.error || "Erro ao salvar funcionário.");
      }
    });
  };

  const handleToggleStatus = (id: string, currentActive: boolean) => {
    if (confirm(`Deseja realmente ${currentActive ? "DESATIVAR" : "REATIVAR"} este funcionário?`)) {
      startTransition(async () => {
        const result = await toggleUserStatusAction(id, !currentActive);
        if (result.success) {
          toast.success(currentActive ? "Funcionário desativado." : "Funcionário reativado!");
        } else {
          toast.error(result.error || "Erro ao alterar status do funcionário.");
        }
      });
    }
  };

  const handleBackToPdv = () => {
    router.push("/pdv");
  };

  return (
    <div className="min-h-screen flex flex-col bg-[#050507] text-slate-100 p-6">
      
      {/* Luzes decorativas */}
      <div className="absolute top-10 right-10 w-96 h-96 bg-amber-500/5 rounded-full blur-[100px] pointer-events-none"></div>

      <div className="w-full max-w-6xl mx-auto z-10 flex-1 flex flex-col">
        
        {/* CABEÇALHO DA PÁGINA */}
        <header className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4 mb-8">
          <div className="flex items-center gap-3">
            <button
              onClick={handleBackToPdv}
              className="w-10 h-10 rounded-xl bg-white/[0.02] border border-white/5 flex items-center justify-center text-slate-400 hover:text-amber-400 hover:bg-white/5 hover:border-amber-500/20 transition cursor-pointer"
            >
              <ArrowLeft className="w-5 h-5" />
            </button>
            <div>
              <span className="text-[10px] font-bold text-amber-500 uppercase tracking-wider block">
                Painel Administrativo
              </span>
              <h1 className="text-2xl font-extrabold tracking-tight text-slate-100">
                Gestão de Funcionários
              </h1>
            </div>
          </div>

          <button
            onClick={handleOpenAddModal}
            className="flex items-center justify-center gap-2 py-3 px-5 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition-all duration-300 shadow-lg shadow-amber-500/10 cursor-pointer active:scale-[0.98]"
          >
            <UserPlus className="w-4 h-4 text-black stroke-[2.2]" />
            Adicionar Funcionário
          </button>
        </header>

        {/* TABELA DE USUÁRIOS GLASS */}
        <div className="glass rounded-3xl overflow-hidden shadow-2xl flex-1 flex flex-col justify-between">
          <div className="overflow-x-auto">
            <table className="w-full text-left border-collapse">
              <thead>
                <tr className="border-b border-white/5 bg-white/[0.01]">
                  <th className="p-4 pl-6 text-xs font-bold uppercase tracking-wider text-slate-400">Nome / Usuário</th>
                  <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">E-mail de Acesso</th>
                  <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Perfil / Cargo</th>
                  <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Loja Vinculada</th>
                  <th className="p-4 text-xs font-bold uppercase tracking-wider text-slate-400">Status</th>
                  <th className="p-4 pr-6 text-xs font-bold uppercase tracking-wider text-slate-400 text-right">Ações</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-white/5 text-sm font-medium">
                {users.map((user) => {
                  const isCurrent = user.id === session.id;
                  
                  return (
                    <tr 
                      key={user.id} 
                      className={`hover:bg-white/[0.01] transition-colors ${
                        !user.active ? "opacity-45" : ""
                      }`}
                    >
                      {/* Nome */}
                      <td className="p-4 pl-6">
                        <div className="flex items-center gap-3">
                          <div className={`w-9 h-9 rounded-xl flex items-center justify-center text-slate-400 border border-white/5 ${
                            user.role === "DONO" 
                              ? "bg-amber-500/10 text-amber-400 border-amber-500/20" 
                              : user.role === "GERENTE" 
                                ? "bg-indigo-500/10 text-indigo-400" 
                                : "bg-emerald-500/10 text-emerald-400"
                          }`}>
                            {user.role === "DONO" ? (
                              <ShieldCheck className="w-4 h-4" />
                            ) : user.role === "GERENTE" ? (
                              <ChefHat className="w-4 h-4" />
                            ) : (
                              <UserIcon className="w-4 h-4" />
                            )}
                          </div>
                          <div>
                            <span className="font-bold text-slate-200 block">{user.name}</span>
                            {isCurrent && (
                              <span className="text-[9px] bg-amber-500/15 text-amber-500 border border-amber-500/20 px-1 py-0.5 rounded uppercase font-extrabold tracking-wider">
                                Você logado
                              </span>
                            )}
                          </div>
                        </div>
                      </td>

                      {/* E-mail */}
                      <td className="p-4 text-slate-300 font-mono text-xs">{user.email}</td>

                      {/* Cargo */}
                      <td className="p-4">
                        <span className={`text-[10px] font-black uppercase tracking-wider px-2.5 py-1 rounded-full ${
                          user.role === "DONO"
                            ? "bg-amber-500/10 text-amber-400 border border-amber-500/20"
                            : user.role === "GERENTE"
                              ? "bg-indigo-500/10 text-indigo-400 border border-indigo-500/20"
                              : "bg-emerald-500/10 text-emerald-400 border border-emerald-500/20"
                        }`}>
                          {user.role}
                        </span>
                      </td>

                      {/* Loja Vinculada */}
                      <td className="p-4 text-slate-300">
                        {user.role === "DONO" ? (
                          <span className="text-slate-500 italic text-xs">Rede Completa (3 lojas)</span>
                        ) : (
                          <div className="flex items-center gap-1.5">
                            <Store className="w-3.5 h-3.5 text-slate-500 shrink-0" />
                            <span>{user.storeName || "Sem Loja Vinculada"}</span>
                          </div>
                        )}
                      </td>

                      {/* Status */}
                      <td className="p-4">
                        <span className={`text-[10px] font-extrabold uppercase px-2 py-0.5 rounded ${
                          user.active
                            ? "bg-emerald-500/10 text-emerald-400 border border-emerald-500/15"
                            : "bg-red-500/10 text-red-400 border border-red-500/15"
                        }`}>
                          {user.active ? "Ativo" : "Inativo"}
                        </span>
                      </td>

                      {/* Ações */}
                      <td className="p-4 pr-6 text-right space-x-1 whitespace-nowrap">
                        <button
                          onClick={() => handleOpenEditModal(user)}
                          className="p-2 rounded-xl text-slate-400 hover:text-amber-400 hover:bg-white/5 border border-transparent hover:border-white/5 transition cursor-pointer"
                        >
                          <Edit2 className="w-4 h-4" />
                        </button>
                        
                        <button
                          onClick={() => handleToggleStatus(user.id, user.active)}
                          disabled={isCurrent}
                          className={`p-2 rounded-xl border border-transparent transition cursor-pointer ${
                            isCurrent 
                              ? "opacity-35 cursor-not-allowed" 
                              : user.active
                                ? "text-slate-400 hover:text-red-400 hover:bg-red-500/10 hover:border-red-500/15"
                                : "text-slate-400 hover:text-emerald-400 hover:bg-emerald-500/10 hover:border-emerald-500/15"
                          }`}
                        >
                          {user.active ? <UserX className="w-4 h-4" /> : <UserCheck className="w-4 h-4" />}
                        </button>
                      </td>
                    </tr>
                  );
                })}

                {users.length === 0 && (
                  <tr>
                    <td colSpan={6} className="p-16 text-center text-slate-500 font-medium">
                      Nenhum funcionário cadastrado no banco.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          <div className="p-4 border-t border-white/5 bg-white/[0.01] flex items-center justify-between text-xs text-slate-500">
            <span>Total de Funcionários cadastrados: {users.length}</span>
            <span>Apenas administradores de rede podem auditar usuários.</span>
          </div>
        </div>
      </div>

      {/* 🛠️ MODAL DE CRIAÇÃO / EDIÇÃO DE FUNCIONÁRIO GLASS */}
      {isModalOpen && (
        <div className="fixed inset-0 bg-black/80 flex items-center justify-center p-4 z-50">
          <div className="glass rounded-3xl p-6 w-full max-w-md relative overflow-hidden">
            <div className="absolute top-0 left-0 right-0 h-[2px] bg-gradient-to-r from-transparent via-amber-500 to-transparent"></div>

            {/* Fechar */}
            <button
              onClick={() => setIsModalOpen(false)}
              className="absolute top-4 right-4 text-slate-500 hover:text-slate-200 transition cursor-pointer"
            >
              <X className="w-5 h-5" />
            </button>

            <h3 className="text-lg font-bold text-slate-100 flex items-center gap-2 mb-5">
              <UserPlus className="w-5 h-5 text-amber-500 animate-float" />
              {editUserId ? "Editar Funcionário" : "Cadastrar Novo Funcionário"}
            </h3>

            <form onSubmit={handleSubmit} className="space-y-4">
              
              {/* Nome */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Nome Completo
                </label>
                <div className="relative">
                  <span className="absolute inset-y-0 left-0 pl-3 flex items-center text-slate-500">
                    <UserIcon className="w-4 h-4" />
                  </span>
                  <input
                    type="text"
                    required
                    placeholder="Ex: Aline Ribeiro"
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    className="w-full pl-9 pr-4 py-2.5 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none"
                  />
                </div>
              </div>

              {/* E-mail */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  E-mail (Usuário de Login)
                </label>
                <div className="relative">
                  <span className="absolute inset-y-0 left-0 pl-3 flex items-center text-slate-500">
                    <Mail className="w-4 h-4" />
                  </span>
                  <input
                    type="email"
                    required
                    placeholder="aline.caixa@padaria.com.br"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    className="w-full pl-9 pr-4 py-2.5 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none"
                  />
                </div>
              </div>

              {/* Senha */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Senha Provisória
                </label>
                <div className="relative">
                  <span className="absolute inset-y-0 left-0 pl-3 flex items-center text-slate-500">
                    <KeyRound className="w-4 h-4" />
                  </span>
                  <input
                    type="text"
                    required
                    placeholder="Senha de acesso"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    className="w-full pl-9 pr-4 py-2.5 text-sm rounded-xl glass-input text-slate-100 placeholder-slate-600 focus:outline-none font-mono"
                  />
                </div>
              </div>

              {/* Cargo / Permissão */}
              <div>
                <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                  Perfil de Acesso
                </label>
                <select
                  value={role}
                  onChange={(e) => setRole(e.target.value)}
                  className="w-full px-3 py-2.5 text-sm rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer"
                >
                  <option value="ATENDENTE" className="bg-[#121217]">ATENDENTE (Opera o Caixa)</option>
                  <option value="GERENTE" className="bg-[#121217]">GERENTE (Opera Loja & Estoque)</option>
                  <option value="DONO" className="bg-[#121217]">DONO (Acesso Geral Multi-lojas)</option>
                </select>
              </div>

              {/* Filial (Oculto se Dono) */}
              {role !== "DONO" && (
                <div>
                  <label className="block text-xs font-bold text-slate-400 uppercase tracking-wider mb-2">
                    Loja Vinculada
                  </label>
                  <select
                    value={storeId}
                    onChange={(e) => setStoreId(e.target.value)}
                    className="w-full px-3 py-2.5 text-sm rounded-xl glass-input text-slate-100 focus:outline-none cursor-pointer"
                  >
                    {stores.map((store) => (
                      <option key={store.id} value={store.id} className="bg-[#121217]">
                        {store.name}
                      </option>
                    ))}
                  </select>
                </div>
              )}

              {/* Botões */}
              <div className="flex gap-3 pt-4">
                <button
                  type="button"
                  onClick={() => setIsModalOpen(false)}
                  className="flex-1 py-3 rounded-xl border border-white/5 text-slate-400 font-semibold text-xs hover:bg-white/5 transition cursor-pointer"
                >
                  Cancelar
                </button>
                <button
                  type="submit"
                  disabled={isPending}
                  className="flex-1 py-3 rounded-xl bg-gradient-to-r from-amber-500 to-orange-500 text-black font-extrabold text-xs hover:from-amber-400 hover:to-orange-400 transition cursor-pointer flex items-center justify-center gap-1.5"
                >
                  {isPending ? (
                    <Loader2 className="w-4 h-4 animate-spin text-black" />
                  ) : editUserId ? (
                    "Atualizar"
                  ) : (
                    "Cadastrar"
                  )}
                </button>
              </div>

            </form>
          </div>
        </div>
      )}

    </div>
  );
}
