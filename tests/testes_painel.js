// Sem rede/dependencias: executa o JavaScript publicado com DOM e fetch controlados.
// node tests/testes_painel.js
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '..');
const html = fs.readFileSync(path.join(root, 'docs/index.html'), 'utf8');
const elements = new Map();
function el(id) {
  if (!elements.has(id)) elements.set(id, {
    value: '', innerHTML: '', textContent: '', dataset: {}, disabled: false,
    classList: { add() {}, remove() {} }, focus() {}, selectedOptions: []
  });
  return elements.get(id);
}
let input = { value: '25', focus() {} };
const context = vm.createContext({
  document: { getElementById: el, querySelectorAll: () => [],
    querySelector: () => ({ querySelector: () => input, dataset: {} }) },
  navigator: {}, window: {}, confirm: () => true, setTimeout: () => 0
});
vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
const run = code => vm.runInContext(code, context);
const reply = (body, ok = true) => {
  context.fetch = async () => ({ ok, status: ok ? 200 : 400, json: async () => body });
};
const button = () => ({ dataset: { i: '0' }, disabled: false, textContent: '' });
function stock() {
  run("window._estqProds=[{productId:'p',nome:'Produto',quantidade:0,minimo:0,situacao:'ACABOU',jaLancado:true}]");
  el('estqLoja').value = 'A';
  el('estqCard').dataset.storeId = 'A';
  context.btn = button();
}

async function main() {
  reply({ code: '23505', message: 'Falha de integridade' }, false);
  assert.equal((await run("rpc('teste',{})")).error, 'Falha de integridade');
  context.fetch = async () => { throw new Error('offline'); };
  assert.match((await run("rpc('teste',{})")).error, /Recarregue/);
  context.fetch = async () => ({ ok: true, json: async () => { throw new Error('JSON'); } });
  assert.ok((await run("rpc('teste',{})")).error);
  for (const bad of [null, [], 'ok']) {
    reply(bad);
    assert.ok((await run("rpc('teste',{})")).error);
  }

  for (const response of [{ code: '23505', message: 'Falha' }, { error: 'forbidden' }, {}]) {
    stock(); reply(response, !response.code);
    await run('salvarItem(btn)');
    assert.equal(context.window._estqProds[0].quantidade, 0);
    assert.equal(context.btn.disabled, false);
    await run('excluirItem(btn)');
    assert.equal(context.window._estqProds.length, 1);
    assert.equal(context.btn.disabled, false);
  }
  stock(); context.fetch = async () => { throw new Error('offline'); };
  await run('salvarItem(btn)');
  assert.equal(context.btn.disabled, false);
  assert.equal(context.window._estqProds[0].quantidade, 0);
  await run('excluirItem(btn)');
  assert.equal(context.btn.disabled, false);
  assert.equal(context.window._estqProds.length, 1);

  stock(); reply({ success: true, nova_quantidade: 25 });
  await run('salvarItem(btn)');
  assert.equal(context.window._estqProds[0].quantidade, 25);
  assert.equal(run('situacaoDe(window._estqProds[0])'), 'OK');
  assert.match(el('estqResumo').innerHTML, /1 com estoque/);
  input.value = '0';
  reply({ success: true, nova_quantidade: 0 });
  await run('salvarItem(btn)');
  assert.equal(run('situacaoDe(window._estqProds[0])'), 'ACABOU');
  input.value = '25';

  el('prodNome').value = 'Produto novo';
  el('prodCategoria').selectedOptions = [{ value: 'c', dataset: { nome: 'Geral' } }];
  el('prodUnidade').value = 'UN'; el('prodBarras').value = '789';
  reply({ code: '23505', message: 'Cadastro recusado' }, false);
  await run('salvarNovoProduto()');
  assert.equal(el('prodSalvar').disabled, false);
  assert.equal(el('prodErr').textContent, 'Cadastro recusado');
  context.fetch = async () => { throw new Error('offline'); };
  await run('salvarNovoProduto()');
  assert.equal(el('prodSalvar').disabled, false);
  await run('gerarCodigoInterno()');
  assert.equal(el('prodGerarCodigo').disabled, false);

  const pending = [];
  context.fetch = () => new Promise(resolve => pending.push(resolve));
  el('estqLoja').value = 'A'; const a = run('carregarEstoque()');
  el('estqLoja').value = 'B'; const b = run('carregarEstoque()');
  assert.equal(context.window._estqProds, null);
  pending[1]({ ok: true, json: async () => ({ produtos: [{ nome: 'B', quantidade: 5 }] }) });
  await b;
  pending[0]({ ok: true, json: async () => ({ produtos: [{ nome: 'A', quantidade: 2 }] }) });
  await a;
  assert.equal(el('estqCard').dataset.storeId, 'B');
  assert.equal(context.window._estqProds[0].nome, 'B');
  el('estqLoja').value = '';
  await run('carregarEstoque()');
  assert.equal(context.window._estqProds, null);
  assert.equal(el('estqCard').dataset.storeId, undefined);

  const vendas = Array.from({ length: 500 }, (_, i) => ({ id: String(i), status: 'APROVADO', total_centavos: 1000 }));
  reply({ vendas, resumo: { vendas_qtd: 501, faturamento_centavos: 501000, total_registros: 501 } });
  await run('carregarVendas()');
  assert.match(el('vendaResumo').innerHTML, /5\.010,00/);
  assert.match(el('vendaResumo').innerHTML, />501</);
  assert.match(el('vendaLista').innerHTML, /500 vendas mais recentes/);
  reply({ vendas }); await run('carregarVendas()');
  assert.match(el('vendaResumo').innerHTML, /nesta lista/);
  assert.match(el('vendaLista').innerHTML, /valores acima são parciais/);

  const atrasadas = [];
  context.fetch = () => new Promise(resolve => atrasadas.push(resolve));
  const responder = (i, body) => atrasadas[i]({ ok: true, json: async () => body });
  el('vendaLoja').value = 'A'; const vendasA = run('carregarVendas()');
  el('vendaLoja').value = 'B'; const vendasB = run('carregarVendas()');
  responder(1, { vendas: [{ id: 'B', status: 'APROVADO', total_centavos: 200 }] });
  await vendasB;
  responder(0, { vendas: [{ id: 'A', status: 'APROVADO', total_centavos: 100 }] });
  await vendasA;
  assert.equal(context.window._vendas[0].id, 'B');
  assert.match(el('vendaResumo').innerHTML, /2,00/);

  el('paoLoja').value = 'A'; const paoA = run('carregarPao()');
  el('paoLoja').value = 'B'; const paoB = run('carregarPao()');
  responder(3, { linhas: [{ produto: 'Pao loja B', loja: 'B', receitaNoDia: 200 }] });
  await paoB;
  responder(2, { error: 'Resposta antiga da loja A' });
  await paoA;
  assert.match(el('paoLista').innerHTML, /Pao loja B/);
  assert.match(el('paoResumo').innerHTML, /2,00/);

  const detalheA = run("abrirVendaDetalhe('A')");
  run('fecharVendaModal()');
  const detalheB = run("abrirVendaDetalhe('B')");
  responder(5, { loja: 'Loja B', itens: [], total_centavos: 200 });
  await detalheB;
  responder(4, { loja: 'Loja A', itens: [], total_centavos: 100 });
  await detalheA;
  assert.match(el('vendaModalBody').innerHTML, /Loja B/);
  assert.doesNotMatch(el('vendaModalBody').innerHTML, /Loja A/);

  const vendasLogout = run('carregarVendas()');
  const paoLogout = run('carregarPao()');
  const detalheLogout = run("abrirVendaDetalhe('A')");
  el('sairBtn').onclick();
  responder(6, { vendas: [{ id: 'A', total_centavos: 100 }] });
  responder(7, { linhas: [{ produto: 'Pao loja A', loja: 'A' }] });
  responder(8, { loja: 'Loja A', itens: [], total_centavos: 100 });
  await Promise.all([vendasLogout, paoLogout, detalheLogout]);
  assert.equal(context.window._vendas.length, 0);
  for (const id of ['vendaLista', 'vendaResumo', 'paoLista', 'paoResumo', 'vendaModalBody'])
    assert.equal(el(id).innerHTML, '', 'Logout descarta resposta antiga: ' + id);

  const events = {};
  const sw = fs.readFileSync(path.join(root, 'docs/sw.js'), 'utf8');
  vm.runInNewContext(sw, { URL, self: {
    location: { origin: 'https://padaria.example' }, addEventListener: (name, fn) => events[name] = fn
  }, caches: { match: async () => ({ cached: true }) }, fetch: async () => ({ status: 200 }) });
  for (const url of ['https://raw.githubusercontent.com/owner/repo/main/docs/version.json',
    'https://padaria.example/docs/version.json', 'https://padaria.example/docs/baixar.html',
    'https://database.supabase.co/rest/v1/Product']) {
    events.fetch({ request: { method: 'GET', url }, respondWith: () => assert.fail('Versao/API nao pode usar cache do SW') });
  }
  let intercepted = false;
  events.fetch({ request: { method: 'GET', url: 'https://padaria.example/docs/index.html' },
    respondWith: () => { intercepted = true; } });
  assert.ok(intercepted);
  console.log('Painel: erros HTTP/rede/JSON, escritas, botoes, estoque, corrida de lojas, totais e cache OK.');
}
main().catch(error => { console.error(error); process.exitCode = 1; });
