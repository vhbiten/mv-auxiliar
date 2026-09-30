using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Atualização automática: o app compara a própria versão (AppConfig.Versao) com a
    // última release pública em github.com/vhbiten/mv-auxiliar e, se houver mais nova,
    // baixa o pacote e troca o próprio exe — a farmácia não depende de ninguém copiar
    // zip. Publicação do outro lado: publish.ps1 na raiz do projeto.
    //
    // Regras do pacote ao aplicar:
    //  - só o mv-auxiliar.exe é SUBSTITUÍDO (o exe em execução é renomeado para .old,
    //    o Windows permite; o resíduo é apagado no próximo início);
    //  - qualquer outro arquivo do zip (dados\, Desbloquear.bat) só é criado se NÃO
    //    existir — as listas editadas na farmácia (contagens, régua, embalagens...)
    //    nunca são sobrescrevidas por uma atualização;
    //  - config.txt nem vem no pacote (tem o IP da rede local) e seria pulado se viesse;
    //  - qualquer falha desfaz a troca e o app segue na versão atual, sem incomodar.
    public static class AtualizadorApp
    {
        private const string UrlBase = "https://github.com/vhbiten/mv-auxiliar/releases/latest/download/";

        // Verifica e aplica em silêncio. Devolve a versão nova (ex.: "0.2.0") quando o
        // exe no disco já foi trocado — aí é só reiniciar — ou null quando não há nada,
        // não há internet ou algo falhou (tudo apenas logado; o app nunca trava por isso).
        //
        // 'aviso' recebe o estágio em texto para a tela de login ("Verificando
        // atualizações...", "Baixando a versão X..."): sem ele o reinício parecia um
        // bug para quem estava olhando (a janela fechava e voltava do nada). String
        // vazia = nada a mostrar. O texto FINAL (vai reabrir) é de quem chama, que é
        // quem sabe se vai reiniciar ou se alguém já logou.
        public static async Task<string> AtualizarAsync(Action<string> aviso)
        {
            Action<string> diz = aviso ?? delegate(string s) { };
            try
            {
                // .NET 4.8 no Windows 10/11 já negocia TLS 1.2, mas custa nada garantir.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("mv-auxiliar");

                    // Query única para furar o cache do CDN. Atenção à pegadinha (validada ao
                    // vivo em 29/08): o "latest/download" responde 302 e a Location DESCARTA a
                    // query — então ela só vale em URL direta de release. No versao.txt via
                    // latest um cache velho apenas atrasa a detecção em minutos (inofensivo);
                    // o ZIP, porém, é baixado pela URL VERSIONADA "vX.Y.Z", onde a query
                    // funciona de verdade — e cravar a versão elimina por construção a corrida
                    // "zip velho em cache com versao.txt novo".
                    diz("Verificando atualizações...");
                    string cb = "?t=" + DateTime.UtcNow.Ticks;
                    string texto = (await http.GetStringAsync(UrlBase + "versao.txt" + cb)).Trim();
                    Version nova, atual;
                    if (!Version.TryParse(texto, out nova) || !Version.TryParse(AppConfig.Versao, out atual)) { diz(""); return null; }
                    if (nova <= atual) { diz(""); return null; }

                    Logger.Log("Atualizador: versão " + texto + " disponível (esta é " + AppConfig.Versao + ") — baixando.");
                    diz("Baixando a versão " + texto + "...");
                    string zipTmp = Path.Combine(Path.GetTempPath(), "mv-auxiliar-atualizacao.zip");
                    string urlZip = "https://github.com/vhbiten/mv-auxiliar/releases/download/v" + texto + "/mv-auxiliar.zip" + cb;
                    File.WriteAllBytes(zipTmp, await http.GetByteArrayAsync(urlZip));
                    diz("Instalando a atualização...");
                    try { Aplicar(zipTmp); }
                    finally { try { File.Delete(zipTmp); } catch { } }
                    Logger.Log("Atualizador: versão " + texto + " aplicada.");
                    return texto;
                }
            }
            catch (Exception ex)
            {
                // Sem internet ou falha qualquer: some em silêncio (o motivo fica no log).
                Logger.Log("Atualizador: sem atualização agora (" + ex.Message + ").");
                diz("");
                return null;
            }
        }

        // Troca os arquivos no disco. O processo atual continua rodando a versão velha;
        // quem chama decide reiniciar (só faz sentido se ninguém estiver logado).
        private static void Aplicar(string zipPath)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string baseDir = Path.GetDirectoryName(exe);
            string nomeExe = Path.GetFileName(exe);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                // Valida antes de tocar em qualquer coisa: o pacote precisa trazer o exe inteiro.
                ZipArchiveEntry entradaExe = null;
                foreach (var e in zip.Entries)
                    if (string.Equals(Nome(e), nomeExe, StringComparison.OrdinalIgnoreCase)) { entradaExe = e; break; }
                if (entradaExe == null || entradaExe.Length < 200 * 1024)
                    throw new InvalidDataException("pacote sem o executável esperado");

                string velho = exe + ".old";
                if (File.Exists(velho)) File.Delete(velho);
                File.Move(exe, velho);
                try
                {
                    foreach (var e in zip.Entries)
                    {
                        string rel = Nome(e);
                        if (rel.Length == 0 || rel.EndsWith("\\")) continue;          // pasta, não arquivo
                        if (rel.Contains("..") || Path.IsPathRooted(rel)) continue;   // nada fora da pasta do app
                        bool ehExe = string.Equals(rel, nomeExe, StringComparison.OrdinalIgnoreCase);
                        // Arquivos DA MÁQUINA: nunca vêm de pacote (config tem o IP da rede,
                        // licenca é a chave de acesso desta instalação; o carimbo e a janela
                        // são estado local — um zip antigo chegou a embarcar o carimbo).
                        if (!ehExe && (string.Equals(rel, "config.txt", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(rel, "licenca.txt", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(rel, "dados\\licenca-carimbo.txt", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(rel, "dados\\janela.txt", StringComparison.OrdinalIgnoreCase))) continue;
                        string destino = Path.Combine(baseDir, rel);
                        if (!ehExe && File.Exists(destino)) continue;                 // dados locais são sagrados
                        Directory.CreateDirectory(Path.GetDirectoryName(destino));
                        e.ExtractToFile(destino, true);
                    }
                }
                catch
                {
                    // Devolve o exe velho ao lugar para o app continuar abrindo normalmente.
                    if (!File.Exists(exe) && File.Exists(velho)) File.Move(velho, exe);
                    throw;
                }
            }
        }

        // Caminho de uma entrada do zip normalizado (o Compress-Archive grava "\") e sem a
        // pasta-raiz do pacote: "mv-auxiliar\dados\soros.txt" -> "dados\soros.txt".
        private static string Nome(ZipArchiveEntry e)
        {
            string s = e.FullName.Replace('/', '\\').TrimStart('\\');
            int i = s.IndexOf('\\');
            if (i > 0 && string.Equals(s.Substring(0, i), "mv-auxiliar", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(i + 1);
            return s;
        }

        // Apaga o exe da versão anterior (fica travado enquanto o processo que atualizou
        // encerra; por isso a limpeza é sempre no início SEGUINTE, e falhar é normal).
        public static void LimparResiduo()
        {
            try
            {
                string velho = Process.GetCurrentProcess().MainModule.FileName + ".old";
                if (File.Exists(velho)) File.Delete(velho);
            }
            catch { }
        }

        // Sem isto o Windows NEGA o primeiro plano à janela nova: quem inicia um processo
        // e morre em seguida não passa o direito de foco adiante, e o app reaberto ficava
        // ATRÁS de tudo, "minimizado" na barra de tarefas (relatado na farmácia em 29/08).
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int dwProcessId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Abre o exe recém-trocado e encerra este processo. Só chamar na tela de login:
        // depois de logado, a versão nova fica no disco e vale no próximo início.
        // A ordem importa: cria o processo novo, TRANSFERE a permissão de primeiro plano
        // e só morre quando a janela nova já existe (WaitForInputIdle) — assim a troca é
        // uma janela cobrindo a outra, não um fecha-abre com o app sumindo para trás.
        public static void Reiniciar()
        {
            Process novo = null;
            try { novo = Process.Start(Process.GetCurrentProcess().MainModule.FileName); }
            catch (Exception ex)
            {
                // Não conseguiu reabrir: NÃO se encerra o que está funcionando — o app
                // segue na versão antiga e a nova vale no próximo início manual.
                Logger.Log("Atualizador: não consegui reabrir o programa (" + ex.Message + ").");
                return;
            }
            try { AllowSetForegroundWindow(novo.Id); } catch { }
            try { novo.WaitForInputIdle(10000); } catch { }
            // Este processo ainda é o dono do primeiro plano (o usuário está olhando o
            // aviso de reabertura): ativa a janela nova ELE MESMO antes de morrer — é
            // determinístico, não depende da corrida de ativação do processo novo. Se o
            // usuário estiver em OUTRO programa, o Windows nega e a janela fica na barra
            // piscando, que é o comportamento educado.
            try
            {
                for (int i = 0; i < 30; i++)
                {
                    novo.Refresh();
                    if (novo.MainWindowHandle != IntPtr.Zero) { SetForegroundWindow(novo.MainWindowHandle); break; }
                    System.Threading.Thread.Sleep(100);
                }
            }
            catch { }
            Environment.Exit(0);
        }
    }
}
