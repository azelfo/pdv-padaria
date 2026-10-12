-- Auditoria 12/10/2026. Patch independente, conferido com as definicoes vivas.
-- Aplicar somente este arquivo; supabase_rpcs.sql tambem contem rascunho de ledger.
-- Nao altera push_vendas/push_estoque, politicas anon_read ou pagamentos.
BEGIN;

ALTER FUNCTION public.ean13_check_digit(TEXT) SET search_path = pg_catalog;

-- Preserva as definicoes vivas (inclusive situacao/jaLancado e conferencia de pao).
DO $auth_active$
DECLARE
  v_name TEXT; v_oid OID; v_definition TEXT; v_new TEXT;
BEGIN
  FOREACH v_name IN ARRAY ARRAY['get_loja_estoque', 'excluir_produto', 'criar_produto',
    'get_categorias', 'atualizar_produto', 'gerar_codigo_interno', 'get_conferencia_pao']
  LOOP
    SELECT p.oid INTO STRICT v_oid FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
     WHERE n.nspname = 'public' AND p.proname = v_name;
    v_definition := pg_get_functiondef(v_oid);
    v_new := regexp_replace(v_definition,
      '(WHERE[[:space:]]+email[[:space:]]*=[[:space:]]*p_email)([[:space:]]+AND[[:space:]]+password[[:space:]]*=)',
      '\1 AND active = true\2', 'gi');
    IF v_new = v_definition AND v_definition !~* 'WHERE[[:space:]]+email[[:space:]]*=[[:space:]]*p_email[[:space:]]+AND[[:space:]]+active[[:space:]]*=[[:space:]]*true' THEN
      RAISE EXCEPTION 'Autenticacao de % mudou; revise a definicao antes de aplicar', v_name;
    END IF;
    EXECUTE v_new;
  END LOOP;
END;
$auth_active$;

CREATE OR REPLACE FUNCTION ajustar_estoque(
  p_email           TEXT,
  p_password        TEXT,
  p_store_id        TEXT,
  p_product_id      TEXT,
  p_nova_quantidade FLOAT,
  p_motivo          TEXT DEFAULT 'AJUSTE_MANUAL'
)
RETURNS JSON
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_user_id   TEXT;
  v_role      TEXT;
  v_tenant_id TEXT;
  v_store_ok  BOOLEAN;
  v_qty_atual FLOAT := 0;
  v_diff      FLOAT;
BEGIN
  -- Verifica credenciais
  SELECT id, role, "tenantId"
    INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email AND active = true
     AND password = extensions.crypt(p_password, password)
   LIMIT 1;

  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId"
      INTO v_user_id, v_role, v_tenant_id
      FROM "User"
     WHERE email = p_email AND active = true AND password = p_password
     LIMIT 1;
  END IF;

  IF v_user_id IS NULL THEN
    RETURN json_build_object('error', 'invalid_credentials');
  END IF;

  IF v_role != 'DONO' THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  SELECT EXISTS(
    SELECT 1 FROM "Store" WHERE id = p_store_id AND "tenantId" = v_tenant_id AND active = true
  ) INTO v_store_ok;

  IF NOT v_store_ok THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM "Product"
     WHERE id = p_product_id AND "tenantId" = v_tenant_id AND active = true
  ) THEN
    RETURN json_build_object('error', 'not_found');
  END IF;
  IF p_nova_quantidade IS NULL OR p_nova_quantidade < 0
     OR p_nova_quantidade::text IN ('NaN', 'Infinity', '-Infinity') THEN
    RETURN json_build_object('error', 'invalid_quantity');
  END IF;

  INSERT INTO "StoreProduct" ("id", "productId", "storeId", quantity, "minStock", "updatedAt")
  VALUES (gen_random_uuid()::text, p_product_id, p_store_id, 0, 0, NOW())
  ON CONFLICT ("storeId", "productId") DO NOTHING;

  -- Quantidade atual
  SELECT quantity INTO v_qty_atual
    FROM "StoreProduct"
   WHERE "storeId" = p_store_id AND "productId" = p_product_id
   FOR UPDATE;

  IF v_qty_atual IS NULL THEN v_qty_atual := 0; END IF;

  v_diff := p_nova_quantidade - v_qty_atual;

  -- Atualiza (ou cria) registro de estoque por loja
  INSERT INTO "StoreProduct" ("id", "productId", "storeId", "quantity", "minStock", "updatedAt")
  VALUES (
    gen_random_uuid()::text,
    p_product_id,
    p_store_id,
    p_nova_quantidade,
    0,
    NOW()
  )
  ON CONFLICT ("storeId", "productId")
  DO UPDATE SET quantity = p_nova_quantidade, "updatedAt" = NOW();

  -- Registra movimento de estoque
  IF v_diff != 0 THEN
    INSERT INTO "StockMovement"
      ("id","productId","storeId","userId","tenantId","type","quantity","reason","createdAt","isSynced","balanceBefore","balanceAfter")
    VALUES (
      gen_random_uuid()::text,
      p_product_id,
      p_store_id,
      v_user_id,
      v_tenant_id,
      'AJUSTE',
      ABS(v_diff),
      p_motivo,
      NOW(),
      true,
      v_qty_atual,
      p_nova_quantidade
    );
  END IF;

  -- Mesmo canal do set_estoque_loja: o PDV da loja aplica este ajuste no próximo sync
  -- (ApplyOwnerAdjustmentsAsync lê OwnerStockAdjustment). Sem isto, o ajuste feito pelo
  -- painel web não desceria para o PDV da loja.
  INSERT INTO "OwnerStockAdjustment" ("id","tenantId","storeId","productId","quantity","minStock","createdBy","createdAt")
  VALUES (gen_random_uuid()::text, v_tenant_id, p_store_id, p_product_id, p_nova_quantidade, NULL, v_user_id, clock_timestamp());

  RETURN json_build_object('success', true, 'nova_quantidade', p_nova_quantidade);
END;
$$;

CREATE OR REPLACE FUNCTION get_vendas_rede(
  p_email    TEXT,
  p_password TEXT,
  p_from     TIMESTAMP,
  p_to       TIMESTAMP,
  p_store_id TEXT DEFAULT NULL,
  p_payment  TEXT DEFAULT 'TODOS'
)
RETURNS JSONB LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_tenant TEXT; v_role TEXT; v_result JSONB;
BEGIN
  SELECT u."tenantId", u.role INTO v_tenant, v_role
    FROM "User" u
   WHERE u.email = p_email AND u.active = true
     AND u.password = extensions.crypt(p_password, u.password)
   LIMIT 1;

  IF v_tenant IS NULL THEN RETURN jsonb_build_object('error','invalid_credentials'); END IF;
  IF v_role NOT IN ('DONO','GERENTE') THEN RETURN jsonb_build_object('error','forbidden'); END IF;

  WITH base AS (
    SELECT s.id, s."storeId", s."saleDate", s.total, s."paymentMethod", s."paymentStatus",
           st.name AS loja_nome,
           COALESCE((SELECT sum(si.quantity) FROM "SaleItem" si WHERE si."saleId" = s.id),0) AS itens
    FROM "Sale" s
    JOIN "Store" st ON st.id = s."storeId"
    WHERE s."tenantId" = v_tenant
      AND s."saleDate" >= p_from AND s."saleDate" <= p_to
      AND (p_store_id IS NULL OR s."storeId" = p_store_id)
      AND (p_payment = 'TODOS' OR s."paymentMethod" = p_payment)
  ), recentes AS (
    SELECT * FROM base
    ORDER BY "saleDate" DESC, id
    LIMIT 500
  )
  SELECT jsonb_build_object('vendas', COALESCE((SELECT jsonb_agg(jsonb_build_object(
    'id', b.id, 'storeId', b."storeId", 'data', b."saleDate", 'total_centavos', b.total,
    'metodo', b."paymentMethod", 'status', b."paymentStatus",
    'loja', b.loja_nome, 'itens', b.itens) ORDER BY b."saleDate" DESC, b.id)
    FROM recentes b), '[]'::jsonb),
    'resumo', (SELECT jsonb_build_object(
      'total_registros', count(*),
      'vendas_qtd', count(*) FILTER (WHERE "paymentStatus" = 'APROVADO'),
      'faturamento_centavos', COALESCE(sum(total) FILTER (WHERE "paymentStatus" = 'APROVADO'), 0)
    ) FROM base))
  INTO v_result;

  RETURN v_result;
END;
$$;

CREATE OR REPLACE FUNCTION public.somar_estoque_loja(
  p_email TEXT, p_password TEXT, p_store_id TEXT, p_items JSONB
)
RETURNS JSONB LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_user_id TEXT; v_tenant TEXT; v_role TEXT; v_item JSONB;
  v_delta DOUBLE PRECISION; v_after DOUBLE PRECISION; v_items JSONB := '[]'::jsonb;
BEGIN
  SELECT u.id, u."tenantId", u.role INTO v_user_id, v_tenant, v_role
    FROM "User" u JOIN "Tenant" t ON t.id = u."tenantId" AND t.active = true
   WHERE u.email = p_email AND u.active = true
     AND CASE WHEN u.password LIKE '$2a$%' OR u.password LIKE '$2b$%' OR u.password LIKE '$2y$%'
       THEN u.password = extensions.crypt(p_password, u.password)
       ELSE u.password = p_password END
   LIMIT 1;
  IF v_user_id IS NULL THEN RETURN jsonb_build_object('error', 'invalid_credentials'); END IF;
  IF v_role <> 'DONO' THEN RETURN jsonb_build_object('error', 'forbidden'); END IF;
  IF NOT EXISTS (SELECT 1 FROM "Store" WHERE id = p_store_id AND "tenantId" = v_tenant AND active = true) THEN
    RETURN jsonb_build_object('error', 'store_not_found');
  END IF;
  IF p_items IS NULL OR jsonb_typeof(p_items) <> 'array' THEN
    RETURN jsonb_build_object('error', 'invalid_payload');
  END IF;
  IF jsonb_array_length(p_items) = 0 OR EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_items) i GROUP BY i->>'productId' HAVING count(*) > 1
  ) THEN RETURN jsonb_build_object('error', 'invalid_payload'); END IF;

  -- Valida o lote inteiro antes de gravar para nunca retornar erro apos aplicar parte.
  FOR v_item IN SELECT value FROM jsonb_array_elements(p_items) LOOP
    IF NOT EXISTS (SELECT 1 FROM "Product" WHERE id = v_item->>'productId' AND "tenantId" = v_tenant AND active = true) THEN
      RETURN jsonb_build_object('error', 'product_not_found');
    END IF;
    IF jsonb_typeof(v_item->'quantity') IS DISTINCT FROM 'number' THEN
      RETURN jsonb_build_object('error', 'invalid_quantity');
    END IF;
    IF (v_item->>'quantity')::numeric < 0 OR (v_item->>'quantity')::numeric > 1.7976931348623157e308::numeric THEN
      RETURN jsonb_build_object('error', 'invalid_quantity');
    END IF;
  END LOOP;

  FOR v_item IN SELECT value FROM jsonb_array_elements(p_items) ORDER BY value->>'productId' LOOP
    v_delta := (v_item->>'quantity')::double precision;
    INSERT INTO "StoreProduct" AS stock (id, "productId", "storeId", quantity, "minStock", "updatedAt")
    VALUES (gen_random_uuid()::text, v_item->>'productId', p_store_id, v_delta, 0, clock_timestamp())
    ON CONFLICT ("storeId", "productId") DO UPDATE
      SET quantity = stock.quantity + excluded.quantity, "updatedAt" = clock_timestamp()
    RETURNING quantity INTO v_after;

    IF v_delta > 0 THEN
      INSERT INTO "StockMovement" (id,"productId","storeId","userId","tenantId",type,quantity,reason,"createdAt","isSynced","balanceBefore","balanceAfter")
      VALUES (gen_random_uuid()::text, v_item->>'productId', p_store_id, v_user_id, v_tenant,
        'ENTRADA', v_delta, 'REPOSICAO', clock_timestamp(), true, v_after - v_delta, v_after);
    END IF;
    INSERT INTO "OwnerStockAdjustment" (id,"tenantId","storeId","productId",quantity,"minStock","createdBy","createdAt")
    VALUES (gen_random_uuid()::text, v_tenant, p_store_id, v_item->>'productId', v_after, NULL, v_user_id, clock_timestamp());
    v_items := v_items || jsonb_build_array(jsonb_build_object('productId', v_item->>'productId', 'quantity', v_after));
  END LOOP;
  RETURN jsonb_build_object('success', true, 'items', v_items);
END;
$$;
REVOKE ALL ON FUNCTION public.somar_estoque_loja(TEXT, TEXT, TEXT, JSONB) FROM PUBLIC, authenticated;
GRANT EXECUTE ON FUNCTION public.somar_estoque_loja(TEXT, TEXT, TEXT, JSONB) TO anon;

COMMIT;
