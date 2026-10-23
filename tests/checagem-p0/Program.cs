using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PdvPadaria.Services;

class Program
{
    static int falhas = 0;
    static bool Lanca<T>(Action acao) where T : Exception
    {
        try { acao(); return false; } catch (T) { return true; } catch { return false; }
    }

    static void Checa(string nome, bool ok, string detalhe = "")
    {
        Console.WriteLine($"  {(ok ? "PASSA" : "FALHA")}  {nome}{(ok ? "" : "  <- " + detalhe)}");
        if (!ok) falhas++;
    }

    static async Task<int> Main()
    {
        var pastaTeste = Path.Combine(Path.GetTempPath(), "pdv-checagem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pastaTeste);
        string? dadosAnterior = Environment.GetEnvironmentVariable("PDV_DADOS_DIR");
        Environment.SetEnvironmentVariable("PDV_DADOS_DIR", pastaTeste);
        try { return await ChecagensLocais(pastaTeste); }
        finally
        {
            Environment.SetEnvironmentVariable("PDV_DADOS_DIR", dadosAnterior);
            string destino = Path.GetFullPath(pastaTeste);
            if (destino.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(destino, true);
        }
    }

    static async Task<int> ChecagensLocais(string pastaTeste)
    {
        // O csproj fornece esta fixture, nunca a configuracao real de uma loja.
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".env"));
        if (!fixture.StartsWith("# PDV_TESTE_OFFLINE") || EnvService.Get("STORE_SYNC_TOKEN") != "")
            throw new InvalidOperationException("A checagem exige a fixture offline, sem credencial real.");
        SQLitePCL.Batteries_V2.Init();

        Console.WriteLine("\n== P0-1: impressao digital do instalador ==");
        var hash = typeof(UpdateService).GetMethod("ImpressaoDigital",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string digital = (string)hash.Invoke(null, new object[] { System.Text.Encoding.UTF8.GetBytes("abc") })!;
        Checa("SHA256 confere com vetor conhecido",
            digital == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
        Checa("bytes diferentes produzem outra impressao",
            digital != (string)hash.Invoke(null, new object[] { System.Text.Encoding.UTF8.GetBytes("abcd") })!);

        var consultaHttp = (System.Net.Http.HttpClient)typeof(UpdateService).GetField("_http",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var downloadHttp = (System.Net.Http.HttpClient)typeof(UpdateService).GetField("_downloadHttp",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        Checa("consulta de versao continua limitada a 10 segundos", consultaHttp.Timeout == TimeSpan.FromSeconds(10));
        Checa("download de componentes pode levar ate 10 minutos", downloadHttp.Timeout == TimeSpan.FromMinutes(10));
        Checa("download e consulta usam clientes separados", !ReferenceEquals(consultaHttp, downloadHttp));
        foreach (string endereco in new[]
        {
            "https://example.invalid/instalador.exe",
            "https://raw.githubusercontent.com/azelfo/pdv-padaria-falso/main/setup.exe",
            "http://raw.githubusercontent.com/azelfo/pdv-padaria/main/setup.exe"
        })
        {
            Checa("instalador fora da origem autorizada e recusado: " + endereco,
                !await UpdateService.DownloadAndInstallAsync(new UpdateInfo { Url = endereco, Sha256 = digital }));
        }

        Console.WriteLine("\n== P0-3: identidade offline e logout ==");
        File.WriteAllText(Path.Combine(pastaTeste, "loja-identidade.txt"), "loja-a");
        await StoreIdentityService.ResolverAsync(string.Empty);
        string antes = StoreIdentityService.StoreId;
        Checa("identidade offline usa o cache da maquina", antes == "loja-a");
        Checa("sem token, nao tenta registrar ou sincronizar", StoreIdentityService.TokenAusente);
        StoreIdentityService.Encerrar();
        Checa("StoreId zerado apos Encerrar", string.IsNullOrEmpty(StoreIdentityService.StoreId));
        Checa("TokenAusente zerado", !StoreIdentityService.TokenAusente);
        await StoreIdentityService.ResolverAsync(string.Empty);
        Checa("2a sessao recupera identidade offline", StoreIdentityService.StoreId == antes);

        Console.WriteLine("\n== cancelamento: so a loja dona pode estornar ==");
        Checa("venda desta loja pode ser cancelada aqui", StoreIdentityService.PertenceAEstaMaquina(antes));
        Checa("venda de outra loja e barrada", !StoreIdentityService.PertenceAEstaMaquina("loja-b"));
        Checa("registro sem loja e barrado", !StoreIdentityService.PertenceAEstaMaquina(null));
        Checa("loja em branco e barrada", !StoreIdentityService.PertenceAEstaMaquina("   "));
        StoreIdentityService.Encerrar();
        Checa("apos logout a maquina ainda sabe sua loja", StoreIdentityService.PertenceAEstaMaquina(antes));

        Console.WriteLine("\n== recusa de credencial sobrevive ao logout ==");
        StoreIdentityService.MarcarTokenInvalido();
        StoreIdentityService.Encerrar();
        Checa("o veredito sobrevive ao logout", StoreIdentityService.TokenInvalido);
        Console.WriteLine("\n== etapa 1: a sessao e imutavel e unica ==");
        var s1 = new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1");
        var s2 = new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1");
        Checa("cada sessao tem geracao propria", s1.Geracao != s2.Geracao,
              "duas sessoes com a mesma geracao: resposta atrasada de uma passaria pela outra");
        Checa("geracao nao vem vazia", !string.IsNullOrWhiteSpace(s1.Geracao));
        // Trocar de loja tem que ser encerrar uma sessao e abrir outra, nunca mutar esta.
        foreach (var prop in new[] { "LojaId", "RedeId", "UsuarioId", "Papel", "Geracao" })
            Checa($"{prop} nao tem setter", typeof(Sessao).GetProperty(prop)?.SetMethod == null,
                  "sessao mutavel: da para trocar a loja sem passar por login");
        Checa("guarda o momento de abertura", s1.AbertaEm > DateTime.MinValue);

        Console.WriteLine("\n== etapa 3: o escopo desfaz a sessao ==");
        await StoreIdentityService.ResolverAsync(string.Empty);   // identidade resolvida
        var escopo = new EscopoDeSessao(new Sessao("u-1", "ATENDENTE", "loja-a", "rede-1"));
        Checa("escopo nasce ativo", !escopo.Encerrado);

        escopo.Dispose();
        Checa("Dispose encerra o escopo", escopo.Encerrado);
        // A limpeza deixa de depender de alguem lembrar de chamar Encerrar() no botao de
        // logout: fechar pelo X passa a descartar o escopo pelo mesmo caminho.
        Checa("Dispose limpa a identidade da sessao",
              string.IsNullOrEmpty(StoreIdentityService.StoreId),
              "estado da sessao anterior sobreviveu ao descarte do escopo");
        Checa("operar num escopo encerrado e recusado",
              Lanca<ObjectDisposedException>(() => escopo.GarantirAtivo()),
              "escopo encerrado ainda aceita operacao");
        escopo.Dispose();
        Checa("Dispose duas vezes nao quebra", escopo.Encerrado);

        Console.WriteLine("\n== etapa 5: encerrar tenta subir o que ficou para tras ==");
        var limite = TimeSpan.FromMilliseconds(300);

        var esc1 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res1 = await esc1.EncerrarAsync(() => Task.FromResult(true), limite);
        Checa("envio ok: encerra sem pendencia", res1 == ResultadoDoEncerramento.Enviado && esc1.Encerrado);

        var esc2 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res2 = await esc2.EncerrarAsync(() => Task.FromResult(false), limite);
        // Falhar em subir NAO pode impedir de fechar -- travaria o caixa sem internet.
        // Mas o chamador precisa saber, para avisar quem esta fechando.
        Checa("envio falhou: encerra assim mesmo, mas avisa",
              res2 == ResultadoDoEncerramento.FicouPendente && esc2.Encerrado);

        var esc3 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var relogio = System.Diagnostics.Stopwatch.StartNew();
        var res3 = await esc3.EncerrarAsync(async () => { await Task.Delay(10000); return true; }, limite);
        relogio.Stop();
        Checa("envio pendurado nao trava o fechamento",
              res3 == ResultadoDoEncerramento.FicouPendente && relogio.ElapsedMilliseconds < 3000,
              $"levou {relogio.ElapsedMilliseconds}ms");
        Checa("mesmo estourando o tempo, o escopo e encerrado", esc3.Encerrado);

        var esc4 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        var res4 = await esc4.EncerrarAsync(() => throw new InvalidOperationException("rede caiu"), limite);
        Checa("erro no envio nao escapa para quem esta fechando",
              res4 == ResultadoDoEncerramento.FicouPendente && esc4.Encerrado);

        Console.WriteLine("\n== etapa 4: nao existe sincronizador sem sessao ==");
        // A garantia e de COMPILACAO: nao ha construtor sem Sessao, entao consulta sem
        // contexto deixa de ser esquecimento e passa a nao compilar. O que da para afirmar
        // em execucao e que passar nulo e recusado na hora, em vez de virar um objeto que
        // grava sem saber por qual loja.
        Checa("nenhum construtor de SyncService dispensa a sessao",
              typeof(SyncService).GetConstructors()
                  .All(ctor => ctor.GetParameters().Any(par => par.ParameterType == typeof(Sessao))),
              "da para montar um sincronizador sem contexto de sessao");
        using (var c2 = new SQLite.SQLiteConnection(Path.Combine(pastaTeste, "vazio.db")))
        {
            Checa("sessao nula e recusada na hora",
                  Lanca<ArgumentNullException>(() => { using var _ = new SyncService(c2, null!); }),
                  "aceitou sessao nula");
        }

        Console.WriteLine("\n== etapa 6: configuracao nao atravessa a sessao ==");
        string urlAntes = EnvService.Get("SUPABASE_URL");
        Checa("config carregada", !string.IsNullOrEmpty(urlAntes));

        int cargasAntes = EnvService.Carregamentos;
        var esc5 = new EscopoDeSessao(new Sessao("u", "ATENDENTE", "loja-a", "rede-1"));
        esc5.Dispose();
        string urlDepois = EnvService.Get("SUPABASE_URL");

        // Contar as cargas e o que distingue "releu" de "continuou em cache". Sem isto o
        // teste passaria mesmo sem implementacao nenhuma -- o cache antigo devolveria o
        // mesmo valor e o verde seria mentira.
        Checa("descartar o escopo derruba o cache de configuracao",
              EnvService.Carregamentos > cargasAntes,
              "config da sessao anterior continuou em memoria");
        Checa("a releitura nao perde nada", urlDepois == urlAntes,
              "caixa abriria a sessao seguinte sem saber falar com a nuvem");

        Console.WriteLine("\n== etapa 7: caixa com .env em branco ainda fala com a nuvem ==");

        // O instalador so grava o .env se ainda nao houver um. Uma maquina instalada com o
        // arquivo em branco ficava em branco para sempre -- "Configuracao da nuvem ausente
        // no .env" no login e na sincronizacao -- e atualizar nao consertava.
        Checa("endereco da nuvem vem embutido",
              EnvService.PadraoEmbutido("SUPABASE_URL").StartsWith("https://"),
              "instalacao nova nasce sem saber falar com a nuvem");
        Checa("chave publica vem embutida",
              EnvService.PadraoEmbutido("SUPABASE_ANON_KEY").StartsWith("eyJ"),
              "instalacao nova nasce sem credencial de leitura");

        // O segredo de escrita nunca pode estar embutido: ele ja vazou uma vez num
        // repositorio publico e foi preciso trocar o token das tres lojas.
        Checa("o segredo de escrita NAO vem embutido",
              EnvService.PadraoEmbutido("STORE_SYNC_TOKEN") == "",
              "credencial de escrita seria distribuida no instalador");
        Checa("a loja NAO vem embutida",
              EnvService.PadraoEmbutido("STORE_ID") == "",
              "toda maquina nasceria carimbada como a mesma loja");

        // O que estiver escrito no .env continua mandando -- senao uma maquina apontada
        // para outro servidor passaria a falar com este sem ninguem perceber.
        string urlDoArquivo = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, ".env"))
            .Where(l => l.TrimStart().StartsWith("SUPABASE_URL="))
            .Select(l => l.Split(new[] { '=' }, 2)[1].Trim().Trim('"'))
            .FirstOrDefault() ?? "";
        Checa("valor escrito no .env vence o embutido",
              urlDoArquivo != "" && EnvService.Get("SUPABASE_URL") == urlDoArquivo,
              "o .env da maquina foi ignorado");

        Console.WriteLine("\n== etapa 8: o caminho do conserto nao depende do que esta quebrado ==");

        // A checagem de atualizacao mora dentro do MainWindow, que so abre depois do login.
        // Se ela ficar atras da identidade ou da primeira sincronizacao, uma maquina mal
        // configurada perde a unica via de receber a correcao -- fica sem conserto remoto.
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz != null && !File.Exists(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml.cs")))
            raiz = raiz.Parent;
        Checa("MainWindow.xaml.cs encontrado", raiz != null, "nao deu para conferir a ordem");
        if (raiz != null)
        {
            string fonte = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml.cs"));
            int ini = fonte.IndexOf("private async void Window_Loaded");
            int fim = fonte.IndexOf("\n        private ", ini + 1);
            string corpo = fonte.Substring(ini, fim - ini);

            int atualizacao = corpo.IndexOf("CheckForUpdateAsync()");
            int sincronizacao = corpo.IndexOf("RunSincronizacaoSilenciosa()");
            int identidade = corpo.IndexOf("StoreIdentityService.ResolverAsync");

            Checa("procurar atualizacao vem antes da sincronizacao",
                  atualizacao >= 0 && sincronizacao > atualizacao,
                  "uma falha de sincronizacao impediria a maquina de se consertar");
            Checa("se a identidade for resolvida na abertura, nao bloqueia a atualizacao",
                  atualizacao >= 0 && (identidade < 0 || identidade > atualizacao),
                  "um token recusado impediria a maquina de se consertar");
        }

        Console.WriteLine("\n== etapa 9: a linha do carrinho acompanha o que foi cobrado ==");

        // Reatribuir o mesmo objeto no mesmo indice (_cartItems[i] = item) NAO redesenha nada:
        // para o WPF o DataContext da linha continua sendo o mesmo objeto. Como o total e
        // escrito direto em TotalText.Text, a tela se contradizia -- linha "1 UN", total
        // cobrando por 2 -- e o operador clicava de novo, cobrando o cliente a mais.
        if (raiz != null)
        {
            string cs = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml.cs"));
            string xaml = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml"));

            Checa("ninguem mais reatribui o item no mesmo indice",
                  !cs.Contains("_cartItems[index] ="),
                  "voltou o padrao que cobra sem mostrar");
            Checa("existe um unico jeito de redesenhar o carrinho",
                  cs.Contains("private void RedesenharCarrinho() => CartItemsList.Items.Refresh();"),
                  "o redesenho do carrinho sumiu ou mudou de forma");

            // Toda mutacao de item ja no carrinho tem de terminar em redesenho.
            int mutacoes = System.Text.RegularExpressions.Regex.Matches(cs, @"\.Quantity (\+|-)= ").Count;
            int redesenhos = System.Text.RegularExpressions.Regex.Matches(cs, @"RedesenharCarrinho\(\);").Count;
            Checa("cada mudanca de quantidade avisa a tela",
                  redesenhos >= mutacoes,
                  $"{mutacoes} mudancas de quantidade para {redesenhos} redesenhos");

            // Sem Style proprio, o botao cai no template padrao do Windows: no hover ele pinta
            // azul-claro e o simbolo quase branco some -- some justo na hora de clicar.
            foreach (var clique in new[] { "IncreaseQuantity_Click", "DecreaseQuantity_Click" })
            {
                int i = xaml.IndexOf(clique);
                int abre = i < 0 ? -1 : xaml.LastIndexOf("<Button", i);
                string tag = (i < 0 || abre < 0) ? "" : xaml.Substring(abre, i - abre);
                Checa($"botao {(clique[0] == 'I' ? "+" : "-")} nao usa o template do Windows",
                      tag.Contains("Style=\"{StaticResource"),
                      "o botao some no azul-claro do hover");
            }
        }

        Console.WriteLine("\n== etapa 10: recusar a venda ANTES de o dinheiro entrar ==");

        // A conferencia de estoque rodava so na hora de gravar -- depois de o operador
        // escolher a forma de pagamento, receber o dinheiro e anunciar o troco. A recusa
        // chegava com o dinheiro na mao e a venda nao era gravada.
        if (raiz != null)
        {
            string cs = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml.cs"));

            int ini = cs.IndexOf("private async void AbrirModalPagamento");
            int fim = ini < 0 ? -1 : cs.IndexOf("\n        private ", ini + 1);
            string corpo = (ini < 0 || fim < 0) ? "" : cs.Substring(ini, fim - ini);

            int confere = corpo.IndexOf("EstoqueDoCarrinhoConfereAsync");
            int cobra = corpo.IndexOf("ShowDialog()");
            Checa("a conferencia de estoque vem antes da tela de pagamento",
                  confere >= 0 && cobra > confere,
                  "a venda voltaria a ser recusada com o dinheiro ja recebido");
            Checa("F12 apertado duas vezes nao abre dois pagamentos",
                  corpo.Contains("_abrindoPagamento"),
                  "a espera pela consulta ao banco virou brecha para dois pagamentos");
            Checa("gravar ainda reconfere (o outro caixa pode ter vendido)",
                  System.Text.RegularExpressions.Regex.Matches(cs, "EstoqueDoCarrinhoConfereAsync\\(\\)").Count >= 3,
                  "sobrou so uma conferencia; a corrida entre dois caixas volta");

            // O catalogo em memoria alimenta a trava da entrada do carrinho. Se ele nao for
            // descartado, a venda seguinte e conferida contra o estoque de antes da anterior.
            Checa("a venda descarta o catalogo em memoria",
                  cs.Contains("_catalogoBusca.Clear();"),
                  "a proxima venda usaria o estoque de antes desta");
        }

        Console.WriteLine("\n== etapa 11: auditoria de tela-mentindo — segundo lote ==");

        // Auditoria multi-agente (12 agentes, 47 achados confirmados, 16 refutados por
        // caminho impossivel) sobre o resto do app. Estas checagens cobrem so os achados
        // REAIS e de maior gravidade que foram corrigidos nesta rodada.
        if (raiz != null)
        {
            string mw = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "MainWindow.xaml.cs"));
            string appXaml = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "App.xaml"));
            string loginCs = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "Views", "LoginWindow.xaml.cs"));
            string payCs = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "Views", "PaymentWindow.xaml.cs"));
            string payXaml = File.ReadAllText(Path.Combine(raiz.FullName, "PdvPadaria", "Views", "PaymentWindow.xaml"));

            // Pao era o UNICO dos tres caminhos de entrada no carrinho sem nenhuma trava de
            // estoque -- entrava sempre, calado, e so era recusado com o dinheiro na mao.
            Checa("pao lancado por valor confere estoque na entrada",
                  mw.Contains("private bool AddBreadProductToCart") &&
                  System.Text.RegularExpressions.Regex.IsMatch(mw,
                      @"private bool AddBreadProductToCart[\s\S]{0,1500}LocalStockQuantity < jaNoCarrinho \+ quantidadePaes"),
                  "pao voltou a entrar no carrinho sem checar o estoque");

            // A revalidacao antes de pagar conferia LINHA por linha -- duas linhas de 20 e 16
            // paes, nenhuma sozinha estourava 30 em estoque, a soma sim.
            Checa("a revalidacao de estoque soma por PRODUTO, nao por linha do carrinho",
                  System.Text.RegularExpressions.Regex.IsMatch(mw,
                      @"EstoqueDoCarrinhoConfereAsync[\s\S]{0,1500}GroupBy\(i => i\.ProductId\)"),
                  "voltou a comparar linha a linha; da para vender mais do que existe somando linhas");

            // "Repor" na aba Alertas so olhava o seletor de loja da aba Estoque: abrir Alertas
            // na Loja B e clicar Repor ajustava a loja que a aba Estoque estivesse mostrando.
            Checa("o botao Repor (aba Alertas) usa o seletor de loja da PROPRIA aba",
                  mw.Contains("MainTabControl.SelectedIndex == 4") &&
                  mw.Contains("AdjustStockRemoteAlerta") &&
                  mw.Contains("StockAlertsList.ItemsSource as List<StockAlertView>"),
                  "o ajuste da aba Alertas pode voltar a cair na loja errada");

            // O catalogo de sugestoes da tela de venda so era descartado apos uma venda. A
            // sincronizacao (a cada 60s, ou pelo botao) atualiza preco/estoque no SQLite mas
            // o caixa continuava vendendo pelos dados de antes de sincronizar.
            Checa("sincronizar com sucesso descarta o catalogo de sugestoes",
                  System.Text.RegularExpressions.Regex.IsMatch(mw,
                      @"if \(pullSuccess\) _catalogoBusca\.Clear\(\);"),
                  "o caixa pode voltar a vender pelo preco/estoque de antes do ultimo sync");

            // O campo de senha continuava ATIVO com o botao dizendo "Entrando...": Enter de
            // novo iniciava um SEGUNDO login, revogando o token que o primeiro tinha acabado
            // de criar.
            Checa("Enter na senha nao dispara um segundo login em cima do primeiro",
                  loginCs.Contains("e.Key == Key.Enter && LoginButton.IsEnabled"),
                  "duplo Enter pode voltar a abrir duas sessoes/derrubar o token da maquina");

            // O painel verde de troco mostrava "R$ 0,00" tanto quando o cliente pagou o valor
            // exato (pode fechar) quanto quando pagou de menos (NAO pode) -- mesmo texto,
            // mesma cor, para duas situacoes opostas.
            Checa("o painel de troco distingue 'pode fechar' de 'falta receber'",
                  payCs.Contains("Falta receber:") && payCs.Contains("MostrarPainelTroco"),
                  "voltou a mostrar R$ 0,00 tanto pro caso valido quanto pro insuficiente");
            Checa("o painel de troco tem nome para trocar de cor",
                  payXaml.Contains("x:Name=\"TrocoPanel\"") && payXaml.Contains("x:Name=\"TrocoLabel\""),
                  "o codigo-behind nao tem como pintar o painel de vermelho");

            // Todo Button sem Style proprio caia no chrome padrao do Windows: hover pinta
            // azul-claro por cima do Background, o texto claro (a maioria neste app) some.
            Checa("existe um Style implicito de Button (App.xaml) com Template proprio",
                  System.Text.RegularExpressions.Regex.IsMatch(appXaml,
                      @"<Style TargetType=""Button"">[\s\S]{0,300}<Setter Property=""Template"">"),
                  "botao sem Style= volta a sumir no hover azul do Windows");
            Checa("FinishCashBtn (fechar a venda em dinheiro) tem Template proprio",
                  System.Text.RegularExpressions.Regex.IsMatch(payXaml,
                      @"FinishCashBtn[\s\S]{0,2000}<Button\.Style>[\s\S]{0,2000}<Setter Property=""Template"">"),
                  "o Style local dele bloqueia o implicito e ele continua no chrome do Windows");

            // A barreira real e exercitada em regressao-sync; aqui conferimos seus callers WPF.
            string Metodo(string assinatura)
            {
                int inicio = mw.IndexOf(assinatura);
                if (inicio < 0) return "";
                int final = mw.IndexOf("\n        private ", inicio + 1);
                return mw.Substring(inicio, (final < 0 ? mw.Length : final) - inicio);
            }
            string venda = Metodo("private async void FinishSaleFlow");
            string encerrar = Metodo("private async Task<bool> EncerrarSessaoAsync");
            string pagamento = Metodo("private async void AbrirModalPagamento");
            Checa("a janela principal nao resolve identidade fora do login",
                !mw.Contains("StoreIdentityService.ResolverAsync("),
                "resposta de identidade pode chegar depois do logout e trocar a loja do novo login");
            Checa("venda usa identidade imutavel da sessao",
                venda.Contains("_escopo.Sessao.RedeId") && venda.Contains("_escopo.Sessao.LojaId") &&
                !venda.Contains("StoreIdentityService.Atual("));
            Checa("gravacao da venda entra na barreira dentro da transacao",
                venda.Contains("RunInTransactionAsync((tx) => _escopo.Sessao.ExecutarSeAtiva(() =>"));
            int esperaVenda = encerrar.IndexOf("if (!await _saleGate.WaitAsync(");
            int liberaVenda = encerrar.IndexOf("_saleGate.Release();");
            string vendaEmCurso = esperaVenda < 0 || liberaVenda < esperaVenda ? "" :
                encerrar.Substring(esperaVenda, liberaVenda - esperaVenda);
            Checa("venda em andamento adia logout sem encerrar a sessao",
                vendaEmCurso.Contains("return false;") && vendaEmCurso.Contains("IsEnabled = true;") &&
                vendaEmCurso.Contains("_encerrandoEmCurso = false;") && !vendaEmCurso.Contains("Dispose("));
            Checa("logout adiado nao abre outra janela de login",
                Metodo("private async void LogoutButton_Click").Contains("if (!await EncerrarSessaoAsync()) return;"));
            Checa("fechamento adiado mantem a janela aberta",
                Metodo("private async void Window_Closing").Contains("if (await EncerrarSessaoAsync()) Close();"));
            int consulta = pagamento.IndexOf("await EstoqueDoCarrinhoConfereAsync()");
            int modal = pagamento.IndexOf("new Views.PaymentWindow");
            string depoisDaConsulta = consulta < 0 || modal < consulta ? "" : pagamento.Substring(consulta, modal - consulta);
            Checa("pagamento nao abre ao voltar de consulta apos logout",
                depoisDaConsulta.Contains("_encerrandoEmCurso") && depoisDaConsulta.Contains("_escopo.Encerrado"));
        }


        Console.WriteLine($"\n  {(falhas == 0 ? "tudo passou" : falhas + " falharam")}");
        return falhas;
    }
}
