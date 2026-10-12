// Contratos estaticos; nao conecta nem altera o Supabase.
// node tests/testes_contrato_sql.js
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const reference = fs.readFileSync(path.join(root, 'docs/supabase_rpcs.sql'), 'utf8');
const patch = fs.readFileSync(path.join(root, 'docs/correcoes-auditoria.sql'), 'utf8');
function fn(text, name) {
  const start = text.search(new RegExp('^CREATE OR REPLACE FUNCTION (?:public\\.)?' + name + '\\(', 'm'));
  assert.ok(start >= 0, name);
  return text.slice(start, text.indexOf('\n$$;', start) + 4);
}
for (const name of ['ajustar_estoque', 'somar_estoque_loja', 'get_vendas_rede']) {
  assert.equal(fn(reference, name), fn(patch, name), `${name}: patch e referencia divergem`);
}
for (const name of ['get_loja_estoque', 'ajustar_estoque', 'excluir_produto', 'criar_produto',
  'get_categorias', 'atualizar_produto', 'gerar_codigo_interno', 'get_conferencia_pao',
  'get_vendas_rede', 'get_venda_detalhe', 'somar_estoque_loja']) {
  const queries = fn(reference, name).match(/FROM "User"[\s\S]*?LIMIT 1;/g);
  assert.ok(queries?.length, `${name}: auth ausente`);
  for (const query of queries) assert.match(query, /(?:u\.)?active = true/, `${name}: usuario inativo`);
}
const adjust = fn(patch, 'ajustar_estoque');
assert.match(adjust, /FROM "Product"\s+WHERE id = p_product_id AND "tenantId" = v_tenant_id AND active = true/);
assert.ok(adjust.indexOf('FROM "Product"') < adjust.indexOf('INSERT INTO "StoreProduct"'));
assert.match(adjust, /FOR UPDATE/);
assert.match(adjust, /'NaN', 'Infinity', '-Infinity'/);
const sum = fn(patch, 'somar_estoque_loja');
assert.match(sum, /SET quantity = stock\.quantity \+ excluded\.quantity/);
assert.match(sum, /ORDER BY value->>'productId'/);
assert.match(sum, /HAVING count\(\*\) > 1/);
assert.match(sum, /"tenantId" = v_tenant AND active = true/);
assert.match(sum, /jsonb_typeof\(v_item->'quantity'\) IS DISTINCT FROM 'number'/);
assert.match(sum, /INSERT INTO "OwnerStockAdjustment"[\s\S]*v_after, NULL, v_user_id, clock_timestamp\(\)/);
assert.match(sum, /'success', true, 'items', v_items/);
const sales = fn(patch, 'get_vendas_rede');
assert.ok(sales.indexOf('recentes AS') < sales.indexOf('LIMIT 500'));
assert.match(sales, /sum\(total\)[\s\S]*FROM base/);
assert.match(sales, /'total_registros', count\(\*\)/);
assert.match(sales, /FROM recentes b/);
assert.match(patch, /pg_get_functiondef/);
assert.match(patch, /RAISE EXCEPTION 'Autenticacao/);
assert.doesNotMatch(patch, /CREATE OR REPLACE FUNCTION (?:public\.)?(push_vendas|push_estoque|set_estoque_loja)\(/);
assert.doesNotMatch(patch, /(?:DROP|CREATE|ALTER) POLICY/i);
assert.match(patch, /BEGIN;[\s\S]*COMMIT;/);
console.log('SQL: autenticacao ativa, tenant do produto, soma atomica, totais completos e patch isolado OK.');
