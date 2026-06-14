import { prisma } from "@/lib/prisma";
import { NextResponse } from "next/server";

export const dynamic = "force-dynamic";

interface SaleInput {
  id: string;
  storeId: string;
  userId: string;
  tenantId: string;
  saleDate: string;
  subtotal: number;
  discount: number;
  total: number;
  paymentMethod: string;
  paymentStatus: string;
  receivedAmount: number | null;
  changeAmount: number | null;
  externalTxId: string | null;
  nsuTx: string | null;
  receiptUrl: string | null;
  notes: string | null;
  items: {
    id: string;
    productId: string;
    quantity: number;
    priceUnit: number;
    subtotal: number;
    type: string;
    details: string | null;
  }[];
  stockMovements: {
    id: string;
    productId: string;
    userId: string;
    type: string;
    quantity: number;
    reason: string;
    createdAt: string;
  }[];
}

export async function POST(request: Request) {
  try {
    const { sales }: { sales: SaleInput[] } = await request.json();

    if (!sales || !Array.isArray(sales)) {
      return NextResponse.json(
        { error: "O campo sales deve ser um array." },
        { status: 400 }
      );
    }

    const successIds: string[] = [];
    const errors: { id: string; error: string }[] = [];

    // Processa cada venda individualmente
    for (const sale of sales) {
      try {
        await prisma.$transaction(async (tx) => {
          // Verifica se a venda já existe no banco remoto (idempotência)
          const existingSale = await tx.sale.findUnique({
            where: { id: sale.id },
          });

          if (existingSale) {
            // Se já existe, apenas atualizamos o status de pagamento e outros metadados, caso necessário
            await tx.sale.update({
              where: { id: sale.id },
              data: {
                paymentStatus: sale.paymentStatus,
                externalTxId: sale.externalTxId,
                nsuTx: sale.nsuTx,
                receiptUrl: sale.receiptUrl,
                isSynced: true,
                syncedAt: new Date(),
              },
            });
            return;
          }

          // Cria a venda e seus itens relacionados na nuvem
          await tx.sale.create({
            data: {
              id: sale.id,
              storeId: sale.storeId,
              userId: sale.userId,
              tenantId: sale.tenantId,
              saleDate: new Date(sale.saleDate),
              subtotal: sale.subtotal,
              discount: sale.discount,
              total: sale.total,
              paymentMethod: sale.paymentMethod,
              paymentStatus: sale.paymentStatus,
              receivedAmount: sale.receivedAmount,
              changeAmount: sale.changeAmount,
              externalTxId: sale.externalTxId,
              nsuTx: sale.nsuTx,
              receiptUrl: sale.receiptUrl,
              notes: sale.notes,
              isSynced: true,
              syncedAt: new Date(),
              items: {
                createMany: {
                  data: sale.items.map((item) => ({
                    id: item.id,
                    productId: item.productId,
                    quantity: item.quantity,
                    priceUnit: item.priceUnit,
                    subtotal: item.subtotal,
                    type: item.type,
                    details: item.details,
                  })),
                },
              },
            },
          });

          // Cria as movimentações de estoque associadas se houver
          if (sale.stockMovements && sale.stockMovements.length > 0) {
            for (const movement of sale.stockMovements) {
              const existingMovement = await tx.stockMovement.findUnique({
                where: { id: movement.id },
              });

              if (!existingMovement) {
                await tx.stockMovement.create({
                  data: {
                    id: movement.id,
                    productId: movement.productId,
                    storeId: sale.storeId,
                    userId: movement.userId,
                    tenantId: sale.tenantId,
                    type: movement.type,
                    quantity: movement.quantity,
                    reason: movement.reason,
                    saleId: sale.id,
                    createdAt: new Date(movement.createdAt),
                    isSynced: true,
                    syncedAt: new Date(),
                  },
                });
              }
            }
          }
        });

        successIds.push(sale.id);
      } catch (err: any) {
        console.error(`Erro ao processar venda ${sale.id}:`, err);
        errors.push({ id: sale.id, error: err.message });
      }
    }

    return NextResponse.json({
      success: true,
      successIds,
      errors,
    });
  } catch (error: any) {
    console.error("Erro geral no Sync Push:", error);
    return NextResponse.json(
      { error: "Erro interno no servidor.", details: error.message },
      { status: 500 }
    );
  }
}
