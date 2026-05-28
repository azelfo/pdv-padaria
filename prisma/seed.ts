import "dotenv/config";
import { PrismaClient } from "@prisma/client";

const prisma = new PrismaClient();

async function main() {
  console.log("Iniciando limpeza do banco PostgreSQL...");
  
  // No PostgreSQL o deleteMany precisa respeitar a ordem de chaves estrangeiras
  await prisma.stockMovement.deleteMany({});
  await prisma.saleItem.deleteMany({});
  await prisma.sale.deleteMany({});
  await prisma.breadConfig.deleteMany({});
  await prisma.storeProduct.deleteMany({});
  await prisma.product.deleteMany({});
  await prisma.category.deleteMany({});
  await prisma.user.deleteMany({});
  await prisma.store.deleteMany({});
  await prisma.tenant.deleteMany({});

  console.log("Cadastrando Tenant SaaS de Testes...");
  
  const tenantPremium = await prisma.tenant.create({
    data: {
      name: "Rede Premium Padaria",
      slug: "padaria-premium",
    },
  });

  console.log("Lojas cadastrando sob o Tenant...");

  const storeCentro = await prisma.store.create({
    data: {
      name: "Padaria Centro",
      address: "Av. Central, 100 - Centro",
      phone: "(11) 98765-4321",
      cnpj: "12.345.678/0001-01",
      tenantId: tenantPremium.id,
    },
  });

  const storeBairroX = await prisma.store.create({
    data: {
      name: "Padaria Bairro X",
      address: "Rua das Flores, 250 - Bairro X",
      phone: "(11) 98765-4322",
      cnpj: "12.345.678/0002-02",
      tenantId: tenantPremium.id,
    },
  });

  const storeBairroY = await prisma.store.create({
    data: {
      name: "Padaria Bairro Y",
      address: "Av. Brasil, 800 - Bairro Y",
      phone: "(11) 98765-4323",
      cnpj: "12.345.678/0003-03",
      tenantId: tenantPremium.id,
    },
  });

  console.log("Usuários cadastrando sob o Tenant...");

  await prisma.user.create({
    data: {
      name: "Marcelo Dono",
      email: "dono@padaria.com.br",
      password: "123",
      role: "DONO",
      tenantId: tenantPremium.id,
    },
  });

  await prisma.user.create({
    data: {
      name: "Carlos Gerente",
      email: "gerente.centro@padaria.com.br",
      password: "123",
      role: "GERENTE",
      storeId: storeCentro.id,
      tenantId: tenantPremium.id,
    },
  });

  const atendenteCentro = await prisma.user.create({
    data: {
      name: "Aline Caixa",
      email: "atendente.centro@padaria.com.br",
      password: "123",
      role: "ATENDENTE",
      storeId: storeCentro.id,
      tenantId: tenantPremium.id,
    },
  });

  console.log("Categorias de Padaria & Mercadinho cadastrando...");

  const catProducao = await prisma.category.create({
    data: {
      name: "Produção Própria",
      tenantId: tenantPremium.id,
    },
  });

  const catBebidas = await prisma.category.create({
    data: {
      name: "Bebidas",
      tenantId: tenantPremium.id,
    },
  });

  const catMercearia = await prisma.category.create({
    data: {
      name: "Mercearia",
      tenantId: tenantPremium.id,
    },
  });

  const catHigiene = await prisma.category.create({
    data: {
      name: "Higiene",
      tenantId: tenantPremium.id,
    },
  });

  const catLimpeza = await prisma.category.create({
    data: {
      name: "Limpeza",
      tenantId: tenantPremium.id,
    },
  });

  console.log("Produtos expandidos cadastrando...");

  // 1. Produção Própria (Padaria)
  const prodPao = await prisma.product.create({
    data: {
      name: "Pão Francês Quente",
      barcode: "100000000001",
      priceSale: 50,
      priceCost: 15,
      type: "PAO_FRANCES",
      unitMeasure: "UN",
      categoryId: catProducao.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodCoxinha = await prisma.product.create({
    data: {
      name: "Coxinha de Frango c/ Catupiry",
      barcode: "100000000002",
      priceSale: 600,
      priceCost: 250,
      type: "SALGADO",
      unitMeasure: "UN",
      categoryId: catProducao.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodBoloCenoura = await prisma.product.create({
    data: {
      name: "Bolo de Cenoura c/ Chocolate",
      barcode: "100000000003",
      priceSale: 800,
      priceCost: 300,
      type: "BOLO",
      unitMeasure: "FATIA",
      categoryId: catProducao.id,
      tenantId: tenantPremium.id,
    },
  });

  // 2. Bebidas
  const prodCoca350 = await prisma.product.create({
    data: {
      name: "Coca-Cola Lata 350ml",
      barcode: "789100010010",
      priceSale: 500,
      priceCost: 220,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catBebidas.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodSucoLaranja = await prisma.product.create({
    data: {
      name: "Suco de Laranja Natural 1L",
      barcode: "789100010020",
      priceSale: 1000,
      priceCost: 450,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catBebidas.id,
      tenantId: tenantPremium.id,
    },
  });

  // 3. Mercearia (Mercadinho)
  const prodArroz = await prisma.product.create({
    data: {
      name: "Arroz Agulhinha Tipo 1 - 1kg",
      barcode: "789102030001",
      priceSale: 650,
      priceCost: 380,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catMercearia.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodOleo = await prisma.product.create({
    data: {
      name: "Óleo de Soja Pet 900ml",
      barcode: "789102030002",
      priceSale: 720,
      priceCost: 410,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catMercearia.id,
      tenantId: tenantPremium.id,
    },
  });

  // 4. Higiene
  const prodCremeDental = await prisma.product.create({
    data: {
      name: "Creme Dental Tripla Ação 90g",
      barcode: "789103040001",
      priceSale: 420,
      priceCost: 190,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catHigiene.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodSabonete = await prisma.product.create({
    data: {
      name: "Sabonete Barra Leve 90g",
      barcode: "789103040002",
      priceSale: 250,
      priceCost: 100,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catHigiene.id,
      tenantId: tenantPremium.id,
    },
  });

  // 5. Limpeza
  const prodDetergente = await prisma.product.create({
    data: {
      name: "Detergente Líquido Neutro 500ml",
      barcode: "789104050001",
      priceSale: 280,
      priceCost: 120,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catLimpeza.id,
      tenantId: tenantPremium.id,
    },
  });

  const prodSabaoPo = await prisma.product.create({
    data: {
      name: "Sabão em Pó Caixa 800g",
      barcode: "789104050002",
      priceSale: 1450,
      priceCost: 820,
      type: "NORMAL",
      unitMeasure: "UN",
      categoryId: catLimpeza.id,
      tenantId: tenantPremium.id,
    },
  });

  console.log("Configurações do pão francês...");

  const faixasPao = JSON.stringify([
    { ate: 50, qtd: 1 },
    { ate: 100, qtd: 3 },
    { ate: 150, qtd: 4 },
    { ate: 200, qtd: 6 },
    { ate: 250, qtd: 7 },
    { ate: 300, qtd: 9 },
    { ate: 500, qtd: 15 },
    { ate: 1000, qtd: 30 },
  ]);

  const lojas = [storeCentro, storeBairroX, storeBairroY];

  for (const store of lojas) {
    await prisma.breadConfig.create({
      data: {
        storeId: store.id,
        priceUnit: 50,
        brackets: faixasPao,
      },
    });
  }

  console.log("Estoque individual por loja cadastrando com auditoria de reposição...");

  const estoqueItens = [
    { product: prodPao, qtd: 600, min: 60 },
    { product: prodCoxinha, qtd: 45, min: 8 },
    { product: prodBoloCenoura, qtd: 12, min: 2 },
    { product: prodCoca350, qtd: 150, min: 25 },
    { product: prodSucoLaranja, qtd: 30, min: 5 },
    { product: prodArroz, qtd: 80, min: 15 },
    { product: prodOleo, qtd: 50, min: 10 },
    { product: prodCremeDental, qtd: 40, min: 8 },
    { product: prodSabonete, qtd: 60, min: 12 },
    { product: prodDetergente, qtd: 70, min: 15 },
    { product: prodSabaoPo, qtd: 35, min: 5 },
  ];

  for (const store of lojas) {
    for (const item of estoqueItens) {
      await prisma.storeProduct.create({
        data: {
          storeId: store.id,
          productId: item.product.id,
          quantity: item.qtd,
          minStock: item.min,
        },
      });

      await prisma.stockMovement.create({
        data: {
          storeId: store.id,
          productId: item.product.id,
          userId: atendenteCentro.id,
          tenantId: tenantPremium.id,
          type: "ENTRADA",
          quantity: item.qtd,
          reason: "REPOSICAO",
        },
      });
    }
  }

  console.log("População de SaaS Multitenant realizada com absoluto sucesso no Supabase!");
}

main()
  .catch((e) => {
    console.error("Erro executando o seed:", e);
    process.exit(1);
  })
  .finally(async () => {
    await prisma.$disconnect();
  });
