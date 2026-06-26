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
