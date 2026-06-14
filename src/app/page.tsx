import { getSession } from "@/lib/auth";
import { redirect } from "next/navigation";

export const dynamic = "force-dynamic";

export default async function HomePage() {
  const session = await getSession();

  if (!session) {
    redirect("/login");
  }

  // Dono sem loja vinculada vai para seleção de loja
  if (session.role === "DONO" && !session.storeId) {
    redirect("/store-select");
  }

  // Demais perfis ou dono com loja selecionada vão para o PDV
  redirect("/pdv");
}
