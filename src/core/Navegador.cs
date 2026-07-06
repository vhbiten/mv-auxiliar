using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SoulmvKit.Core
{
    // Abre URLs (PDFs de comprovante/relatório) no navegador CERTO da farmácia:
    // 1º Cent Browser (portable — não se registra como padrão do Windows, então o
    //    Process.Start(url) comum cairia no Edge); 2º Edge; 3º padrão do Windows.
    // O caminho encontrado fica gravado em dados\navegador.txt — pode ser editado à
    // mão se o Cent estiver numa pasta fora das procuradas.
    public static class Navegador
    {
        private static readonly object _lock = new object();
        private static string _exe;          // navegador preferido descoberto
        private static bool _procurado;
        private static string _cfgPath;      // dados\navegador.txt

        public static void Init(string dirBase)
        {
            lock (_lock)
            {
                try
                {
                    string dirDados = Path.Combine(dirBase, "dados");
                    Directory.CreateDirectory(dirDados);
                    _cfgPath = Path.Combine(dirDados, "navegador.txt");
                }
                catch (Exception ex) { Logger.Log("Navegador: " + ex.Message); }
            }
        }

        // Abre a URL: Cent Browser -> Edge -> padrão do Windows.
        // Só deixa a exceção subir se TODAS as opções falharem (o chamador já trata).
        public static void Abrir(string url)
        {
            string exe = ExePreferido();
            if (exe != null)
            {
                try { Process.Start(exe, "\"" + url + "\""); return; }
                catch (Exception ex) { Logger.Log("Navegador: falha com " + exe + ": " + ex.Message); }
            }

            string edge = AcharEdge();
            if (edge != null)
            {
                try { Process.Start(edge, "\"" + url + "\""); return; }
                catch (Exception ex) { Logger.Log("Navegador: falha com o Edge: " + ex.Message); }
            }

            Process.Start(url);   // padrão do Windows
        }

        private static string ExePreferido()
        {
            lock (_lock)
            {
                if (_procurado) return _exe;
                _procurado = true;

                // 1) caminho fixado à mão (ou achado numa execução anterior)
                try
                {
                    if (_cfgPath != null && File.Exists(_cfgPath))
                    {
                        string manual = File.ReadAllText(_cfgPath).Trim();
                        if (manual.Length > 0 && File.Exists(manual)) { _exe = manual; return _exe; }
                    }
                }
                catch (Exception ex) { Logger.Log("Navegador: cfg: " + ex.Message); }

                // 2) procura automática do Cent Browser
                _exe = ProcurarCent(BasesPadrao());
                if (_exe != null)
                {
                    Logger.Log("Navegador: Cent Browser encontrado em " + _exe);
                    try { if (_cfgPath != null) File.WriteAllText(_cfgPath, _exe); } catch { }
                }
                else Logger.Log("Navegador: Cent Browser não encontrado — PDFs abrirão no Edge/padrão.");
                return _exe;
            }
        }

        // Onde procurar: instalações comuns + pastas onde um portable costuma ficar.
        private static string[] BasesPadrao()
        {
            var b = new List<string>();
            b.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            b.Add(Environment.GetEnvironmentVariable("ProgramFiles"));
            b.Add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
            b.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            b.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            b.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            b.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            b.Add(AppDomain.CurrentDomain.BaseDirectory);   // ao lado do próprio exe
            b.Add("C:\\");
            b.Add("D:\\");
            return b.ToArray();
        }

        // Varre cada base (1 nível): pasta com "cent" no nome contendo chrome.exe
        // (direto ou em Application\). Público para o teste offline conseguir exercitar.
        public static string ProcurarCent(string[] bases)
        {
            foreach (string raiz in bases)
            {
                if (raiz == null || raiz.Length == 0) continue;
                try
                {
                    if (!Directory.Exists(raiz)) continue;
                    foreach (string dir in Directory.GetDirectories(raiz))
                    {
                        string nome = Path.GetFileName(dir);
                        if (nome == null || nome.IndexOf("cent", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string direto = Path.Combine(dir, "chrome.exe");
                        if (File.Exists(direto)) return direto;
                        string app = Path.Combine(dir, "Application", "chrome.exe");
                        if (File.Exists(app)) return app;
                    }
                }
                catch { }   // sem acesso à pasta: segue para a próxima
            }
            return null;
        }

        private static string AcharEdge()
        {
            string[] cands = new string[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") + "\\Microsoft\\Edge\\Application\\msedge.exe",
                Environment.GetEnvironmentVariable("ProgramFiles") + "\\Microsoft\\Edge\\Application\\msedge.exe",
            };
            foreach (string c in cands)
                try { if (File.Exists(c)) return c; } catch { }
            return null;
        }
    }
}
