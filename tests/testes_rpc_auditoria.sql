-- Executar APOS o patch, na MESMA transacao, encerrada por ROLLBACK pelo chamador.
-- Nao executar o COMMIT de docs/correcoes-auditoria.sql na validacao.
-- LIKE nao copia FKs/triggers; nenhum dado real e copiado para as fixtures.
CREATE TEMP TABLE "Tenant" (LIKE public."Tenant" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "Store" (LIKE public."Store" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "User" (LIKE public."User" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "Category" (LIKE public."Category" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "Product" (LIKE public."Product" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "StoreProduct" (LIKE public."StoreProduct" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "Sale" (LIKE public."Sale" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "SaleItem" (LIKE public."SaleItem" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "StockMovement" (LIKE public."StockMovement" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP TABLE "OwnerStockAdjustment" (LIKE public."OwnerStockAdjustment" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES) ON COMMIT DROP;
CREATE TEMP SEQUENCE internal_barcode_seq;
SET LOCAL search_path = public;

DO $isolation$
DECLARE v_name TEXT;
BEGIN
  -- As RPCs usam SET search_path=public. O pg_temp implicito deve ganhar para relacoes.
  FOREACH v_name IN ARRAY ARRAY['Tenant','Store','User','Category','Product','StoreProduct',
    'Sale','SaleItem','StockMovement','OwnerStockAdjustment','internal_barcode_seq'] LOOP
    IF to_regclass(format('%I', v_name)) IS DISTINCT FROM to_regclass(format('pg_temp.%I', v_name)) THEN
      RAISE EXCEPTION 'Teste abortado: % nao resolve para pg_temp', v_name;
    END IF;
  END LOOP;
  IF EXISTS (
    SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public'
       AND p.proname IN ('get_loja_estoque','excluir_produto','criar_produto','get_categorias',
         'atualizar_produto','gerar_codigo_interno','get_conferencia_pao','ajustar_estoque',
         'get_vendas_rede','somar_estoque_loja')
       AND p.prosrc ~* '(FROM|JOIN|UPDATE|INTO|TRUNCATE)[[:space:]]+public\.'
  ) THEN
    RAISE EXCEPTION 'Teste abortado: uma RPC referencia tabelas public explicitamente';
  END IF;
END;
$isolation$;

INSERT INTO pg_temp."Tenant" (id,name,slug,"updatedAt") VALUES
  ('audit-tenant','Rede de teste','audit-tenant',now()),
  ('audit-other-tenant','Outra rede de teste','audit-other-tenant',now());
INSERT INTO pg_temp."Store" (id,name,address,phone,cnpj,"tenantId","updatedAt") VALUES
  ('audit-store','Loja de teste','','','','audit-tenant',now()),
  ('audit-other-store','Loja de outra rede','','','','audit-other-tenant',now());
INSERT INTO pg_temp."Category" (id,name,"tenantId","updatedAt") VALUES
  ('audit-category','Categoria teste','audit-tenant',now());
INSERT INTO pg_temp."User" (id,name,email,password,role,active,"tenantId","updatedAt") VALUES
  ('audit-owner','Dono teste','audit-owner@example.invalid',extensions.crypt('audit-password',extensions.gen_salt('bf',4)),'DONO',true,'audit-tenant',now()),
  ('audit-disabled','Desativado teste','audit-disabled@example.invalid',extensions.crypt('audit-password',extensions.gen_salt('bf',4)),'DONO',false,'audit-tenant',now()),
  ('audit-legacy-disabled','Desativado legado','audit-legacy-disabled@example.invalid','audit-password','DONO',false,'audit-tenant',now());
INSERT INTO pg_temp."Product" (id,name,barcode,"priceSale","priceCost",type,"unitMeasure","categoryId","tenantId","updatedAt") VALUES
  ('audit-product','Produto teste','audit-barcode',1000,500,'NORMAL','UN','audit-category','audit-tenant',now()),
  ('audit-foreign-product','Produto de outra rede','audit-foreign-barcode',1000,500,'NORMAL','UN','audit-category','audit-other-tenant',now());
INSERT INTO pg_temp."StoreProduct" (id,"productId","storeId",quantity,"updatedAt")
VALUES ('audit-stock','audit-product','audit-store',100,now());

DO $disabled_users$
DECLARE v_email TEXT; v_name TEXT; v_result JSONB;
BEGIN
  FOREACH v_email IN ARRAY ARRAY['audit-disabled@example.invalid','audit-legacy-disabled@example.invalid'] LOOP
    FOR v_name, v_result IN
      SELECT 'ajustar_estoque', public.ajustar_estoque(v_email,'audit-password','audit-store','audit-product',25)::jsonb
      UNION ALL SELECT 'excluir_produto', public.excluir_produto(v_email,'audit-password','audit-product')::jsonb
      UNION ALL SELECT 'criar_produto', public.criar_produto(v_email,'audit-password','Novo','NORMAL','UN',100,50,'audit-category','audit-new-barcode')::jsonb
      UNION ALL SELECT 'atualizar_produto', public.atualizar_produto(v_email,'audit-password','audit-product','Editado','NORMAL','UN',100,50,'audit-category','audit-barcode')::jsonb
      UNION ALL SELECT 'get_categorias', public.get_categorias(v_email,'audit-password')::jsonb
      UNION ALL SELECT 'get_loja_estoque', public.get_loja_estoque(v_email,'audit-password','audit-store')::jsonb
      UNION ALL SELECT 'gerar_codigo_interno', public.gerar_codigo_interno(v_email,'audit-password')::jsonb
      UNION ALL SELECT 'get_conferencia_pao', public.get_conferencia_pao(v_email,'audit-password','2026-10-12'::date,'audit-store')::jsonb
      UNION ALL SELECT 'get_vendas_rede', public.get_vendas_rede(v_email,'audit-password','2026-10-12'::timestamp,'2026-10-13'::timestamp,'audit-store')::jsonb
      UNION ALL SELECT 'somar_estoque_loja', public.somar_estoque_loja(v_email,'audit-password','audit-store','[{"productId":"audit-product","quantity":20}]'::jsonb)
    LOOP
      IF v_result->>'error' IS DISTINCT FROM 'invalid_credentials' THEN
        RAISE EXCEPTION '% aceitou usuario desativado (%): %', v_name, v_email, v_result;
      END IF;
    END LOOP;
  END LOOP;
END;
$disabled_users$;

DO $stock_scope$
DECLARE v_result JSONB; v_quantity DOUBLE PRECISION;
BEGIN
  v_result := public.ajustar_estoque('audit-owner@example.invalid','audit-password','audit-store','audit-foreign-product',25)::jsonb;
  IF v_result->>'error' IS DISTINCT FROM 'not_found' THEN
    RAISE EXCEPTION 'Ajuste aceitou produto de outra rede: %', v_result;
  END IF;
  FOREACH v_quantity IN ARRAY ARRAY[-1::double precision, 'NaN'::double precision, 'Infinity'::double precision, NULL::double precision] LOOP
    v_result := public.ajustar_estoque('audit-owner@example.invalid','audit-password','audit-store','audit-product',v_quantity)::jsonb;
    IF v_result->>'error' IS DISTINCT FROM 'invalid_quantity' THEN
      RAISE EXCEPTION 'Ajuste aceitou quantidade invalida: %', v_result;
    END IF;
  END LOOP;
  IF (SELECT quantity FROM pg_temp."StoreProduct" WHERE id='audit-stock') <> 100
     OR EXISTS (SELECT 1 FROM pg_temp."StockMovement")
     OR EXISTS (SELECT 1 FROM pg_temp."OwnerStockAdjustment") THEN
    RAISE EXCEPTION 'Operacao recusada alterou estoque/movimento/ajuste';
  END IF;
END;
$stock_scope$;

INSERT INTO pg_temp."Sale" (id,"storeId","userId","tenantId","saleDate",subtotal,total,"paymentMethod","paymentStatus")
SELECT 'audit-sale-'||g, 'audit-store','audit-owner','audit-tenant',timestamp '2026-10-12 12:00:00'+g*interval '1 second',1000,1000,'DINHEIRO','APROVADO'
FROM generate_series(1,501) g;
INSERT INTO pg_temp."SaleItem" (id,"saleId","productId",quantity,"priceUnit",subtotal,type)
SELECT 'audit-item-'||g, 'audit-sale-'||g, 'audit-product',1,1000,1000,'NORMAL'
FROM generate_series(1,501) g;

DO $complete_sales$
DECLARE v_result JSONB;
BEGIN
  v_result := public.get_vendas_rede('audit-owner@example.invalid','audit-password','2026-10-12'::timestamp,'2026-10-13'::timestamp,'audit-store');
  IF jsonb_array_length(v_result->'vendas') IS DISTINCT FROM 500
     OR (v_result#>>'{resumo,total_registros}')::int IS DISTINCT FROM 501
     OR (v_result#>>'{resumo,vendas_qtd}')::int IS DISTINCT FROM 501
     OR (v_result#>>'{resumo,faturamento_centavos}')::bigint IS DISTINCT FROM 501000 THEN
    RAISE EXCEPTION 'Total de 501 vendas incorreto: %', v_result->'resumo';
  END IF;
END;
$complete_sales$;

-- A tela havia lido 100; uma venda concorrente levou o saldo a 90 antes da reposicao.
UPDATE pg_temp."StoreProduct" SET quantity=90 WHERE id='audit-stock';
DO $atomic_addition$
DECLARE v_result JSONB; v_invalid JSONB;
BEGIN
  v_result := public.somar_estoque_loja('audit-owner@example.invalid','audit-password','audit-store','[{"productId":"audit-product","quantity":20}]'::jsonb);
  IF v_result->>'success' IS DISTINCT FROM 'true'
     OR (v_result#>>'{items,0,quantity}')::numeric IS DISTINCT FROM 110
     OR (SELECT quantity FROM pg_temp."StoreProduct" WHERE id='audit-stock') IS DISTINCT FROM 110::double precision THEN
    RAISE EXCEPTION 'Soma nao usou saldo atual 90 + 20: %', v_result;
  END IF;
  IF (SELECT count(*) FROM pg_temp."StockMovement" WHERE "productId"='audit-product' AND "storeId"='audit-store'
      AND type='ENTRADA' AND quantity=20 AND "balanceBefore"=90 AND "balanceAfter"=110) <> 1
     OR (SELECT count(*) FROM pg_temp."OwnerStockAdjustment" WHERE "productId"='audit-product' AND "storeId"='audit-store' AND quantity=110) <> 1 THEN
    RAISE EXCEPTION 'Soma nao registrou movimento e ajuste absoluto corretos';
  END IF;

  FOREACH v_invalid IN ARRAY ARRAY[
    '[{"productId":"audit-product","quantity":5},{"productId":"audit-foreign-product","quantity":1}]'::jsonb,
    '[{"productId":"audit-product","quantity":5},{"productId":"audit-product","quantity":1}]'::jsonb,
    '[{"productId":"audit-product","quantity":-1}]'::jsonb,
    '[{"productId":"audit-product","quantity":"Infinity"}]'::jsonb,
    '[{"productId":"audit-product","quantity":1e309}]'::jsonb,
    '[]'::jsonb, '{}'::jsonb, NULL::jsonb
  ] LOOP
    v_result := public.somar_estoque_loja('audit-owner@example.invalid','audit-password','audit-store',v_invalid);
    IF NOT (v_result ? 'error') THEN RAISE EXCEPTION 'Soma aceitou lote invalido: %', v_result; END IF;
  END LOOP;
  IF (SELECT quantity FROM pg_temp."StoreProduct" WHERE id='audit-stock') IS DISTINCT FROM 110::double precision
     OR (SELECT count(*) FROM pg_temp."StockMovement") <> 1
     OR (SELECT count(*) FROM pg_temp."OwnerStockAdjustment") <> 1 THEN
    RAISE EXCEPTION 'Lote invalido aplicou reposicao parcial';
  END IF;
END;
$atomic_addition$;

SELECT 'OK: isolamento temporario, usuario inativo, tenant, quantidades, 501 vendas e soma atomica' AS testes_rpc_auditoria;
