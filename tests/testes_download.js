const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const html = fs.readFileSync(path.join(__dirname, "../docs/baixar.html"), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

async function carregar(manifesto, ok = true) {
  const elementos = { versao: { textContent: "Versão atual" } };
  for (const id of ["baixar-completo", "baixar-padrao"])
    elementos[id] = { href: html.match(new RegExp(`id="${id}"[^>]*href="([^"]+)"`))[1] };
  vm.runInNewContext(script, {
    fetch: async () => ({ ok, json: async () => manifesto }),
    document: { getElementById: (id) => elementos[id] }
  });
  await new Promise(setImmediate);
  return elementos;
}

(async () => {
  const completo = "https://github.com/azelfo/pdv-padaria/releases/download/v1.1.19/Setup_PadariaVenancio_Completo.exe";
  const padrao = "https://raw.githubusercontent.com/azelfo/pdv-padaria/main/PdvPadaria/Output/Setup_PadariaVenancio.exe";
  const valido = await carregar({ version: "1.1.19", fullUrl: completo, url: padrao });
  assert.equal(valido.versao.textContent, "Versão 1.1.19");
  assert.equal(valido["baixar-completo"].href, completo);
  assert.equal(valido["baixar-padrao"].href, padrao);

  const recusado = await carregar({ fullUrl: "javascript:alert(1)", url: "https://example.invalid/setup.exe" });
  assert.match(recusado["baixar-completo"].href, /\/v1\.1\.18\/Setup_PadariaVenancio_Completo\.exe$/);
  assert.equal(recusado["baixar-padrao"].href, padrao);
  const indisponivel = await carregar({ version: "incorreta" }, false);
  assert.equal(indisponivel.versao.textContent, "Versão atual");
  assert.equal((await carregar(null)).versao.textContent, "Versão atual");
  console.log("7 verificações da página de download passaram, sem acesso à rede.");
})().catch((erro) => { console.error(erro); process.exitCode = 1; });
