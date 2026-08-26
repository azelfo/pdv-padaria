using System;

namespace PdvPadaria.Services
{
    /// <summary>Como a sessão terminou: tudo subiu, ou sobrou coisa na fila.</summary>
    public enum ResultadoDoEncerramento
    {
        Enviado,
        FicouPendente
    }

    /// <summary>
    /// Dono do ciclo de vida de uma sessão: nasce no login, é descartado quando a sessão
    /// acaba — por logout OU por fechar a janela.
    ///
    /// O BURACO QUE ISTO FECHA
    /// A limpeza da sessão existia em um único lugar: o clique no botão "Sair do Sistema".
    /// Fechar pelo X não passava por lá, um encerramento inesperado também não, e uma
    /// segunda tela de login que alguém adicionasse amanhã também não passaria. Ou seja: a
    /// garantia dependia de alguém lembrar, que é exatamente a dependência que já custou
    /// quatro dias de duas lojas sem sincronizar.
    ///
    /// Com o escopo, a limpeza é consequência de descartar um objeto. Quem fecha a janela
    /// não precisa saber o que precisa ser limpo — precisa apenas descartar o escopo.
    ///
    /// O QUE ELE AINDA NÃO DONA
    /// A conexão do banco e o cache de configuração continuam estáticos por enquanto: o
    /// app os abre antes do login, e movê-los para cá é a etapa de remoção dos estáticos.
    /// Este escopo é o lugar onde eles vão entrar, um de cada vez.
    /// </summary>
    public sealed class EscopoDeSessao : IDisposable
    {
        public Sessao Sessao { get; }

        /// <summary>Já foi descartado? Operação em escopo encerrado é recusada.</summary>
        public bool Encerrado { get; private set; }

        public EscopoDeSessao(Sessao sessao)
        {
            Sessao = sessao ?? throw new ArgumentNullException(nameof(sessao));
        }

        /// <summary>
        /// Barra quem tentar usar o escopo depois do fim da sessão. Serve para a resposta
        /// que chega atrasada: ela pertence a uma sessão que não existe mais e não pode
        /// gravar nada na atual.
        /// </summary>
        public void GarantirAtivo()
        {
            if (Encerrado)
                throw new ObjectDisposedException(nameof(EscopoDeSessao),
                    "Esta sessão já foi encerrada. A operação pertence à sessão anterior.");
        }

        /// <summary>
        /// Encerra a sessão tentando ANTES subir o que ainda está na fila.
        ///
        /// Hoje nem o logout nem o fechamento pela janela empurram nada: o logout só espera
        /// o que já estava em curso, e o X fecha direto. A venda não se perde — fica no
        /// SQLite e sobe na próxima abertura — mas dorme naquele PC. Se a máquina morrer,
        /// for trocada ou reinstalada antes de abrir de novo, o rabo do dia vai junto.
        ///
        /// Três regras, nessa ordem de prioridade:
        ///   1. Falhar em subir NUNCA impede de fechar. Um caixa sem internet que não
        ///      fecha é pior que um dado que sobe amanhã.
        ///   2. Nunca esperar para sempre — daí o limite de tempo.
        ///   3. Quem está fechando precisa SABER que ficou coisa para trás. Silêncio aqui
        ///      é como duas lojas passaram quatro dias sem ninguém notar.
        /// </summary>
        public async System.Threading.Tasks.Task<ResultadoDoEncerramento> EncerrarAsync(
            Func<System.Threading.Tasks.Task<bool>> enviarPendentes,
            TimeSpan limite)
        {
            var resultado = ResultadoDoEncerramento.FicouPendente;
            try
            {
                var envio = enviarPendentes();
                var primeiro = await System.Threading.Tasks.Task.WhenAny(
                    envio, System.Threading.Tasks.Task.Delay(limite));

                if (primeiro == envio
                    && envio.Status == System.Threading.Tasks.TaskStatus.RanToCompletion
                    && envio.Result)
                {
                    resultado = ResultadoDoEncerramento.Enviado;
                }
                else
                {
                    // O envio que estourou o tempo segue correndo sozinho; observar o
                    // resultado evita que uma falha dele derrube o processo depois.
                    _ = envio.ContinueWith(t => { _ = t.Exception; },
                        System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EncerrarAsync]: {ex.Message}");
            }
            finally
            {
                Dispose();
            }
            return resultado;
        }

        public void Dispose()
        {
            if (Encerrado) return;
            Encerrado = true;

            // Descarta o que a sessão deixou em memória. A identidade da MÁQUINA (token,
            // loja conhecida, veredito do token) sobrevive de propósito: ela não é da
            // pessoa que estava logada.
            StoreIdentityService.Encerrar();
        }
    }
}
