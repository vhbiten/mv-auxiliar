using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Serviço de impressora encontrado: URL + token (ambos necessários para imprimir).
    public class LasImpressora
    {
        public string Url { get; set; }
        public string Token { get; set; }
    }

    // Cliente do agente local de impressão (LAS) — 127.0.0.1 na máquina da farmácia.
    // Autenticação (descoberta no JS do LAS + HAR): toda requisição leva
    //   Authentication: TOKEN <uuid>  e  Origin: <MV_HOST>
    // O UUID do AGENTE é gerado pelo próprio cliente (Guid v4); a resposta do módulo
    // devolve o TOKEN da IMPRESSORA, usado no POST de impressão (porta diferente).
    public static class LasClient
    {
        public const int PortStart = 32768;
        public const int PortEnd = 32820;
        private const string UpdateVersion = "las-update-installer-2.2.6";
        private const string PrinterVersion = "las-printer-installer-2.1.6";
        private static readonly string[] PrinterModules =
            { "las-printer-installer-2.1.6", "las-printer-installer-2.1.9", "las-printer-installer" };

        private static readonly string LasOrigin = SoulmvKit.AppConfig.Host;
        private static readonly string LasReferer = SoulmvKit.AppConfig.Host + "/soul-product-workspace/";

        // UUID do cliente (token do agente). Guid v4 do .NET combina com o formato que o LAS exige.
        private static readonly string ClientUuid = Guid.NewGuid().ToString();

        private static HttpClient NovoHttp(int timeoutMs, string token)
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", LasOrigin);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", LasReferer);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authentication", "TOKEN " + token);
            return http;
        }

        private class ProbeResult { public LasImpressora Impressora; public bool Agente401; }

        // --- Diagnóstico da última descoberta (para mensagem precisa e log de TI) ---
        private static readonly object _diagLock = new object();
        private static bool _agenteVisto;
        private static string _agenteBase;
        private static readonly System.Collections.Generic.Dictionary<string, string> _statusModulos =
            new System.Collections.Generic.Dictionary<string, string>();
        private static void DiagReset() { lock (_diagLock) { _agenteVisto = false; _agenteBase = null; _statusModulos.Clear(); } }
        private static void DiagAgente(string baseAgent) { lock (_diagLock) { _agenteVisto = true; _agenteBase = baseAgent; } }
        private static void DiagModulo(string mod, string status) { lock (_diagLock) { _statusModulos[mod] = status; } }
        public static bool AgenteFoiVisto { get { lock (_diagLock) { return _agenteVisto; } } }
        public static string ResumoModulos()
        {
            lock (_diagLock)
            {
                if (_statusModulos.Count == 0) return "nenhum módulo listado";
                var partes = new System.Collections.Generic.List<string>();
                foreach (var kv in _statusModulos) partes.Add(kv.Key.Replace("las-printer-installer", "impressora") + "=" + kv.Value);
                return string.Join(", ", partes.ToArray());
            }
        }

        // Mensagem amigável explicando por que a impressora não foi localizada.
        public static string ExplicarFalha()
        {
            if (AgenteFoiVisto)
                return "O agente de impressão do MV (LAS) está aberto neste computador, mas o módulo da impressora está parado ("
                    + ResumoModulos() + ") e a reativação automática não deu certo. "
                    + "Abra o MV Soul no navegador e imprima uma etiqueta qualquer (isso religa o módulo) ou reinicie o computador.";
            return "Nenhum agente de impressão do MV (LAS) respondeu neste computador. "
                + "Verifique se o agente LAS está instalado e aberto e se a impressora Zebra está ligada.";
        }

        // Descobre o serviço de impressora (URL + token), ou null se não achar.
        // Se o agente recusar o UUID (401), REGISTRA o UUID via protocolo mvupdate:connect
        // (igual ao navegador) e tenta de novo. Se o agente responder mas o módulo de
        // impressora estiver PARADO (aconteceu na farmácia: update-installer OK e
        // las-printer STOPED), tenta REATIVÁ-LO uma vez (REST + lançar o executável).
        public static async Task<LasImpressora> DescobrirImpressoraAsync()
        {
            DiagReset();
            var res = await ProbeAsync();
            if (res.Impressora != null) return res.Impressora;

            // não achou (provável 401): registra o UUID no agente e tenta algumas vezes
            RegistrarUuid();
            bool tentouReativar = false;
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1300);
                res = await ProbeAsync();
                if (res.Impressora != null) return res.Impressora;
                if (!tentouReativar && i >= 1 && AgenteFoiVisto)
                {
                    tentouReativar = true;               // uma única tentativa de reativação
                    await TentarReativarModuloAsync();
                }
            }
            Logger.Log("LAS: nenhum módulo de impressora resolvido. Agente visto=" + AgenteFoiVisto + "; módulos: " + ResumoModulos());
            LogProcessosLas();
            return null;
        }

        // Tenta religar o módulo de impressora: (1) pedido REST ao agente de update;
        // (2) lança o executável do módulo, achado a partir do comando do protocolo
        // mvupdate: no registro (mesma instalação do agente). Tudo melhor-esforço, com log.
        private static async Task TentarReativarModuloAsync()
        {
            string agente; lock (_diagLock) { agente = _agenteBase; }

            // (1) pedir ao agente para iniciar o módulo (endpoint provável; inofensivo se não existir)
            if (agente != null)
                using (var http = NovoHttp(4000, ClientUuid))
                    foreach (var mod in PrinterModules)
                    {
                        try
                        {
                            var resp = await http.PostAsync(agente + "/modules/" + mod + "/start?version=" + UpdateVersion, new StringContent(""));
                            Logger.Log("LAS reativar: POST /modules/" + mod + "/start -> HTTP " + (int)resp.StatusCode);
                            if ((int)resp.StatusCode < 300) { await Task.Delay(3000); return; }
                        }
                        catch (Exception ex) { Logger.Log("LAS reativar: start " + mod + ": " + ex.Message); }
                    }

            // (2) lançar o executável do módulo de impressora da mesma instalação do agente
            try
            {
                string cmd = null;
                using (var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("mvupdate\\shell\\open\\command"))
                    if (k != null) cmd = k.GetValue(null) as string;
                Logger.Log("LAS reativar: comando do protocolo mvupdate = " + (cmd == null ? "(não registrado)" : cmd));
                string exe = ExtrairExe(cmd);
                if (exe != null && System.IO.File.Exists(exe))
                {
                    var candidatos = new System.Collections.Generic.List<string>();
                    string dir = System.IO.Path.GetDirectoryName(exe);
                    BuscarPrinterExe(dir, candidatos, 0);
                    string pai = dir != null ? System.IO.Path.GetDirectoryName(dir) : null;
                    if (pai != null && candidatos.Count == 0) BuscarPrinterExe(pai, candidatos, 0);
                    foreach (var c in candidatos) Logger.Log("LAS reativar: candidato a módulo de impressora -> " + c);
                    if (candidatos.Count > 0)
                    {
                        var psi = new ProcessStartInfo(candidatos[0]);
                        psi.UseShellExecute = true;
                        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(candidatos[0]);
                        Process.Start(psi);
                        Logger.Log("LAS reativar: lancei " + candidatos[0] + " — aguardando o módulo subir…");
                        await Task.Delay(4000);
                    }
                }
            }
            catch (Exception ex) { Logger.Log("LAS reativar (exe): " + ex.Message); }
        }

        // Primeiro caminho de executável dentro de um comando de protocolo ("C:\x\y.exe" "%1").
        private static string ExtrairExe(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            var m = Regex.Match(cmd, "^\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(cmd, "^\\s*([^\\s]+\\.exe)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // Procura executáveis com cara de módulo de impressora LAS (nome contém "printer")
        // até 3 níveis abaixo — o suficiente para instalações padrão do agente.
        private static void BuscarPrinterExe(string dir, System.Collections.Generic.List<string> achados, int nivel)
        {
            if (dir == null || nivel > 3 || achados.Count >= 3) return;
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(dir, "*.exe"))
                {
                    string nome = System.IO.Path.GetFileName(f);
                    if (nome.IndexOf("printer", StringComparison.OrdinalIgnoreCase) >= 0) achados.Add(f);
                }
                foreach (var d in System.IO.Directory.GetDirectories(dir))
                    BuscarPrinterExe(d, achados, nivel + 1);
            }
            catch { }
        }

        // Log de TI: processos em execução com cara de LAS (ajuda a ver se o módulo caiu).
        private static void LogProcessosLas()
        {
            try
            {
                var nomes = new System.Collections.Generic.List<string>();
                foreach (var p in Process.GetProcesses())
                {
                    string nm = p.ProcessName ?? "";
                    if (nm.IndexOf("las", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("mvupdate", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("printer", StringComparison.OrdinalIgnoreCase) >= 0)
                        nomes.Add(nm);
                }
                Logger.Log("LAS diagnóstico: processos relacionados em execução = "
                    + (nomes.Count == 0 ? "(nenhum)" : string.Join(", ", nomes.ToArray())));
            }
            catch (Exception ex) { Logger.Log("LAS diagnóstico de processos: " + ex.Message); }
        }

        // Lança o protocolo do Windows mvupdate:connect?<base64({id,version})> que registra
        // o UUID no agente LAS (mesmo handshake que o navegador faz).
        private static void RegistrarUuid()
        {
            try
            {
                string json = "{\"id\":\"" + ClientUuid + "\",\"version\":\"1.0.0\"}";
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
                string uri = "mvupdate:connect?" + b64;
                Logger.Log("LAS: registrando UUID no agente via protocolo -> " + uri);
                var psi = new ProcessStartInfo(uri);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Logger.Log("LAS: falha ao abrir o protocolo mvupdate: " + ex.Message); }
        }

        // Sonda TODAS as portas EM PARALELO (porta morta dá timeout; em série levava ~45s/rodada).
        private static async Task<ProbeResult> ProbeAsync()
        {
            using (var http = NovoHttp(1500, ClientUuid))
            {
                var tasks = new System.Collections.Generic.List<Task<ProbeResult>>();
                for (int port = PortStart; port <= PortEnd; port++)
                {
                    int p = port;
                    tasks.Add(ProbePortAsync(http, p));
                }
                var rs = await Task.WhenAll(tasks);
                var agg = new ProbeResult();
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    if (r.Impressora != null) return r;
                    if (r.Agente401) agg.Agente401 = true;
                }
                if (agg.Agente401) Logger.Log("LAS: agente respondeu 401 (UUID não registrado?). uuid=" + ClientUuid);
                return agg;
            }
        }

        private static async Task<ProbeResult> ProbePortAsync(HttpClient http, int port)
        {
            string baseAgent = "http://127.0.0.1:" + port;
            int status; string root;
            try
            {
                var r = await http.GetAsync(baseAgent + "/?version=" + UpdateVersion);
                status = (int)r.StatusCode;
                if (status >= 500) return null;
                root = await r.Content.ReadAsStringAsync();
            }
            catch { return null; }

            Logger.Log("LAS sonda porta " + port + " -> HTTP " + status + " " + Curto(root));
            if (status == 401) return new ProbeResult { Agente401 = true };
            if (status == 200) DiagAgente(baseAgent);

            var imp = await ResolverImpressoraAsync(http, baseAgent);
            if (imp != null)
            {
                Logger.Log("LAS: impressora em " + imp.Url + " (token " + Curto(imp.Token) + ")");
                return new ProbeResult { Impressora = imp };
            }
            return null;
        }

        private static async Task<LasImpressora> ResolverImpressoraAsync(HttpClient http, string baseAgent)
        {
            foreach (var mod in PrinterModules)
            {
                try
                {
                    var r = await http.GetAsync(baseAgent + "/modules/" + mod + "?version=" + UpdateVersion);
                    if (!r.IsSuccessStatusCode) { Logger.Log("LAS módulo " + mod + " -> HTTP " + (int)r.StatusCode); continue; }
                    string body = await r.Content.ReadAsStringAsync();
                    Logger.Log("LAS módulo " + mod + " -> " + Curto(body));
                    var st = Regex.Match(body, "\"status\"\\s*:\\s*\"([^\"]+)\"");
                    DiagModulo(mod, st.Success ? st.Groups[1].Value : "sem status");
                    var href = Regex.Match(body, "\"href\"\\s*:\\s*\"([^\"]+)\"");
                    var tok = Regex.Match(body, "\"token\"\\s*:\\s*\"([^\"]+)\"");
                    if (href.Success && tok.Success)
                    {
                        if (body.IndexOf("STARTED", StringComparison.OrdinalIgnoreCase) < 0)
                            Logger.Log("LAS aviso: módulo " + mod + " não está STARTED.");
                        var imp = new LasImpressora();
                        imp.Url = href.Groups[1].Value.TrimEnd('/');
                        imp.Token = tok.Groups[1].Value;
                        return imp;
                    }
                    Logger.Log("LAS: módulo " + mod + " sem href/token (href=" + href.Success + " token=" + tok.Success + ")");
                }
                catch (Exception ex) { Logger.Log("LAS módulo " + mod + " erro: " + ex.Message); }
            }
            return null;
        }

        // Envia o ZPL à impressora (multipart: data=<ZPL>, printer={"id":"LPTx"}; auth = token da impressora).
        public static async Task EnviarZplAsync(LasImpressora imp, string zpl, string printerId, int copies)
        {
            if (imp == null || string.IsNullOrEmpty(imp.Url)) throw new Exception("Serviço de impressora (LAS) não localizado.");
            using (var http = NovoHttp(20000, imp.Token))
            {
                var form = new MultipartFormDataContent();
                var data = new ByteArrayContent(Encoding.UTF8.GetBytes(zpl ?? ""));
                data.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(data, "data", "blob");
                if (!string.IsNullOrEmpty(printerId) && !printerId.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    form.Add(new StringContent("{\"id\":\"" + printerId + "\"}"), "printer");

                string url = imp.Url + "/prints/postscript?copies=" + copies + "&version=" + PrinterVersion;
                Logger.Log("LAS print POST " + url + " (printer=" + printerId + ", zpl=" + (zpl != null ? zpl.Length : 0) + " chars)");
                var resp = await http.PostAsync(url, form);
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                Logger.Log("LAS print resp HTTP " + (int)resp.StatusCode + " body=" + Curto(body));
                if ((int)resp.StatusCode >= 300)
                    throw new Exception("Agente LAS respondeu HTTP " + (int)resp.StatusCode +
                        (body.Length > 0 ? " (" + Curto(body) + ")" : "") + ".");
            }
        }

        // Testa a impressora SEM produzir kit: resolve o serviço e imprime uma etiqueta de teste.
        public static async Task<string> ImprimirTesteAsync()
        {
            Logger.Log("=== TESTE DE IMPRESSORA (LAS) === uuid=" + ClientUuid);
            var imp = await DescobrirImpressoraAsync();
            if (imp == null)
                return ExplicarFalha();
            string zpl = "^XA\n^FO30,30^ADN,36,20^FDTESTE LAS - AUXILIAR MV^FS\n^FO30,90^ADN,28,15^FDImpressora OK^FS\n^XZ";
            await EnviarZplAsync(imp, zpl, "LPT2", 1);
            return "Etiqueta de TESTE enviada à impressora (" + imp.Url + ").";
        }

        private static string Curto(string s)
        {
            if (s == null) return "(null)";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 240 ? s.Substring(0, 240) + "..." : s;
        }
    }
}
