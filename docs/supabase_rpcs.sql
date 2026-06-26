-- ============================================================
-- PADARIA VENÂNCIO — RPCs para Ajuste de Estoque via Web/Mobile
-- Execute no SQL Editor do Supabase (uma vez).
-- Requer extensão pgcrypto (habilitada por padrão no Supabase).
-- IMPORTANTE: no Supabase o pgcrypto fica no schema "extensions", então
-- crypt() precisa ser qualificado como extensions.crypt() (search_path=public
-- não enxerga o schema extensions). Sem isso, a RPC falha com erro 42883.
-- ============================================================

-- ----------------------------------------------------------------
-- RPC 1: get_loja_estoque
-- Lista os produtos de uma loja com quantidades atuais.
-- Usado pela aba Estoque do painel web para exibir e editar.
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION get_loja_estoque(
  p_email     TEXT,
  p_password  TEXT,
  p_store_id  TEXT
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
BEGIN
  -- Verifica credenciais (BCrypt via pgcrypto)
  SELECT id, role, "tenantId"
    INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email
     AND password = extensions.crypt(p_password, password)
   LIMIT 1;

  -- Fallback plaintext (migração)
  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId"
      INTO v_user_id, v_role, v_tenant_id
      FROM "User"
     WHERE email = p_email AND password = p_password
     LIMIT 1;
  END IF;

  IF v_user_id IS NULL THEN
    RETURN json_build_object('error', 'invalid_credentials');
  END IF;

  IF v_role != 'DONO' THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  -- Confirma que a loja pertence ao tenant do dono
  SELECT EXISTS(
    SELECT 1 FROM "Store" WHERE id = p_store_id AND "tenantId" = v_tenant_id
  ) INTO v_store_ok;

  IF NOT v_store_ok THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  RETURN (
    SELECT json_build_object(
      'produtos', COALESCE(
        json_agg(
          json_build_object(
            'productId',   p.id,
            'nome',        p.name,
            'tipo',        p.type,
            'unitMeasure', p."unitMeasure",
            'quantidade',  COALESCE(sp.quantity, 0),
            'minimo',      COALESCE(sp."minStock", 0)
          )
          ORDER BY p.name
        ),
        '[]'::json
      )
    )
    FROM "Product" p
    LEFT JOIN "StoreProduct" sp
           ON sp."productId" = p.id
          AND sp."storeId"   = p_store_id
    WHERE p."tenantId" = v_tenant_id
      AND p.active = true
  );
END;
$$;


-- ----------------------------------------------------------------
-- RPC 2: ajustar_estoque
-- Atualiza a quantidade de um produto em uma loja e registra
-- um StockMovement do tipo AJUSTE.
-- ----------------------------------------------------------------
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
   WHERE email = p_email
     AND password = extensions.crypt(p_password, password)
   LIMIT 1;

  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId"
      INTO v_user_id, v_role, v_tenant_id
      FROM "User"
     WHERE email = p_email AND password = p_password
     LIMIT 1;
  END IF;

  IF v_user_id IS NULL THEN
    RETURN json_build_object('error', 'invalid_credentials');
  END IF;

  IF v_role != 'DONO' THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  SELECT EXISTS(
    SELECT 1 FROM "Store" WHERE id = p_store_id AND "tenantId" = v_tenant_id
  ) INTO v_store_ok;

  IF NOT v_store_ok THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  -- Quantidade atual
  SELECT quantity INTO v_qty_atual
    FROM "StoreProduct"
   WHERE "storeId" = p_store_id AND "productId" = p_product_id;

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
      ("id","productId","storeId","userId","tenantId","type","quantity","reason","createdAt","isSynced")
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
      true
    );
  END IF;

  RETURN json_build_object('success', true, 'nova_quantidade', p_nova_quantidade);
END;
$$;


-- ----------------------------------------------------------------
-- RPC 3: excluir_produto
-- Soft delete: marca o produto como active=false (NÃO remove a linha,
-- preserva FK de Sale/SaleItem/StockMovement). O PDV propaga o flag no
-- pull (puxa ativos+inativos) e o produto some das telas locais.
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION excluir_produto(
  p_email      TEXT,
  p_password   TEXT,
  p_product_id TEXT
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
  v_prod_ok   BOOLEAN;
BEGIN
  SELECT id, role, "tenantId"
    INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email
     AND password = extensions.crypt(p_password, password)
   LIMIT 1;

  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId"
      INTO v_user_id, v_role, v_tenant_id
      FROM "User"
     WHERE email = p_email AND password = p_password
     LIMIT 1;
  END IF;

  IF v_user_id IS NULL THEN
    RETURN json_build_object('error', 'invalid_credentials');
  END IF;

  IF v_role != 'DONO' THEN
    RETURN json_build_object('error', 'forbidden');
  END IF;

  SELECT EXISTS(
    SELECT 1 FROM "Product" WHERE id = p_product_id AND "tenantId" = v_tenant_id
  ) INTO v_prod_ok;

  IF NOT v_prod_ok THEN
    RETURN json_build_object('error', 'not_found');
  END IF;

  UPDATE "Product"
     SET active = false, "updatedAt" = NOW()
   WHERE id = p_product_id AND "tenantId" = v_tenant_id;

  RETURN json_build_object('success', true);
END;
$$;


-- ----------------------------------------------------------------
-- RPC 4: get_vendas_rede
-- Lista as vendas no período (todas as lojas ou uma), até 500, mais
-- recentes primeiro. Usado pela aba Vendas do painel web.
-- ----------------------------------------------------------------
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
    SELECT s.id, s."saleDate", s.total, s."paymentMethod", s."paymentStatus",
           st.name AS loja_nome,
           COALESCE((SELECT sum(si.quantity) FROM "SaleItem" si WHERE si."saleId" = s.id),0) AS itens
    FROM "Sale" s
    JOIN "Store" st ON st.id = s."storeId"
    WHERE s."tenantId" = v_tenant
      AND s."saleDate" >= p_from AND s."saleDate" <= p_to
      AND (p_store_id IS NULL OR s."storeId" = p_store_id)
      AND (p_payment = 'TODOS' OR s."paymentMethod" = p_payment)
    ORDER BY s."saleDate" DESC
    LIMIT 500
  )
  SELECT COALESCE(jsonb_agg(jsonb_build_object(
    'id', b.id, 'data', b."saleDate", 'total_centavos', b.total,
    'metodo', b."paymentMethod", 'status', b."paymentStatus",
    'loja', b.loja_nome, 'itens', b.itens)), '[]'::jsonb)
  INTO v_result FROM base b;

  RETURN jsonb_build_object('vendas', v_result);
END;
$$;


-- ----------------------------------------------------------------
-- RPC 5: get_venda_detalhe
-- Detalhe completo de uma venda (itens + valores) para o modal da
-- aba Vendas. Mesma visão do SaleDetailsWindow do PDV.
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION get_venda_detalhe(
  p_email    TEXT,
  p_password TEXT,
  p_sale_id  TEXT
)
RETURNS JSONB LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_tenant TEXT; v_role TEXT; v_sale "Sale"%ROWTYPE; v_loja TEXT; v_itens JSONB;
BEGIN
  SELECT u."tenantId", u.role INTO v_tenant, v_role
    FROM "User" u
   WHERE u.email = p_email AND u.active = true
     AND u.password = extensions.crypt(p_password, u.password)
   LIMIT 1;

  IF v_tenant IS NULL THEN RETURN jsonb_build_object('error','invalid_credentials'); END IF;
  IF v_role NOT IN ('DONO','GERENTE') THEN RETURN jsonb_build_object('error','forbidden'); END IF;

  SELECT * INTO v_sale FROM "Sale" WHERE id = p_sale_id AND "tenantId" = v_tenant;
  IF v_sale.id IS NULL THEN RETURN jsonb_build_object('error','not_found'); END IF;

  SELECT name INTO v_loja FROM "Store" WHERE id = v_sale."storeId";

  SELECT COALESCE(jsonb_agg(jsonb_build_object(
    'nome', COALESCE(p.name, '(produto removido)'),
    'tipo', si.type,
    'quantidade', si.quantity,
    'preco_unit_centavos', si."priceUnit",
    'subtotal_centavos', si.subtotal
  ) ORDER BY p.name), '[]'::jsonb)
  INTO v_itens
  FROM "SaleItem" si
  LEFT JOIN "Product" p ON p.id = si."productId"
  WHERE si."saleId" = p_sale_id;

  RETURN jsonb_build_object(
    'id', v_sale.id, 'data', v_sale."saleDate", 'loja', v_loja,
    'metodo', v_sale."paymentMethod", 'status', v_sale."paymentStatus",
    'subtotal_centavos', v_sale.subtotal, 'desconto_centavos', v_sale.discount,
    'total_centavos', v_sale.total, 'recebido_centavos', v_sale."receivedAmount",
    'troco_centavos', v_sale."changeAmount", 'itens', v_itens
  );
END;
$$;
