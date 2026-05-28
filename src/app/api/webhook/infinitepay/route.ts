import { NextResponse } from "next/server";
import { prisma } from "@/lib/prisma";

export async function POST(request: Request) {
  try {
    const payload = await request.json();
    console.log("[InfinitePay Webhook] Payload recebido:", payload);

    const {
      order_nsu, // Nosso saleId
      transaction_nsu, // ID da transação na InfinitePay
      paid_amount,
      capture_method,
      receipt_url,
    } = payload;

    if (!order_nsu) {
      return NextResponse.json(
        { error: "order_nsu (saleId) não fornecido no payload." },
        { status: 400 }
      );
    }

    // 1. Localiza a venda correspondente no banco
    const sale = await prisma.sale.findUnique({
      where: { id: order_nsu },
    });

    if (!sale) {
      console.warn(`[InfinitePay Webhook] Venda com ID ${order_nsu} não localizada no banco.`);
      return NextResponse.json(
        { error: "Venda correspondente não encontrada." },
        { status: 404 }
      );
    }

    // Idempotência: Se já foi aprovado, apenas retorna sucesso
    if (sale.paymentStatus === "APROVADO") {
      console.log(`[InfinitePay Webhook] Venda ${order_nsu} já se encontra como APROVADA. Ignorando.`);
      return NextResponse.json({ success: true, message: "Já processado." });
    }

    // 2. Atualiza os dados de pagamento na venda
    await prisma.sale.update({
      where: { id: order_nsu },
      data: {
        paymentStatus: "APROVADO",
        externalTxId: transaction_nsu || sale.externalTxId,
        nsuTx: transaction_nsu || sale.nsuTx,
        receiptUrl: receipt_url || sale.receiptUrl,
        receivedAmount: paid_amount || sale.total, // O valor pago eletronicamente
      },
    });

    console.log(`[InfinitePay Webhook] Venda ${order_nsu} atualizada com sucesso para APROVADO!`);

    return NextResponse.json({ success: true, message: "Webhook processado e venda aprovada." });
  } catch (error) {
    console.error("Erro no processamento do Webhook da InfinitePay:", error);
    return NextResponse.json(
      { error: "Erro interno no servidor ao processar o webhook." },
      { status: 500 }
    );
  }
}
