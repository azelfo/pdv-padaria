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
