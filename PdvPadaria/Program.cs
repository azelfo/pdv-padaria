using System;
using System.Runtime.CompilerServices;

namespace PdvPadaria
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                StartupDiagnostics.Registrar("Exceção não tratada", args.ExceptionObject as Exception);

            try
            {
                StartupDiagnostics.Registrar("Início do programa");
                StartupDiagnostics.VerificarComponenteGrafico();
                return ExecutarAplicacao();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.MostrarErroFatal("Carregamento do aplicativo", ex);
                return 1;
            }
        }

        // O JIT só deve carregar as referências WPF depois que o diagnóstico estiver instalado.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ExecutarAplicacao()
        {
            var app = new App();
            StartupDiagnostics.Registrar("Carregando recursos da interface");
            app.InitializeComponent();
            return app.Run();
        }
    }
}
