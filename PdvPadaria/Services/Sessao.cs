using System;

namespace PdvPadaria.Services
{
    /// <summary>
    /// Quem está operando o caixa agora, e por qual loja.
    ///
    /// POR QUE ISTO EXISTE
    /// Até aqui não havia nada no sistema que representasse "uma sessão". A janela fazia
    /// esse papel por acidente: o estado morria porque a janela era destruída, não porque
    /// alguém o destruía. O que era estático — configuração, conexão do banco, identidade
    /// resolvida — simplesmente atravessava o logout, e a limpeza dependia de alguém
    /// lembrar de chamar um método, em um único caminho de saída.
    ///
    /// IMUTÁVEL DE PROPÓSITO
    /// Não existe "trocar a loja da sessão". Trocar de loja é encerrar esta e abrir outra.
    /// Enquanto isso era um campo que dava para reatribuir, a máquina conseguia estar no
    /// meio do caminho entre duas lojas — vendendo por uma e mostrando o estoque de outra.
    ///
    /// A GERAÇÃO
    /// Carimba a sessão. Serve para descartar o que chega atrasado: uma resposta de rede
    /// iniciada na sessão anterior não pode gravar nada na atual. Hoje as travas de venda e
    /// sincronização cobrem a maior parte disso; a geração é o que fecha o resto, quando as
    /// operações passarem a carregá-la.
    /// </summary>
    public sealed class Sessao
    {
        public string Geracao { get; }
        public string UsuarioId { get; }
        public string Papel { get; }
        public string LojaId { get; }
        public string RedeId { get; }
        public DateTime AbertaEm { get; }

        public Sessao(string usuarioId, string papel, string lojaId, string redeId)
        {
            Geracao = Guid.NewGuid().ToString("N");
            UsuarioId = usuarioId ?? string.Empty;
            Papel = papel ?? string.Empty;
            LojaId = lojaId ?? string.Empty;
            RedeId = redeId ?? string.Empty;
            AbertaEm = DateTime.Now;
        }

        /// <summary>
        /// É a mesma sessão que iniciou aquela operação? Falso quando a operação nasceu
        /// numa sessão que já foi encerrada — o resultado dela deve ser descartado.
        /// </summary>
        public bool MesmaGeracao(string geracao) =>
            !string.IsNullOrEmpty(geracao) && string.Equals(geracao, Geracao, StringComparison.Ordinal);

        public bool EhDono => string.Equals(Papel, "DONO", StringComparison.OrdinalIgnoreCase);
    }
}
