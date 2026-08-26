using System;

namespace PdvPadaria.Services
{
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
