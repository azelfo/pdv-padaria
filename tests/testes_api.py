"""
Testes de regressao que atravessam a API publica do Supabase.

POR QUE ESTES E NAO OUTROS
Sao os testes que nao dependem de refatorar o PDV: falam com o servidor pela
mesma porta que qualquer maquina de loja usa, com a mesma chave publica. O que
eles enxergam e exatamente o que um portador da chave enxerga -- que e o
tamanho real da superficie exposta.

Nem todos passam hoje, DE PROPOSITO. Os testes de vazamento entre redes falham
na versao atual: as tabelas de catalogo respondem a qualquer um. Eles existem
para provar que a correcao funcionou quando ela vier, e para impedir que o
problema volte depois.

COMO RODAR
    py tests/testes_api.py

Sem dependencia externa: so biblioteca padrao. Le as chaves de PdvPadaria/.env.
"""

import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENV = os.path.join(RAIZ, "PdvPadaria", ".env")

# Tabelas de catalogo: hoje respondem a qualquer portador da chave publica.
# O recorte por rede e um filtro que o proprio cliente escolhe mandar.
TABELAS_QUE_VAZAM = ["Product", "Category", "StoreProduct", "BreadConfig", "OwnerStockAdjustment"]

# Tabelas ja no padrao correto: RLS ligado, sem politica, negando tudo.
# Servem de guarda de regressao -- se alguma abrir, o teste acusa.
TABELAS_QUE_NEGAM = ["Sale", "SaleItem", "StockMovement", "User", "Store", "store_sync_secret", "caixa_token"]

INSTALADOR_URL = "https://raw.githubusercontent.com/azelfo/pdv-padaria/main/PdvPadaria/Output/Setup_PadariaVenancio.exe"
INSTALADOR_LOCAL = os.path.join(RAIZ, "PdvPadaria", "Output", "Setup_PadariaVenancio.exe")

resultados = []


def carregar_env():
    if not os.path.exists(ENV):
        sys.exit(f"Nao achei {ENV}. Rode a partir da raiz do projeto.")
    texto = open(ENV, encoding="utf-8").read()
    pares = re.findall(r"^([A-Z_]+)=(.*)$", texto, re.M)
    return {k: v.strip().strip('"') for k, v in pares}


CFG = carregar_env()
BASE = CFG["SUPABASE_URL"].rstrip("/")
CHAVE = CFG["SUPABASE_ANON_KEY"]
CABECALHO = {"apikey": CHAVE, "Authorization": "Bearer " + CHAVE, "Content-Type": "application/json"}


def pedir(caminho, corpo=None):
    """Devolve (status, texto). Nunca lanca -- erro de rede tambem e resultado."""
    req = urllib.request.Request(
        BASE + caminho,
        data=json.dumps(corpo).encode() if corpo is not None else None,
        headers=CABECALHO,
    )
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
    except Exception as e:
        return 0, str(e)


def checa(nome, condicao, detalhe=""):
    resultados.append((nome, bool(condicao), detalhe))


# ---------------------------------------------------------------------------
# 1. VAZAMENTO ENTRE REDES  (falha hoje -- e o problema)
#
# Sem filtro nenhum na consulta, o servidor deveria devolver vazio: a politica
# de linha e que precisa recortar pela rede da credencial. Se voltar linha, o
# recorte esta sendo feito pelo cliente -- ou seja, nao esta sendo feito.
# ---------------------------------------------------------------------------
def teste_vazamento():
    for tabela in TABELAS_QUE_VAZAM:
        status, corpo = pedir(f"/rest/v1/{tabela}?limit=5")
        try:
            linhas = json.loads(corpo) if status == 200 else []
        except ValueError:
            linhas = []
        checa(
            f"[vazamento] {tabela} nao entrega linha sem recorte de rede",
            len(linhas) == 0,
            f"devolveu {len(linhas)} linha(s) para a chave publica",
        )


# ---------------------------------------------------------------------------
# 2. TABELAS SENSIVEIS NEGAM  (passa hoje -- guarda de regressao)
# ---------------------------------------------------------------------------
def teste_nega_por_padrao():
    for tabela in TABELAS_QUE_NEGAM:
        status, corpo = pedir(f"/rest/v1/{tabela}?limit=5")
        try:
            linhas = json.loads(corpo) if status == 200 else []
        except ValueError:
            linhas = []
        vazio = not isinstance(linhas, list) or len(linhas) == 0
        checa(
            f"[nega] {tabela} nao responde a chave publica",
            vazio,
            f"devolveu {len(linhas) if isinstance(linhas, list) else '?'} linha(s)",
        )


# ---------------------------------------------------------------------------
# 3. RECUSA VEM NO CORPO, COM HTTP 200
#
# Foi confiar no codigo HTTP que fez o PDV marcar venda como enviada sem ter
# subido. Este teste trava o contrato: recusa e 200 com "error" dentro.
# ---------------------------------------------------------------------------
def teste_recusa_no_corpo():
    status, corpo = pedir("/rest/v1/rpc/push_estoque",
                          {"p_payload": {"stock": []}, "p_token": "token-que-nao-existe"})
    tem_erro = '"error"' in corpo
    checa("[contrato] push_estoque recusa com HTTP 200 e erro no corpo",
          status == 200 and tem_erro, f"status={status} corpo={corpo[:80]}")

    status, corpo = pedir("/rest/v1/rpc/push_vendas",
                          {"p_payload": {"sales": [], "items": [], "movements": []},
                           "p_token": "token-que-nao-existe"})
    checa("[contrato] push_vendas recusa com HTTP 200 e erro no corpo",
          status == 200 and '"error"' in corpo, f"status={status} corpo={corpo[:80]}")


# ---------------------------------------------------------------------------
# 4. CONTRATO DAS FUNCOES DE IDENTIDADE
# ---------------------------------------------------------------------------
def teste_leitura_recortada():
    """A leitura de catalogo passou a sair por uma funcao que deriva a loja do
    token, igual a escrita. Com token invalido nao pode devolver dado nenhum."""
    status, corpo = pedir("/rest/v1/rpc/pull_cadastros", {"p_token": "token-que-nao-existe"})
    checa("[leitura] pull_cadastros recusa token invalido",
          status == 200 and '"error"' in corpo, f"status={status} corpo={corpo[:80]}")
    checa("[leitura] pull_cadastros nao vaza catalogo para token invalido",
          '"products"' not in corpo, "devolveu catalogo mesmo recusando")


def teste_identidade():
    status, corpo = pedir("/rest/v1/rpc/loja_do_token", {"p_token": "lixo"})
    checa("[identidade] token invalido nao resolve loja nenhuma",
          status == 200 and corpo.strip() == "null", f"status={status} corpo={corpo[:60]}")

    status, corpo = pedir("/rest/v1/rpc/registrar_caixa",
                          {"p_email": "ninguem@invalido.local", "p_senha": "errada",
                           "p_terminal": "teste"})
    checa("[identidade] credencial errada nao emite credencial de maquina",
          status == 200 and "credenciais_invalidas" in corpo, f"corpo={corpo[:80]}")


# ---------------------------------------------------------------------------
# 5. INSTALADOR PUBLICADO E O MESMO QUE FOI COMPILADO
# ---------------------------------------------------------------------------
def digestao(dados):
    return hashlib.sha256(dados).hexdigest()


def teste_instalador():
    if not os.path.exists(INSTALADOR_LOCAL):
        checa("[instalador] publicado confere com o compilado", False, "instalador local nao encontrado")
        return
    local = digestao(open(INSTALADOR_LOCAL, "rb").read())
    try:
        with urllib.request.urlopen(INSTALADOR_URL, timeout=90) as r:
            publicado = digestao(r.read())
    except Exception as e:
        checa("[instalador] publicado confere com o compilado", False, f"falha ao baixar: {e}")
        return
    checa("[instalador] publicado confere com o compilado", local == publicado,
          f"local={local[:16]} publicado={publicado[:16]}")


# ---------------------------------------------------------------------------
# 6. ARQUIVO DE VERSAO DECLARA A IMPRESSAO DIGITAL
#
# Sem este campo o caixa nao tem como conferir o que baixou antes de executar.
# Falha ate a correcao P0-1 ser publicada.
# ---------------------------------------------------------------------------
def teste_versao_declara_digital():
    caminho = os.path.join(RAIZ, "docs", "version.json")
    if not os.path.exists(caminho):
        checa("[atualizacao] version.json declara sha256 do instalador", False, "version.json nao encontrado")
        return
    dados = json.load(open(caminho, encoding="utf-8"))
    tem = isinstance(dados.get("sha256"), str) and len(dados.get("sha256", "")) == 64
    detalhe = "campo ausente" if not tem else ""
    if tem and os.path.exists(INSTALADOR_LOCAL):
        real = digestao(open(INSTALADOR_LOCAL, "rb").read())
        tem = real.lower() == dados["sha256"].lower()
        detalhe = "" if tem else "declarado nao bate com o compilado"
    checa("[atualizacao] version.json declara sha256 do instalador", tem, detalhe)


def garantir_conexao():
    """Sem esta checagem a suite MENTE: erro de rede vira status 0, e os testes de
    vazamento leem 'nao veio linha' como aprovado -- declarando o problema resolvido
    sem nunca ter falado com o servidor."""
    status, _ = pedir("/rest/v1/rpc/loja_do_token", {"p_token": "sonda-de-conexao"})
    if status != 200:
        sys.exit(f"Sem resposta do Supabase (status {status}). "
                 f"Nada foi testado -- corrija a conexao e rode de novo.")


def main():
    garantir_conexao()
    for t in (teste_vazamento, teste_nega_por_padrao, teste_recusa_no_corpo,
              teste_leitura_recortada, teste_identidade, teste_instalador,
              teste_versao_declara_digital):
        t()

    largura = max(len(n) for n, _, _ in resultados)
    passou = falhou = 0
    print()
    for nome, ok, detalhe in resultados:
        marca = "PASSA" if ok else "FALHA"
        print(f"  {marca}  {nome.ljust(largura)}  {detalhe if not ok else ''}".rstrip())
        passou, falhou = (passou + 1, falhou) if ok else (passou, falhou + 1)

    print(f"\n  {passou} passaram, {falhou} falharam de {len(resultados)}")
    if falhou:
        print("  Falha esperada ate a correcao correspondente ser aplicada.")
    return 1 if falhou else 0


if __name__ == "__main__":
    sys.exit(main())
