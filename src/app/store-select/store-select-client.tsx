"use client";

import { useTransition, useState } from "react";
import { selectStoreAction } from "./actions";
import { useRouter } from "next/navigation";
import { Store, MapPin, Phone, Building2, Loader2, ArrowRight } from "lucide-react";
import { toast } from "react-hot-toast";

interface StoreData {
  id: string;
  name: string;
  address: string;
  phone: string;
  cnpj: string;
}

interface StoreSelectClientProps {
  stores: StoreData[];
}

export default function StoreSelectClient({ stores }: StoreSelectClientProps) {
  const [isPending, startTransition] = useTransition();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const router = useRouter();

  const handleSelect = (storeId: string, storeName: string) => {
    setSelectedId(storeId);
    startTransition(async () => {
      const result = await selectStoreAction(storeId);
      if (result.success && result.redirectTo) {
        toast.success(`Acessando: ${storeName}`);
        router.push(result.redirectTo);
      } else {
        toast.error(result.error || "Erro ao selecionar loja.");
        setSelectedId(null);
      }
    });
  };

  return (
    <div className="grid grid-cols-1 md:grid-cols-3 gap-6 w-full">
      {stores.map((store) => {
        const isCurrentSelected = selectedId === store.id;
        
        return (
          <div
            key={store.id}
            onClick={() => !isPending && handleSelect(store.id, store.name)}
            className={`group relative glass rounded-3xl p-6 cursor-pointer glass-hover transition-all duration-300 flex flex-col justify-between min-h-[260px] ${
              isPending && !isCurrentSelected ? "opacity-40 pointer-events-none" : ""
            } ${
              isCurrentSelected ? "border-amber-500 bg-amber-500/[0.04] ring-1 ring-amber-500/20" : ""
            }`}
          >
            {/* Topo do card */}
            <div>
              <div className="flex items-center justify-between mb-5">
                <div className="w-12 h-12 rounded-2xl bg-white/[0.03] group-hover:bg-amber-500/10 group-hover:text-amber-400 flex items-center justify-center text-slate-400 transition-colors border border-white/5 group-hover:border-amber-500/20">
                  <Store className="w-6 h-6 stroke-[1.8]" />
                </div>
                {isCurrentSelected ? (
                  <Loader2 className="w-5 h-5 text-amber-500 animate-spin" />
                ) : (
                  <ArrowRight className="w-5 h-5 text-slate-500 group-hover:text-amber-400 transform group-hover:translate-x-1 transition-all" />
                )}
              </div>

              <h3 className="text-xl font-bold text-slate-100 group-hover:text-amber-400 transition-colors mb-4">
                {store.name}
              </h3>

              {/* Detalhes da loja */}
              <div className="space-y-2 text-slate-400 text-xs font-medium">
                <div className="flex items-start gap-2">
                  <MapPin className="w-4 h-4 text-slate-500 shrink-0 mt-0.5" />
                  <span>{store.address}</span>
                </div>
                <div className="flex items-center gap-2">
                  <Phone className="w-4 h-4 text-slate-500 shrink-0" />
                  <span>{store.phone}</span>
                </div>
                <div className="flex items-center gap-2">
                  <Building2 className="w-4 h-4 text-slate-500 shrink-0" />
                  <span>CNPJ: {store.cnpj}</span>
                </div>
              </div>
            </div>

            {/* Rodapé do card / Botão simulado */}
            <div className="mt-6 pt-4 border-t border-white/5 flex justify-end">
              <span className="text-xs font-bold uppercase tracking-wider text-amber-500/90 group-hover:text-amber-400 transition-colors">
                {isCurrentSelected ? "Conectando..." : "Selecionar Loja"}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}
