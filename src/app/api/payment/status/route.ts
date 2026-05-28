import { NextResponse } from "next/server";
import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";

export async function GET(request: Request) {
  try {
    const session = await getSession();

    if (!session) {
      return NextResponse.json(
        { error: "Sessão inválida ou expirada." },
        { status: 401 }
      );
    }

    const { searchParams } = new URL(request.url);
    const saleId = searchParams.get("saleId");

    if (!saleId) {
      return NextResponse.json(
        { error: "saleId obrigatório na query string." },
        { status: 400 }
      );
    }

    // Busca a venda com detalhes
    const sale = await prisma.sale.findUnique({
      where: { id: saleId },
      include: {
        store: true,
        user: true,
        items: {
          include: {
            product: true,
          },
        },
      },
    });

    if (!sale) {
      return NextResponse.json(
        { error: "Venda não encontrada no banco de dados." },
        { status: 404 }
      );
    }

    // Retorna o status do pagamento e os dados do recibo para agilizar a exibição
    return NextResponse.json({
      success: true,
      status: sale.paymentStatus, // "PENDENTE", "APROVADO", "NEGADO", "CANCELADO"
      receiptData: sale,
    });
  } catch (error) {
    console.error("Erro na API Route /api/payment/status:", error);
    return NextResponse.json(
      { error: "Erro interno no servidor ao consultar o status." },
      { status: 500 }
    );
  }
}
