import { cookies } from "next/headers";
import { prisma } from "./prisma";

export interface UserSession {
  id: string;
  name: string;
  email: string;
  role: string; // "ATENDENTE", "GERENTE", "DONO"
  storeId: string | null;
  storeName: string | null;
  tenantId: string;
  tenantName: string;
}

/**
 * Obtém a sessão do usuário ativo dos cookies (Server Side)
 */
export async function getSession(): Promise<UserSession | null> {
  try {
    const cookieStore = await cookies();
    const sessionCookie = cookieStore.get("user_session");

    if (!sessionCookie || !sessionCookie.value) {
      return null;
    }

    // Decodifica a sessão simples
    const decoded = Buffer.from(sessionCookie.value, "base64").toString("utf-8");
    return JSON.parse(decoded) as UserSession;
  } catch (error) {
    console.error("Erro ao ler sessão:", error);
    return null;
  }
}

/**
 * Define a sessão do usuário nos cookies
 */
export async function setSession(user: {
  id: string;
  name: string;
  email: string;
  role: string;
  storeId: string | null;
  tenantId: string;
}): Promise<void> {
  const cookieStore = await cookies();
  
  let storeName = null;
  if (user.storeId) {
    const store = await prisma.store.findUnique({
      where: { id: user.storeId },
      select: { name: true },
    });
    storeName = store?.name || null;
  }

  const tenant = await prisma.tenant.findUnique({
    where: { id: user.tenantId },
    select: { name: true },
  });
  const tenantName = tenant?.name || "Rede Padaria";

  const sessionData: UserSession = {
    id: user.id,
    name: user.name,
    email: user.email,
    role: user.role,
    storeId: user.storeId,
    storeName,
    tenantId: user.tenantId,
    tenantName,
  };

  const encoded = Buffer.from(JSON.stringify(sessionData)).toString("base64");

  cookieStore.set("user_session", encoded, {
    httpOnly: true,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax",
    maxAge: 60 * 60 * 8, // 8 horas de turno
    path: "/",
  });
}

/**
 * Destrói a sessão atual (logout)
 */
export async function destroySession(): Promise<void> {
  const cookieStore = await cookies();
  cookieStore.delete("user_session");
}
