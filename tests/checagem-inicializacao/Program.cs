using System;
using System.IO;
using PdvPadaria;

var pasta = Path.Combine(Path.GetTempPath(), "pdv-startup-test-" + Guid.NewGuid().ToString("N"));
try
{
    var caminho = StartupDiagnostics.RegistrarEm(pasta, "Antes do WPF");
    Exigir(caminho != null && File.Exists(caminho), "cria diagnóstico antes de usar WPF");

    Exception falha;
    try { throw new InvalidOperationException("causa interna simulada"); }
    catch (Exception inner) { falha = new Exception("falha externa simulada", inner); }
    Exigir(StartupDiagnostics.RegistrarEm(pasta, "Falha ao abrir", falha) == caminho,
        "acrescenta erros ao mesmo arquivo");

    string log = File.ReadAllText(caminho!);
    Exigir(log.Contains("Antes do WPF") && log.Contains("Falha ao abrir"), "preserva registros anteriores");
    Exigir(log.Contains("falha externa simulada") && log.Contains("causa interna simulada"),
        "registra exceções internas");
    Exigir(log.Contains("Program.cs:line") || log.Contains("Program.cs:linha"), "preserva stack trace");
    Exigir(log.Contains("Versão:") && log.Contains("Windows:") && log.Contains("CLR:") && log.Contains("Processo:"),
        "registra versão e ambiente");

    string bloqueio = Path.Combine(pasta, "arquivo-nao-pasta");
    File.WriteAllText(bloqueio, "preservar");
    Exigir(StartupDiagnostics.RegistrarEm(bloqueio, "sem acesso") == null, "falha de gravação não derruba o programa");
    Exigir(File.ReadAllText(bloqueio) == "preservar", "falha de gravação preserva o arquivo existente");
    Console.WriteLine("8 verificações de diagnóstico passaram; nenhum app ou banco real foi aberto.");
}
finally
{
    if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
}

static void Exigir(bool resultado, string nome)
{
    if (!resultado) throw new Exception("FALHOU: " + nome);
}
