using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PdvPadaria
{
    internal static class StartupDiagnostics
    {
        internal static void VerificarComponenteGrafico()
        {
            var windows = Environment.OSVersion.Version;
            if (windows.Major != 6 || windows.Minor != 1) return;

            // No processo x86, o Windows redireciona a pasta de sistema para a DLL de 32 bits.
            string caminho = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "D3DCompiler_47.dll");
            IntPtr modulo = LoadLibraryW(caminho);
            int codigo = modulo == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (modulo != IntPtr.Zero)
            {
                try
                {
                    if (GetProcAddress(modulo, "D3DCompile") == IntPtr.Zero) codigo = 127;
                }
                finally { FreeLibrary(modulo); }
            }
            if (codigo != 0)
            {
                throw new InvalidOperationException(
                    "O Windows 7 não conseguiu usar o componente gráfico D3DCompiler_47.dll.\n" +
                    $"Código do Windows: {codigo}.\n" +
                    "Execute o instalador Setup_PadariaVenancio_Completo.exe para verificar os componentes necessários.",
                    new Win32Exception(codigo));
            }
        }

        internal static string? Registrar(string etapa, Exception? erro = null)
        {
            try
            {
                var diretorio = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pdv-padaria");
                var caminho = RegistrarEm(diretorio, etapa, erro);
                return caminho ?? RegistrarEm(Path.Combine(Path.GetTempPath(), "pdv-padaria"), etapa, erro);
            }
            catch
            {
                return null;
            }
        }

        internal static string? RegistrarEm(string diretorio, string etapa, Exception? erro = null)
        {
            try
            {
                Directory.CreateDirectory(diretorio);
                string caminho = Path.Combine(diretorio, "erro.log");
                File.AppendAllText(caminho,
                    $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {etapa} ==={Environment.NewLine}" +
                    $"Versão: {Assembly.GetExecutingAssembly().GetName().Version}; " +
                    $"Windows: {Environment.OSVersion}; CLR: {Environment.Version}; " +
                    $"Processo: {IntPtr.Size * 8} bits; SO: {(Environment.Is64BitOperatingSystem ? 64 : 32)} bits{Environment.NewLine}" +
                    (erro == null ? string.Empty : erro + Environment.NewLine) + Environment.NewLine);
                return caminho;
            }
            catch
            {
                return null;
            }
        }

        internal static void MostrarErroFatal(string etapa, Exception erro)
        {
            var log = Registrar(etapa, erro);
            string detalhe = erro.Message;
            var causa = erro.GetBaseException();
            if (causa != erro && causa.Message != detalhe) detalhe += "\n\n" + causa.Message;
            string registro = log == null
                ? "Não foi possível gravar o diagnóstico. Anote ou fotografe esta mensagem."
                : "Envie este arquivo ao suporte:\n" + log;
            try
            {
                // Este aviso também precisa funcionar quando a própria interface WPF não carrega.
                MessageBoxW(IntPtr.Zero,
                    "O PDV não conseguiu iniciar.\n\n" + detalhe + "\n\n" + registro,
                    "PDV - Erro de inicialização", 0x10);
            }
            catch { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr janela, string mensagem, string titulo, uint tipo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string caminho);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr modulo, string nome);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FreeLibrary(IntPtr modulo);
    }
}
