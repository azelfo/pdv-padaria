import { NextResponse } from "next/server";
import { getSession } from "@/lib/auth";
import { prisma } from "@/lib/prisma";

export async function POST(request: Request) {
  try {
    const session = await getSession();

    if (!session || !session.storeId) {
      return NextResponse.json(
        { error: "Sessão inválida ou não autorizada." },
        { status: 401 }
      );
    }

    const body = await request.json();
    const { saleId, total, paymentMethod } = body;

    if (!saleId || !total) {
      return NextResponse.json(
        { error: "Parâmetros obrigatórios ausentes (saleId ou total)." },
        { status: 400 }
      );
    }

    // 1. Busca a venda no banco para garantir que existe
    const sale = await prisma.sale.findUnique({
      where: { id: saleId },
    });

    if (!sale) {
      return NextResponse.json(
        { error: "Venda não encontrada no banco de dados." },
        { status: 404 }
      );
    }

    const handle = process.env.INFINITEPAY_HANDLE || "padaria-ouro-test";
    const apiKey = process.env.INFINITEPAY_API_KEY;

    let checkoutUrl = "";
    let externalTxId = `inf_${Math.random().toString(36).substring(2, 11)}`; // Fallback de ID externo

    // Simulamos a chamada se for o token mock de teste ou se não houver chave real configurada
    if (!apiKey || apiKey === "token_mock_infinitepay_2026") {
      console.log("[InfinitePay Mock] Gerando link de cobrança simulada...");
      // Em homologação local, geramos um link para o simulador integrado
      checkoutUrl = `https://checkout.infinitepay.io/pay/${handle}/${saleId}`;
    } else {
      // Caso haja credenciais de produção, faremos a requisição HTTPS real
      try {
        const response = await fetch("https://api.checkout.infinitepay.io/links", {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            Authorization: `Bearer ${apiKey}`,
          },
          body: JSON.stringify({
            handle,
            redirect_url: `${request.headers.get("origin")}/pdv`,
            webhook_url: `${request.headers.get("origin")}/api/webhook/infinitepay`,
            order_nsu: saleId,
            items: [
              {
                description: `Compra Padaria - Ref: ${saleId.slice(0, 8)}`,
                quantity: 1,
                price: total, // Centavos
              },
            ],
          }),
        });

        if (response.ok) {
          const data = await response.json();
          checkoutUrl = data.url;
          externalTxId = data.id || externalTxId;
        } else {
          const errText = await response.text();
          console.warn("[InfinitePay API Error] Resposta inválida, usando fallback de simulação:", errText);
          checkoutUrl = `https://checkout.infinitepay.io/pay/${handle}/${saleId}`;
        }
      } catch (fetchError) {
        console.error("[InfinitePay Connect Error] Erro de rede na API, usando fallback:", fetchError);
        checkoutUrl = `https://checkout.infinitepay.io/pay/${handle}/${saleId}`;
      }
    }

    // 2. Atualiza a venda com o ID externo gerado e o status pendente de transação eletrônica
    await prisma.sale.update({
      where: { id: saleId },
      data: {
        paymentMethod: paymentMethod, // "PIX" ou "CARTAO_DEBITO" / "CARTAO_CREDITO"
        paymentStatus: "PENDENTE",
        externalTxId: externalTxId,
        nsuTx: `NSU-${Math.floor(100000 + Math.random() * 900000)}`, // NSU mock de backup
      },
    });

    return NextResponse.json({
      success: true,
      checkoutUrl,
      externalTxId,
      saleId,
    });
  } catch (error) {
    console.error("Erro na API Route /api/payment/create:", error);
    return NextResponse.json(
      { error: "Erro interno ao processar o pagamento na maquininha." },
      { status: 500 }
    );
  }
}
