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

  RETURN json_build_object(
    'produtos', (
      SELECT COALESCE(json_agg(json_build_object(
        'productId',   p.id,
        'nome',        p.name,
        'tipo',        p.type,
        'unitMeasure', p."unitMeasure",
        'quantidade',  COALESCE(sp.quantity, 0),
        'minimo',      COALESCE(sp."minStock", 0)
      ) ORDER BY p.name), '[]'::json)
      FROM "Product" p
      LEFT JOIN "StoreProduct" sp ON sp."productId" = p.id AND sp."storeId" = p_store_id
      WHERE p."tenantId" = v_tenant_id AND p.active = true
    ),
    -- categorias do tenant (para o dropdown do cadastro de produto no painel web)
    'categorias', (
      SELECT COALESCE(json_agg(json_build_object('id', c.id, 'nome', c.name) ORDER BY c.name), '[]'::json)
      FROM "Category" c WHERE c."tenantId" = v_tenant_id
    )
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
  INSERT INTO "OwnerStockAdjustment" ("id","tenantId","storeId","productId","quantity","minStock","createdBy")
  VALUES (gen_random_uuid()::text, v_tenant_id, p_store_id, p_product_id, p_nova_quantidade, NULL, v_user_id);

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
    SELECT s.id, s."storeId", s."saleDate", s.total, s."paymentMethod", s."paymentStatus",
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
    'id', b.id, 'storeId', b."storeId", 'data', b."saleDate", 'total_centavos', b.total,
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


-- ----------------------------------------------------------------
-- RPC 6: criar_produto
-- Cria um produto no catálogo do tenant (nuvem). Assim que criado, ele
-- aparece em TODAS as lojas (StoreProduct inserido para cada loja ativa
-- com saldo 0) e o PDV o recebe no próximo sync. Preços em centavos (int).
--
-- Código de barras é OBRIGATÓRIO e, dentro do tenant:
--   - já existe um produto ATIVO com este barcode -> erro barcode_duplicado.
--   - já existe um produto INATIVO (excluído antes) com este barcode ->
--     REATIVA essa linha com os dados novos em vez de criar duplicata
--     (retorna 'reativado': true). Resolve "excluí sem querer, recriar".
--   - não existe -> INSERT normal (retorna 'reativado': false).
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION criar_produto(
  p_email          TEXT,
  p_password       TEXT,
  p_nome           TEXT,
  p_tipo           TEXT,
  p_unidade        TEXT,
  p_preco_venda    INT,
  p_preco_custo    INT,
  p_categoria_id   TEXT,
  p_codigo_barras  TEXT DEFAULT NULL
)
RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_user_id TEXT; v_role TEXT; v_tenant_id TEXT; v_cat_ok BOOLEAN; v_new_id TEXT;
  v_barcode TEXT;
  v_existing_id TEXT; v_existing_active BOOLEAN;
BEGIN
  SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email AND password = extensions.crypt(p_password, password)
   LIMIT 1;
  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
      FROM "User" WHERE email = p_email AND password = p_password LIMIT 1;
  END IF;
  IF v_user_id IS NULL THEN RETURN json_build_object('error','invalid_credentials'); END IF;
  IF v_role != 'DONO' THEN RETURN json_build_object('error','forbidden'); END IF;

  IF p_nome IS NULL OR length(trim(p_nome)) = 0 THEN
    RETURN json_build_object('error','nome_obrigatorio');
  END IF;

  v_barcode := NULLIF(trim(p_codigo_barras), '');
  IF v_barcode IS NULL THEN
    RETURN json_build_object('error','barcode_obrigatorio');
  END IF;

  SELECT EXISTS(SELECT 1 FROM "Category" WHERE id = p_categoria_id AND "tenantId" = v_tenant_id) INTO v_cat_ok;
  IF NOT v_cat_ok THEN RETURN json_build_object('error','categoria_invalida'); END IF;

  SELECT id, active INTO v_existing_id, v_existing_active
    FROM "Product"
   WHERE barcode = v_barcode AND "tenantId" = v_tenant_id
   LIMIT 1;

  IF v_existing_id IS NOT NULL AND v_existing_active THEN
    RETURN json_build_object('error','barcode_duplicado');
  END IF;

  IF v_existing_id IS NOT NULL AND NOT v_existing_active THEN
    UPDATE "Product"
       SET name = trim(p_nome),
           type = COALESCE(NULLIF(p_tipo,''),'NORMAL'),
           "unitMeasure" = COALESCE(NULLIF(p_unidade,''),'UN'),
           "priceSale" = COALESCE(p_preco_venda,0),
           "priceCost" = COALESCE(p_preco_custo,0),
           "categoryId" = p_categoria_id,
           active = true,
           "updatedAt" = NOW()
     WHERE id = v_existing_id;

    INSERT INTO "StoreProduct" (id, "productId", "storeId", quantity, "minStock", "updatedAt")
    SELECT gen_random_uuid()::text, v_existing_id, st.id, 0, 0, NOW()
    FROM "Store" st
    WHERE st."tenantId" = v_tenant_id AND st.active = true
      AND NOT EXISTS (
        SELECT 1 FROM "StoreProduct" sp WHERE sp."productId" = v_existing_id AND sp."storeId" = st.id
      );

    RETURN json_build_object('success', true, 'productId', v_existing_id, 'reativado', true);
  END IF;

  v_new_id := gen_random_uuid()::text;

  INSERT INTO "Product"
    (id, name, barcode, "priceSale", "priceCost", type, "unitMeasure", active,
     "categoryId", "tenantId", "createdAt", "updatedAt")
  VALUES
    (v_new_id, trim(p_nome), v_barcode,
     COALESCE(p_preco_venda,0), COALESCE(p_preco_custo,0),
     COALESCE(NULLIF(p_tipo,''),'NORMAL'), COALESCE(NULLIF(p_unidade,''),'UN'),
     true, p_categoria_id, v_tenant_id, NOW(), NOW());

  INSERT INTO "StoreProduct" (id, "productId", "storeId", quantity, "minStock", "updatedAt")
  SELECT gen_random_uuid()::text, v_new_id, st.id, 0, 0, NOW()
  FROM "Store" st
  WHERE st."tenantId" = v_tenant_id AND st.active = true;

  RETURN json_build_object('success', true, 'productId', v_new_id, 'reativado', false);
END;
$$;


-- ----------------------------------------------------------------
-- RPC 7: get_categorias
-- Lista as categorias do tenant. Usado pelo dropdown de cadastro de
-- produto no PDV (o painel web já recebe as categorias no get_loja_estoque).
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION get_categorias(
  p_email TEXT, p_password TEXT
)
RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_user_id TEXT; v_role TEXT; v_tenant_id TEXT;
BEGIN
  SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email AND password = extensions.crypt(p_password, password)
   LIMIT 1;
  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
      FROM "User" WHERE email = p_email AND password = p_password LIMIT 1;
  END IF;
  IF v_user_id IS NULL THEN RETURN json_build_object('error','invalid_credentials'); END IF;
  IF v_role != 'DONO' THEN RETURN json_build_object('error','forbidden'); END IF;

  RETURN json_build_object('categorias', (
    SELECT COALESCE(json_agg(json_build_object('id', c.id, 'nome', c.name) ORDER BY c.name), '[]'::json)
    FROM "Category" c WHERE c."tenantId" = v_tenant_id
  ));
END;
$$;


-- ----------------------------------------------------------------
-- RPC 8: atualizar_produto
-- Edita um produto existente do catalogo (nuvem). Usado pelo PDV
-- (OpenProductFormWindow) para que a edicao propague a rede toda em
-- vez de ficar so no SQLite local (bug: sync revertia a edicao local
-- baixando o preco antigo da nuvem no proximo pull).
-- ----------------------------------------------------------------
CREATE OR REPLACE FUNCTION atualizar_produto(
  p_email          TEXT,
  p_password       TEXT,
  p_product_id     TEXT,
  p_nome           TEXT,
  p_tipo           TEXT,
  p_unidade        TEXT,
  p_preco_venda    INT,
  p_preco_custo    INT,
  p_categoria_id   TEXT,
  p_codigo_barras  TEXT DEFAULT NULL
)
RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_user_id TEXT; v_role TEXT; v_tenant_id TEXT; v_cat_ok BOOLEAN; v_prod_ok BOOLEAN;
BEGIN
  SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
    FROM "User" WHERE email = p_email AND password = extensions.crypt(p_password, password) LIMIT 1;
  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
      FROM "User" WHERE email = p_email AND password = p_password LIMIT 1;
  END IF;
  IF v_user_id IS NULL THEN RETURN json_build_object('error','invalid_credentials'); END IF;
  IF v_role != 'DONO' THEN RETURN json_build_object('error','forbidden'); END IF;

  IF p_nome IS NULL OR length(trim(p_nome)) = 0 THEN
    RETURN json_build_object('error','nome_obrigatorio');
  END IF;

  SELECT EXISTS(SELECT 1 FROM "Product" WHERE id = p_product_id AND "tenantId" = v_tenant_id) INTO v_prod_ok;
  IF NOT v_prod_ok THEN RETURN json_build_object('error','not_found'); END IF;

  SELECT EXISTS(SELECT 1 FROM "Category" WHERE id = p_categoria_id AND "tenantId" = v_tenant_id) INTO v_cat_ok;
  IF NOT v_cat_ok THEN RETURN json_build_object('error','categoria_invalida'); END IF;

  UPDATE "Product"
     SET name = trim(p_nome),
         barcode = COALESCE(NULLIF(trim(p_codigo_barras), ''), barcode),
         "priceSale" = COALESCE(p_preco_venda, "priceSale"),
         "priceCost" = COALESCE(p_preco_custo, "priceCost"),
         type = COALESCE(NULLIF(p_tipo,''), type),
         "unitMeasure" = COALESCE(NULLIF(p_unidade,''), "unitMeasure"),
         "categoryId" = p_categoria_id,
         "updatedAt" = NOW()
   WHERE id = p_product_id AND "tenantId" = v_tenant_id;

  RETURN json_build_object('success', true);
END;
$$;


-- ----------------------------------------------------------------
-- RPC 9: gerar_codigo_interno  (+ sequencia e helper de checksum)
-- Gera um codigo de barras para produtos SEM codigo de fabrica
-- (coxinha, salgado, bolo). Usado pelo botao "Gerar" no cadastro de
-- produto, no PDV e no painel web.
--
-- Formato: EAN-13 valido = '2' + sequencia(11 digitos) + digito verificador.
-- O prefixo 2 e reservado pela GS1 para uso INTERNO da loja, entao nunca
-- colide com produto de fabrica (que no Brasil comeca com 789/790). A
-- sequencia garante que nunca repete entre lojas/cadastros simultaneos.
-- Ex.: 2000000000015, 2000000000022, ...
--
-- criar_produto NAO muda: o botao so preenche o campo, e o cadastro segue
-- com a mesma validacao de duplicidade que ja existia.
-- ----------------------------------------------------------------
CREATE SEQUENCE IF NOT EXISTS internal_barcode_seq START 1;

CREATE OR REPLACE FUNCTION ean13_check_digit(p_12 TEXT)
RETURNS INT LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
  s INT := 0; i INT; d INT;
BEGIN
  IF p_12 IS NULL OR length(p_12) <> 12 OR p_12 !~ '^[0-9]{12}$' THEN
    RAISE EXCEPTION 'ean13_check_digit espera exatamente 12 digitos, recebeu: %', p_12;
  END IF;
  FOR i IN 1..12 LOOP
    d := substr(p_12, i, 1)::INT;
    IF i % 2 = 1 THEN s := s + d; ELSE s := s + d * 3; END IF;
  END LOOP;
  RETURN (10 - (s % 10)) % 10;
END;
$$;

CREATE OR REPLACE FUNCTION gerar_codigo_interno(
  p_email    TEXT,
  p_password TEXT
)
RETURNS JSON LANGUAGE plpgsql SECURITY DEFINER SET search_path = public AS $$
DECLARE
  v_user_id TEXT; v_role TEXT; v_tenant_id TEXT;
  v_seq BIGINT; v_base TEXT; v_code TEXT; v_tries INT := 0;
BEGIN
  SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
    FROM "User"
   WHERE email = p_email AND password = extensions.crypt(p_password, password)
   LIMIT 1;
  IF v_user_id IS NULL THEN
    SELECT id, role, "tenantId" INTO v_user_id, v_role, v_tenant_id
      FROM "User" WHERE email = p_email AND password = p_password LIMIT 1;
  END IF;
  IF v_user_id IS NULL THEN RETURN json_build_object('error','invalid_credentials'); END IF;
  IF v_role != 'DONO' THEN RETURN json_build_object('error','forbidden'); END IF;

  -- A sequencia ja garante unicidade; o loop e paranoia contra um codigo '2...'
  -- que alguem tenha digitado a mao.
  LOOP
    v_tries := v_tries + 1;
    IF v_tries > 50 THEN
      RETURN json_build_object('error','falha_gerar_codigo');
    END IF;

    v_seq  := nextval('internal_barcode_seq');
    v_base := '2' || lpad(v_seq::text, 11, '0');
    v_code := v_base || ean13_check_digit(v_base)::text;

    EXIT WHEN NOT EXISTS (SELECT 1 FROM "Product" WHERE barcode = v_code);
  END LOOP;

  RETURN json_build_object('success', true, 'codigo', v_code);
END;
$$;


-- ============================================================
-- POLITICAS DE LEITURA (RLS) DAS QUAIS O PDV DEPENDE
--
-- O PDV instalado nas lojas le a nuvem usando a ANON KEY, direto pela API REST.
-- Toda tabela que ele precisa ler tem RLS ligado + uma politica "anon_read".
-- ATENCAO: no Postgres, RLS ligado SEM nenhuma politica nao da erro -- a consulta
-- simplesmente retorna VAZIO. Foi assim que o ajuste de estoque do dono deixou de
-- chegar nas lojas: a tabela OwnerStockAdjustment tinha RLS sem politica, o
-- ApplyOwnerAdjustmentsAsync recebia lista vazia, o estoque local ficava zerado e
-- o PushStockSnapshotAsync em seguida devolvia esse zero para a nuvem, apagando o
-- valor que o dono tinha lancado.
--
-- Ao criar uma tabela nova que o PDV precise LER, crie tambem a politica abaixo.
-- A ESCRITA continua fechada para anon de proposito: quem grava sao as RPCs
-- SECURITY DEFINER (push_vendas, push_estoque, ajustar_estoque, set_estoque_loja).
-- ============================================================

-- Tabelas lidas pelo PDV (PullUpdatesAsync / ApplyOwnerAdjustmentsAsync):
--   Product, StoreProduct, Category, BreadConfig, OwnerStockAdjustment
--
-- Modelo da politica (executar uma vez por tabela):
--   CREATE POLICY anon_read ON "NomeDaTabela" FOR SELECT TO anon USING (true);

CREATE POLICY anon_read ON "OwnerStockAdjustment"
  FOR SELECT TO anon USING (true);


-- ============================================================
-- AUDITORIA DE ESTOQUE: saldo antes/depois em cada movimento
--
-- StockMovement guardava so a quantidade movimentada. Para conferir pao enviado x
-- vendido x dinheiro do caixa, o dono precisa ver "tinha 275, saiu 30, ficou 245"
-- sem ter que recalcular a cadeia inteira de movimentos. Estas colunas gravam isso
-- direto na linha. Sao NULL nos movimentos antigos, gravados antes delas existirem.
--
-- push_vendas nao precisou mudar: ele usa jsonb_populate_record(null::"StockMovement", e),
-- que absorve colunas novas automaticamente, e o INSERT nao lista colunas.
-- ============================================================

ALTER TABLE "StockMovement"
  ADD COLUMN IF NOT EXISTS "balanceBefore" double precision,
  ADD COLUMN IF NOT EXISTS "balanceAfter"  double precision;


-- ============================================================
-- get_conferencia_pao(p_email, p_password, p_dia, p_store_id)
--
-- Relatorio anti-desvio do painel do dono (aba "Pao"). Por loja x produto de pao:
--
--   base        ultimo saldo que o DONO declarou para a loja. OwnerStockAdjustment
--               e ABSOLUTO: define o saldo, nao soma.
--   vendido     unidades vendidas em vendas APROVADAS desde a base ate agora
--   saldoAtual  foto do estoque que o PDV daquela loja empurrou (StoreProduct)
--   esperado    base - vendido
--   diferenca   saldoAtual - esperado
--                 < 0  faltou pao sem venda registrada  -> dinheiro nao bate
--                 > 0  sobrou pao (reposicao nao lancada, devolucao)
--
-- CUIDADO COM A JANELA: saldoAtual e a foto de AGORA. Por isso a reconciliacao roda
-- na janela [ultimo ajuste do dono -> agora] e IGNORA p_dia. Comparar a base de um
-- dia passado com o estoque de hoje produz diferenca falsa -- foi o primeiro desenho
-- desta funcao e estava errado. p_dia alimenta so vendidoNoDia/receitaNoDia, que sao
-- informativos e NAO entram no calculo da diferenca.
--
-- Fuso: OwnerStockAdjustment.createdAt e timestamptz (UTC) e Sale.saleDate e
-- timestamp local do PDV. O ajuste e convertido para America/Sao_Paulo antes de
-- comparar; sem isso a janela erra em 3 horas.
--
-- A base e o ultimo ajuste do dono. Produtos sem ajuste ainda aparecem como
-- "sem base" (quantidades zeradas), em vez de comparar o estoque atual com ele
-- mesmo e esconder que a conferencia ainda nao foi iniciada.
CREATE OR REPLACE FUNCTION get_conferencia_pao(
  p_email    TEXT,
  p_password TEXT,
  p_dia      DATE,
  p_store_id TEXT DEFAULT NULL
)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_tenant_id TEXT;
  v_role      TEXT;
  v_linhas    JSONB;
BEGIN
  SELECT u."tenantId", u.role
    INTO v_tenant_id, v_role
    FROM "User" u
   WHERE u.email = p_email
     AND u.active = true
     AND u.password = extensions.crypt(p_password, u.password)
   LIMIT 1;

  -- Mantem compatibilidade com os usuarios ainda em senha legada durante a
  -- migracao do PDV para BCrypt.
  IF v_tenant_id IS NULL THEN
    SELECT u."tenantId", u.role
      INTO v_tenant_id, v_role
      FROM "User" u
     WHERE u.email = p_email
       AND u.active = true
       AND u.password = p_password
     LIMIT 1;
  END IF;

  IF v_tenant_id IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_credentials');
  END IF;
  IF v_role <> 'DONO' THEN
    RETURN jsonb_build_object('error', 'forbidden');
  END IF;

  WITH lojas AS (
    SELECT st.id, st.name
      FROM "Store" st
     WHERE st."tenantId" = v_tenant_id
       AND st.active = true
       AND (p_store_id IS NULL OR st.id = p_store_id)
  ), bases AS (
    SELECT DISTINCT ON (a."storeId", a."productId")
           a."storeId", a."productId", a.quantity AS base,
           a."createdAt" AT TIME ZONE 'America/Sao_Paulo' AS base_em
      FROM "OwnerStockAdjustment" a
      JOIN lojas l ON l.id = a."storeId"
     WHERE a."tenantId" = v_tenant_id
     ORDER BY a."storeId", a."productId", a."createdAt" DESC, a.id DESC
  ), linhas AS (
    SELECT p.id AS product_id,
           p.name AS produto,
           l.name AS loja,
           b.base,
           b.base_em,
           COALESCE(sp.quantity, 0) AS saldo_atual,
           COALESCE(vendas.vendido, 0) AS vendido,
           COALESCE(vendas.receita_no_dia, 0) AS receita_no_dia,
           p."priceSale" AS preco_unit_centavos
      FROM lojas l
      CROSS JOIN "Product" p
      LEFT JOIN "StoreProduct" sp
        ON sp."storeId" = l.id AND sp."productId" = p.id
      LEFT JOIN bases b
        ON b."storeId" = l.id AND b."productId" = p.id
      LEFT JOIN LATERAL (
        SELECT COALESCE(SUM(si.quantity) FILTER (
                         WHERE s."paymentStatus" = 'APROVADO'
                           AND b.base_em IS NOT NULL
                           AND s."saleDate" >= b.base_em), 0) AS vendido,
               COALESCE(SUM(si.subtotal) FILTER (
                         WHERE s."paymentStatus" = 'APROVADO'
                           AND s."saleDate"::date = p_dia), 0) AS receita_no_dia
          FROM "SaleItem" si
          JOIN "Sale" s ON s.id = si."saleId"
         WHERE si."productId" = p.id
           AND s."storeId" = l.id
           AND s."tenantId" = v_tenant_id
      ) vendas ON true
     WHERE p."tenantId" = v_tenant_id
       AND p.active = true
       AND p.type = 'PAO_FRANCES'
  )
  SELECT COALESCE(jsonb_agg(jsonb_build_object(
    'produto', produto,
    'loja', loja,
    'baseEm', base_em,
    'base', COALESCE(base, 0),
    'vendido', vendido,
    'esperado', CASE WHEN base_em IS NULL THEN 0 ELSE base - vendido END,
    'saldoAtual', saldo_atual,
    'diferenca', CASE WHEN base_em IS NULL THEN 0 ELSE saldo_atual - (base - vendido) END,
    'valorDiferenca', CASE WHEN base_em IS NULL THEN 0 ELSE (saldo_atual - (base - vendido)) * preco_unit_centavos END,
    'receitaNoDia', receita_no_dia
  ) ORDER BY loja, produto), '[]'::jsonb)
    INTO v_linhas
    FROM linhas;

  RETURN jsonb_build_object('linhas', v_linhas);
END;
$$;


-- ============================================================
-- ALERTA DE ESTOQUE: "acabou" nao e a mesma coisa que "nunca foi contado"
--
-- get_dashboard_rede contava o alerta como "sp.quantity <= sp.minStock". Como NENHUM
-- produto tem minimo definido (minStock = 0 nas tres lojas), qualquer produto zerado
-- satisfazia 0 <= 0: 111 de 115 produtos na Padaria Centro apareciam como "em falta".
-- Um alerta que nunca fica limpo deixa de ser alerta -- o dono parou de olhar.
--
-- A maioria daqueles 111 nao tinha acabado: nunca teve estoque lancado, porque a loja
-- ainda nao fez a contagem inicial. Sao coisas diferentes:
--
--   ACABOU          zerado e JA teve lancamento nesta loja  -> repor (alerta de verdade)
--   BAIXO           0 < quantidade <= minimo, minimo > 0    -> repor antes de acabar
--   SEM_LANCAMENTO  zerado e nunca lancado                  -> contagem pendente, nao e urgencia
--   OK              tem estoque acima do minimo
--
-- "Ja teve lancamento" precisa olhar StockMovement E OwnerStockAdjustment:
-- ajustar_estoque grava um StockMovement, mas set_estoque_loja (lancamento em lote pelo
-- painel) grava SO o OwnerStockAdjustment. Olhando uma tabela so, todo produto lancado
-- em lote seria classificado como "nunca contado".
--
-- get_dashboard_rede devolve por loja: estoque_baixo, estoque_acabou,
-- estoque_sem_lancamento e estoque_alerta (= baixo + acabou, o numero que pede acao).
-- get_loja_estoque devolve 'situacao' e 'jaLancado' por produto, para a aba Estoque
-- filtrar por "Repor" e por "Sem contagem".
--
-- Definicoes completas: migrations dashboard_separa_acabou_de_nunca_lancado e
-- loja_estoque_classifica_situacao.

-- ============================================================
-- CONTRATO DAS RPCs DE ESCRITA: erro vem no CORPO, com HTTP 200
--
-- push_vendas e push_estoque RETORNAM json_build_object('error', ...) quando
-- recusam o envio (token invalido). Para o PostgREST isso e uma chamada de
-- funcao bem-sucedida: o HTTP e 200. Quem consome PRECISA ler o corpo.
--
-- O PDV olhava so o status HTTP. Resultado, em producao: um caixa com token
-- vencido recebia 200, dava a venda por enviada, marcava isSynced=true no
-- SQLite e nunca mais tentava -- a venda sumia. E a foto do estoque era
-- descartada em silencio, com o caixa exibindo "Sincronizado" em verde.
-- Corrigido em SyncService.ErroDaResposta (app 1.1.3).
--
-- Ao criar uma RPC nova que possa RECUSAR, mantenha o mesmo formato
-- ('error', <codigo>) e trate o codigo no cliente.
-- ============================================================

-- ============================================================
-- IDENTIDADE DA MAQUINA: STORE_ID e STORE_SYNC_TOKEN sao INDEPENDENTES
--
-- O token manda em tudo que o PDV ESCREVE (push_vendas/push_estoque derivam a
-- loja dele, ignorando o payload). O STORE_ID do .env manda em tudo que ele LE
-- (StoreProduct, BreadConfig, OwnerStockAdjustment). Nada amarrava os dois.
--
-- Uma maquina com as duas linhas apontando para lojas DIFERENTES vende por uma
-- loja e mostra o estoque de outra, sem erro nenhum na tela -- o sintoma que o
-- usuario relata e "o estoque do PDV nao atualiza". Aconteceu em 20/08/2026.
--
-- loja_do_token e SECURITY DEFINER com EXECUTE para anon exatamente para o PDV
-- poder conferir isso sozinho na abertura (SyncService.ConferirIdentidadeDaLojaAsync).
-- Nao remova esse GRANT.
-- ============================================================

-- ============================================================
-- AUTO-CONFIGURACAO DO CAIXA PELO LOGIN  (caixa_token / registrar_caixa)
--
-- Migracao: caixa_token_auto_registro_no_login (21/08/2026)
--
-- O PROBLEMA QUE ISTO RESOLVE
-- Dizer a uma maquina de que loja ela era exigia colar um segredo no .env, a
-- mao, em cada PC. Quando o token de duas lojas foi rotacionado, a troca nunca
-- chegou nas maquinas: Japao e Producao ficaram de 17/08 a 21/08 sem mandar
-- venda nem estoque. Nenhum passo automatico dependia de um humano ir ate la.
--
-- COMO FUNCIONA AGORA
-- O usuario do caixa (centro@, japao@, producao@) ja tem "storeId" no cadastro.
-- No primeiro login ONLINE o PDV chama registrar_caixa, que valida a senha e
-- emite um token so daquela maquina. O PDV guarda em %AppData%/pdv-padaria/
-- caixa-token.dat e usa dali em diante -- para gravar E para saber que estoque
-- mostrar. Ninguem digita segredo em lugar nenhum.
--
-- FORMATO DO TOKEN: "<uuid>.<segredo>"
-- O uuid na frente e a chave primaria da linha, entao loja_do_token acha a
-- linha pelo indice e roda bcrypt UMA vez. Sem ele seria bcrypt contra a tabela
-- inteira a cada sincronizacao (2x por minuto, por caixa).
--
-- loja_do_token aceita os dois formatos: com ponto vai em caixa_token, sem
-- ponto vai em store_sync_secret (as maquinas antigas continuam valendo).
--
-- SEGURANCA
--   - caixa_token tem RLS ligado e NENHUMA politica: anon nunca le os hashes.
--     Quem consulta e loja_do_token, SECURITY DEFINER.
--   - o segredo em texto so existe no retorno de registrar_caixa. No banco,
--     so o hash bcrypt.
--   - quem tem email+senha de um caixa consegue emitir token daquela loja.
--     E o mesmo poder que essa pessoa ja tem sentada no caixa (lancar venda
--     naquela loja), entao o alcance nao aumentou -- e agora cada maquina tem
--     o seu, entao da para revogar UMA sem parar a loja.
--
-- REVOGAR UMA MAQUINA:
--   update caixa_token set "revokedAt" = now() where id = '<id do token>';
--
-- VER AS MAQUINAS REGISTRADAS:
--   select c.id, s.name, c.terminal, c."createdAt", c."revokedAt"
--     from caixa_token c join "Store" s on s.id = c."storeId"
--    order by s.name, c."createdAt";
-- ============================================================

-- ============================================================
-- IMPLEMENTACAO EXECUTAVEL DA IDENTIDADE POR MAQUINA
-- App 1.1.7 / 21-08-2026
-- ============================================================

CREATE TABLE IF NOT EXISTS public.caixa_token (
  id          TEXT PRIMARY KEY,
  "storeId"   TEXT NOT NULL REFERENCES public."Store"(id),
  "tenantId"  TEXT NOT NULL,
  token_hash  TEXT NOT NULL,
  terminal    TEXT,
  "createdAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
  "revokedAt" TIMESTAMPTZ
);

ALTER TABLE public.caixa_token ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON TABLE public.caixa_token FROM PUBLIC, anon, authenticated;

ALTER TABLE public.store_sync_secret
  ADD COLUMN IF NOT EXISTS token_hash_prev TEXT;
REVOKE ALL ON TABLE public.store_sync_secret FROM PUBLIC, anon, authenticated;

-- Rotacionar o token legado sem preencher token_hash_prev derrubou todas as
-- maquinas antigas de uma vez. O trigger preserva uma janela curta de transicao;
-- loja_do_token deixa o segredo anterior expirar depois de sete dias.
CREATE OR REPLACE FUNCTION public.preservar_token_anterior()
RETURNS trigger
LANGUAGE plpgsql
SET search_path = ''
AS $$
BEGIN
  IF NEW.token_hash IS DISTINCT FROM OLD.token_hash THEN
    NEW.token_hash_prev := OLD.token_hash;
    NEW."updatedAt" := now();
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS store_sync_secret_preserva_anterior
  ON public.store_sync_secret;
CREATE TRIGGER store_sync_secret_preserva_anterior
BEFORE UPDATE OF token_hash ON public.store_sync_secret
FOR EACH ROW EXECUTE FUNCTION public.preservar_token_anterior();

REVOKE ALL ON FUNCTION public.preservar_token_anterior()
  FROM PUBLIC, anon, authenticated;

CREATE OR REPLACE FUNCTION public.loja_do_token(p_token TEXT)
RETURNS TEXT
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_id    TEXT;
  v_seg   TEXT;
  v_store TEXT;
BEGIN
  IF p_token IS NULL OR p_token = '' THEN
    RETURN NULL;
  END IF;

  IF position('.' IN p_token) > 0 THEN
    v_id  := split_part(p_token, '.', 1);
    v_seg := split_part(p_token, '.', 2);

    SELECT c."storeId" INTO v_store
      FROM public.caixa_token c
      JOIN public."Store" s
        ON s.id = c."storeId"
       AND s."tenantId" = c."tenantId"
       AND s.active = true
     WHERE c.id = v_id
       AND c."revokedAt" IS NULL
       AND c.token_hash = extensions.crypt(v_seg, c.token_hash)
     LIMIT 1;

    RETURN v_store;
  END IF;

  SELECT s."storeId" INTO v_store
    FROM public.store_sync_secret s
   WHERE s.token_hash = extensions.crypt(p_token, s.token_hash)
      OR (s.token_hash_prev IS NOT NULL
          AND s."updatedAt" > now() - interval '7 days'
          AND s.token_hash_prev = extensions.crypt(p_token, s.token_hash_prev))
   LIMIT 1;

  RETURN v_store;
END;
$$;

REVOKE ALL ON FUNCTION public.loja_do_token(TEXT)
  FROM PUBLIC, authenticated;
GRANT EXECUTE ON FUNCTION public.loja_do_token(TEXT) TO anon;

-- Os dois ultimos parametros tem default para clientes 1.1.6 continuarem
-- chamando a RPC com email, senha e terminal ate receberem a atualizacao.
DROP FUNCTION IF EXISTS public.registrar_caixa(TEXT, TEXT, TEXT);
CREATE OR REPLACE FUNCTION public.registrar_caixa(
  p_email       TEXT,
  p_senha       TEXT,
  p_terminal    TEXT DEFAULT NULL,
  p_store_id    TEXT DEFAULT NULL,
  p_token_atual TEXT DEFAULT NULL
)
RETURNS JSON
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_user_id        TEXT;
  v_user_store_id  TEXT;
  v_tenant_id      TEXT;
  v_role           TEXT;
  v_store_id       TEXT;
  v_token_id       TEXT;
  v_token_secret   TEXT;
  v_token_store    TEXT;
  v_token_tenant   TEXT;
  v_id             TEXT;
  v_segredo        TEXT;
BEGIN
  SELECT u.id, u."storeId", u."tenantId", u.role
    INTO v_user_id, v_user_store_id, v_tenant_id, v_role
    FROM public."User" u
    JOIN public."Tenant" t
      ON t.id = u."tenantId" AND t.active = true
   WHERE u.email = p_email
     AND u.active = true
     AND CASE
       WHEN u.password LIKE '$2a$%'
         OR u.password LIKE '$2b$%'
         OR u.password LIKE '$2y$%'
       THEN u.password = extensions.crypt(p_senha, u.password)
       ELSE u.password = p_senha
     END
   LIMIT 1;

  IF v_user_id IS NULL THEN
    RETURN json_build_object('error', 'credenciais_invalidas');
  END IF;

  -- Valida o token inteiro (id + segredo), inclusive se ja foi revogado. Isso
  -- permite ao DONO renovar a mesma maquina sem aceitar um id inventado.
  IF position('.' IN coalesce(p_token_atual, '')) > 0 THEN
    v_token_id     := split_part(p_token_atual, '.', 1);
    v_token_secret := split_part(p_token_atual, '.', 2);

    SELECT c."storeId", c."tenantId"
      INTO v_token_store, v_token_tenant
      FROM public.caixa_token c
      JOIN public."Store" s
        ON s.id = c."storeId"
       AND s."tenantId" = c."tenantId"
       AND s.active = true
     WHERE c.id = v_token_id
       AND c.token_hash = extensions.crypt(v_token_secret, c.token_hash)
     LIMIT 1;

    IF v_token_tenant IS DISTINCT FROM v_tenant_id THEN
      v_token_id := NULL;
      v_token_store := NULL;
    END IF;
  END IF;

  IF v_user_store_id IS NOT NULL THEN
    -- Usuario de loja nunca escolhe outra loja pelo payload.
    SELECT s.id INTO v_store_id
      FROM public."Store" s
     WHERE s.id = v_user_store_id
       AND s."tenantId" = v_tenant_id
       AND s.active = true;

    IF v_store_id IS NULL THEN
      RETURN json_build_object('error', 'loja_invalida');
    END IF;
  ELSE
    IF v_role <> 'DONO' THEN
      RETURN json_build_object('error', 'usuario_sem_loja');
    END IF;

    -- DONO mantem a loja da propria maquina. Para token legado ja perdido,
    -- usa somente a loja confirmada e enviada pelo app, validada no tenant.
    v_store_id := v_token_store;
    IF v_store_id IS NULL AND nullif(p_store_id, '') IS NOT NULL THEN
      SELECT s.id INTO v_store_id
        FROM public."Store" s
       WHERE s.id = p_store_id
         AND s."tenantId" = v_tenant_id
         AND s.active = true;
    END IF;

    IF v_store_id IS NULL THEN
      RETURN json_build_object('error', 'loja_nao_informada');
    END IF;
  END IF;

  v_id      := gen_random_uuid()::TEXT;
  v_segredo := encode(extensions.gen_random_bytes(24), 'hex');

  INSERT INTO public.caixa_token
    (id, "storeId", "tenantId", token_hash, terminal)
  VALUES
    (v_id, v_store_id, v_tenant_id,
     extensions.crypt(v_segredo, extensions.gen_salt('bf', 6)),
     nullif(left(trim(coalesce(p_terminal, '')), 100), ''));

  IF v_token_id IS NOT NULL THEN
    UPDATE public.caixa_token
       SET "revokedAt" = coalesce("revokedAt", now())
     WHERE id = v_token_id
       AND "tenantId" = v_tenant_id;
  END IF;

  RETURN json_build_object(
    'storeId',  v_store_id,
    'tenantId', v_tenant_id,
    'token',    v_id || '.' || v_segredo
  );
END;
$$;

REVOKE ALL ON FUNCTION public.registrar_caixa(TEXT, TEXT, TEXT, TEXT, TEXT)
  FROM PUBLIC, authenticated;
GRANT EXECUTE ON FUNCTION public.registrar_caixa(TEXT, TEXT, TEXT, TEXT, TEXT)
  TO anon;

-- RASCUNHO FUTURO, NAO ATIVAR NA RELEASE 1.1.7.
-- O corte exige uma versao-ponte e drenagem de todos os clientes 1.1.6 por loja;
-- misturar snapshot legado e delta de ledger pode corromper o estoque.
-- CORTE PARA O LEDGER: StoreProduct existente vira a base, sem tentar reproduzir
-- movimentos antigos (as fotos legadas ja se contradizem). Apos o deploy, cada
-- StockMovement.id novo altera essa base exatamente uma vez. A base precisa de uma
-- conferencia fisica unica; nenhuma heuristica consegue reconstruir o passado.
--
-- Nao deixa um payload copiado de outra maquina alterar ids que ja pertencem a
-- outra loja/tenant. O token continua sendo a unica fonte de storeId/tenantId.
CREATE OR REPLACE FUNCTION public.push_vendas(p_payload JSONB, p_token TEXT)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_store    TEXT;
  v_tenant   TEXT;
  v_fallback TEXT;
  v_sales    INT := 0;
  v_items    INT := 0;
  v_moves    INT := 0;
BEGIN
  v_store := public.loja_do_token(p_token);

  IF v_store IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_token');
  END IF;

  SELECT "tenantId" INTO v_tenant
    FROM public."Store"
   WHERE id = v_store AND active = true;

  IF v_tenant IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_store');
  END IF;

  IF EXISTS (
    SELECT 1
      FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) e
      JOIN public."Sale" s ON s.id = e->>'id'
     WHERE s."storeId" <> v_store OR s."tenantId" <> v_tenant
  ) OR EXISTS (
    SELECT 1
      FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
      JOIN public."StockMovement" m ON m.id = e->>'id'
     WHERE m."storeId" <> v_store OR m."tenantId" <> v_tenant
  ) THEN
    RETURN jsonb_build_object('error', 'invalid_scope');
  END IF;

  -- Um id repetido dentro do mesmo lote tornaria o resultado dependente da ordem.
  -- Rejeita antes de qualquer INSERT; retries entre lotes continuam idempotentes.
  IF EXISTS (
    SELECT 1
      FROM (
        SELECT 'sale' AS kind, e->>'id' AS id
          FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) e
        UNION ALL
        SELECT 'item', e->>'id'
          FROM jsonb_array_elements(coalesce(p_payload->'items', '[]'::JSONB)) e
        UNION ALL
        SELECT 'movement', e->>'id'
          FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
      ) ids
     WHERE nullif(id, '') IS NULL
  ) OR EXISTS (
    SELECT 1
      FROM (
        SELECT 'sale' AS kind, e->>'id' AS id
          FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) e
        UNION ALL
        SELECT 'item', e->>'id'
          FROM jsonb_array_elements(coalesce(p_payload->'items', '[]'::JSONB)) e
        UNION ALL
        SELECT 'movement', e->>'id'
          FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
      ) ids
     GROUP BY kind, id
    HAVING count(*) > 1
  ) THEN
    RETURN jsonb_build_object('error', 'invalid_payload');
  END IF;

  IF EXISTS (
    SELECT 1
      FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
     WHERE CASE
       WHEN upper(coalesce(e->>'type', '')) NOT IN ('ENTRADA', 'SAIDA', 'AJUSTE')
         THEN true
       WHEN coalesce(e->>'quantity', '') !~
            '^[+]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$'
         THEN true
       ELSE (e->>'quantity')::DOUBLE PRECISION < 0
     END
  ) THEN
    RETURN jsonb_build_object('error', 'invalid_payload');
  END IF;

  -- Recusa o lote inteiro em vez de descartar silenciosamente item/movimento
  -- invalido. O cliente so marca a fila como sincronizada quando nao ha error.
  IF EXISTS (
    SELECT 1
      FROM jsonb_array_elements(coalesce(p_payload->'items', '[]'::JSONB)) e
     WHERE NOT EXISTS (
             SELECT 1 FROM public."Product" p
              WHERE p.id = e->>'productId' AND p."tenantId" = v_tenant
           )
        OR NOT (
             EXISTS (
               SELECT 1 FROM public."Sale" s
                WHERE s.id = e->>'saleId'
                  AND s."storeId" = v_store
                  AND s."tenantId" = v_tenant
             )
             OR EXISTS (
               SELECT 1
                 FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) venda
                WHERE venda->>'id' = e->>'saleId'
             )
           )
        OR EXISTS (
             SELECT 1
               FROM public."SaleItem" i
               JOIN public."Sale" s ON s.id = i."saleId"
              WHERE i.id = e->>'id'
                AND (s."storeId" <> v_store OR s."tenantId" <> v_tenant)
           )
  ) OR EXISTS (
    SELECT 1
      FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
     WHERE NOT EXISTS (
             SELECT 1 FROM public."Product" p
              WHERE p.id = e->>'productId' AND p."tenantId" = v_tenant
           )
        OR (
             nullif(e->>'saleId', '') IS NOT NULL
             AND NOT (
               EXISTS (
                 SELECT 1 FROM public."Sale" s
                  WHERE s.id = e->>'saleId'
                    AND s."storeId" = v_store
                    AND s."tenantId" = v_tenant
               )
               OR EXISTS (
                 SELECT 1
                   FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) venda
                  WHERE venda->>'id' = e->>'saleId'
               )
             )
           )
  ) THEN
    RETURN jsonb_build_object('error', 'invalid_scope');
  END IF;

  SELECT id INTO v_fallback
    FROM public."User"
   WHERE "tenantId" = v_tenant AND active = true
   ORDER BY CASE WHEN role = 'DONO' THEN 0 WHEN role = 'GERENTE' THEN 1 ELSE 2 END
   LIMIT 1;

  INSERT INTO public."Sale"
  SELECT (jsonb_populate_record(NULL::public."Sale", e || jsonb_build_object(
            'storeId', v_store,
            'tenantId', v_tenant,
            'userId', coalesce((
              SELECT u.id FROM public."User" u
               WHERE u.id = e->>'userId'
                 AND u."tenantId" = v_tenant
                 AND (u."storeId" IS NULL OR u."storeId" = v_store)
            ), v_fallback)
          ))).*
    FROM jsonb_array_elements(coalesce(p_payload->'sales', '[]'::JSONB)) e
  ON CONFLICT (id) DO UPDATE SET
    "isSynced" = excluded."isSynced",
    "syncedAt" = excluded."syncedAt",
    "paymentStatus" = excluded."paymentStatus",
    subtotal = excluded.subtotal,
    discount = excluded.discount,
    total = excluded.total
  WHERE "Sale"."storeId" = v_store
    AND "Sale"."tenantId" = v_tenant;
  GET DIAGNOSTICS v_sales = ROW_COUNT;

  INSERT INTO public."SaleItem"
  SELECT (jsonb_populate_record(NULL::public."SaleItem", e)).*
    FROM jsonb_array_elements(coalesce(p_payload->'items', '[]'::JSONB)) e
   WHERE EXISTS (
           SELECT 1 FROM public."Product" p
            WHERE p.id = e->>'productId' AND p."tenantId" = v_tenant
         )
     AND EXISTS (
           SELECT 1 FROM public."Sale" s
            WHERE s.id = e->>'saleId'
              AND s."storeId" = v_store
              AND s."tenantId" = v_tenant
         )
  ON CONFLICT (id) DO NOTHING;
  GET DIAGNOSTICS v_items = ROW_COUNT;

  -- StockMovement.id e o ledger: somente o INSERT realmente novo gera delta.
  -- Uma resposta perdida pode ser repetida sem baixar o estoque outra vez.
  WITH inserted AS (
    INSERT INTO public."StockMovement"
    SELECT (jsonb_populate_record(NULL::public."StockMovement", e || jsonb_build_object(
              'storeId', v_store,
              'tenantId', v_tenant,
              'isSynced', true,
              'syncedAt', now(),
              'userId', coalesce((
                SELECT u.id FROM public."User" u
                 WHERE u.id = e->>'userId'
                   AND u."tenantId" = v_tenant
                   AND (u."storeId" IS NULL OR u."storeId" = v_store)
              ), v_fallback)
            ))).*
      FROM jsonb_array_elements(coalesce(p_payload->'movements', '[]'::JSONB)) e
     WHERE EXISTS (
             SELECT 1 FROM public."Product" p
              WHERE p.id = e->>'productId' AND p."tenantId" = v_tenant
           )
       AND (
         nullif(e->>'saleId', '') IS NULL OR EXISTS (
           SELECT 1 FROM public."Sale" s
            WHERE s.id = e->>'saleId'
              AND s."storeId" = v_store
              AND s."tenantId" = v_tenant
         )
       )
    ON CONFLICT (id) DO NOTHING
    RETURNING "productId", type, quantity
  ), deltas AS (
    SELECT "productId",
           sum(CASE upper(type)
                 WHEN 'ENTRADA' THEN abs(quantity)
                 WHEN 'SAIDA'   THEN -abs(quantity)
                 ELSE 0
               END) AS quantity
      FROM inserted
     GROUP BY "productId"
  ), projected AS (
    INSERT INTO public."StoreProduct" AS stock
      (id, "productId", "storeId", quantity, "minStock", "updatedAt")
    SELECT gen_random_uuid()::TEXT, "productId", v_store, quantity, 0, now()
      FROM deltas
     WHERE quantity <> 0
     ORDER BY "productId"
    ON CONFLICT ("storeId", "productId") DO UPDATE
      SET quantity = stock.quantity + excluded.quantity,
          "updatedAt" = now()
    RETURNING 1
  )
  SELECT count(*) INTO v_moves FROM inserted;

  RETURN jsonb_build_object(
    'sales', v_sales, 'items', v_items, 'movements', v_moves, 'mode', 'ledger'
  );
END;
$$;

REVOKE ALL ON FUNCTION public.push_vendas(JSONB, TEXT)
  FROM PUBLIC, authenticated;
GRANT EXECUTE ON FUNCTION public.push_vendas(JSONB, TEXT) TO anon;

-- Clientes antigos ainda chamam push_estoque. Autentica e responde sucesso, mas
-- nao aceita mais que uma foto absoluta de um computador sobrescreva o ledger.
CREATE OR REPLACE FUNCTION public.push_estoque(p_payload JSONB, p_token TEXT)
RETURNS JSONB
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = public
AS $$
BEGIN
  IF public.loja_do_token(p_token) IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_token', 'stock', 0);
  END IF;

  RETURN jsonb_build_object('stock', 0, 'mode', 'ledger');
END;
$$;

REVOKE ALL ON FUNCTION public.push_estoque(JSONB, TEXT)
  FROM PUBLIC, authenticated;
GRANT EXECUTE ON FUNCTION public.push_estoque(JSONB, TEXT) TO anon;

-- ============================================================
-- pull_cadastros(p_token)  --  LEITURA RECORTADA PELA CREDENCIAL
--
-- Aplicada em 22/08/2026 (migracao pull_cadastros_leitura_pelo_token). Estava so no
-- banco: o cliente dependia deste contrato e nada no repositorio o fixava, entao uma
-- edicao pelo painel quebraria o PDV sem diff e sem revisao.
--
-- Substitui cinco leituras diretas as tabelas, cujo recorte por rede era um parametro
-- que o proprio cliente escolhia mandar na URL. Aqui a loja vem do TOKEN, server-side,
-- igual as escritas. Ver SyncService.ObterCadastrosAsync.
--
-- O CLIENTE DEPENDE DE:
--   * as chaves storeId, tenantId, categories, products, storeProducts, breadConfigs,
--     ownerAdjustments (CadastrosDto casa por camelCase);
--   * ownerAdjustments vir ORDENADO por createdAt -- ApplyOwnerAdjustmentsAsync aplica
--     cada ajuste como saldo ABSOLUTO, entao fora de ordem o saldo final fica errado;
--   * breadConfigs ja filtrado por active = true -- o caixa apaga as demais linhas da
--     loja e instala breadConfigs[0];
--   * products vir com ativos E inativos, para o flag active descer e o produto
--     excluido na nuvem sumir das telas sem apagar historico local.
-- ============================================================

CREATE OR REPLACE FUNCTION pull_cadastros(p_token TEXT)
RETURNS JSONB
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = public
AS $$
DECLARE
  v_store  TEXT;
  v_tenant TEXT;
BEGIN
  v_store := public.loja_do_token(p_token);
  IF v_store IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_token');
  END IF;

  SELECT "tenantId" INTO v_tenant
    FROM public."Store"
   WHERE id = v_store AND active = true;

  IF v_tenant IS NULL THEN
    RETURN jsonb_build_object('error', 'invalid_store');
  END IF;

  RETURN jsonb_build_object(
    'storeId',  v_store,
    'tenantId', v_tenant,
    'categories', coalesce((
      SELECT jsonb_agg(to_jsonb(c)) FROM public."Category" c WHERE c."tenantId" = v_tenant
    ), '[]'::jsonb),
    'products', coalesce((
      SELECT jsonb_agg(to_jsonb(p)) FROM public."Product" p WHERE p."tenantId" = v_tenant
    ), '[]'::jsonb),
    'storeProducts', coalesce((
      SELECT jsonb_agg(to_jsonb(sp)) FROM public."StoreProduct" sp WHERE sp."storeId" = v_store
    ), '[]'::jsonb),
    'breadConfigs', coalesce((
      SELECT jsonb_agg(to_jsonb(b)) FROM public."BreadConfig" b
       WHERE b."storeId" = v_store AND b.active = true
    ), '[]'::jsonb),
    'ownerAdjustments', coalesce((
      SELECT jsonb_agg(to_jsonb(o) ORDER BY o."createdAt") FROM public."OwnerStockAdjustment" o
       WHERE o."storeId" = v_store
    ), '[]'::jsonb)
  );
END;
$$;

GRANT EXECUTE ON FUNCTION pull_cadastros(TEXT) TO anon;
