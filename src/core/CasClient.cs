using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    public class CasResult
    {
        public bool Success;
        public string Message;
        public Session Session;
    }

    // Login no CAS (mvautenticador) do SoulMV.
    //   1. GET /soul-mv/  -> o app redireciona ao CAS com o service exato (inclui :80)
    //   2. POST credenciais -> CAS emite ticket -> soul-mv valida -> sessão + cookie SSO (CASTGC)
    //   3. Bootstrap do contexto (soul-integrated-services) -> empresa/usuário
    //   4. GET /soul-product-forms/services/status -> SSO emite ticket -> JSESSIONID do forms
    // Usa redirect automático do HttpClient (preserva a codificação do parâmetro service).
    public static class CasClient
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

        public static async Task<CasResult> LoginAsync(string host, string user, string password, int company)
        {
            var res = new CasResult();
            try
            {
                // Permite mais conexões simultâneas ao servidor (padrão do .NET é só 2),
                // liberando as consultas de solicitações em paralelo.
                System.Net.ServicePointManager.DefaultConnectionLimit = 20;

                var handler = new HttpClientHandler
                {
                    CookieContainer = new CookieContainer(),
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 20,
                    UseCookies = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                var http = new HttpClient(handler);
                http.Timeout = TimeSpan.FromSeconds(30);
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9");

                var baseUri = new Uri(host);
                string casLogin = host + "/mvautenticador-cas/login";

                // 1) Abrir soul-mv -> cai no formulário do CAS (com o service correto)
                Logger.Log("CAS: abrindo soul-mv");
                var getResp = await http.GetAsync(host + "/soul-mv/");
                string html = await getResp.Content.ReadAsStringAsync();
                Uri postUri = getResp.RequestMessage.RequestUri;
                if (postUri.AbsoluteUri.IndexOf("/mvautenticador-cas/login", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Já estava logado? Segue para o bootstrap.
                    Logger.Log("CAS: sem formulário (sessão já ativa?) url=" + Sanitize(postUri.ToString()));
                }
                else
                {
                    // 2) POST das credenciais para a URL do CAS (com service correto)
                    var fields = ParseHiddenInputs(html);
                    fields["username"] = user;
                    fields["password"] = password;
                    fields["company"] = company.ToString();
                    fields["type"] = "AUTHENTICATION_SGU";
                    fields["timezone"] = GmtOffset();
                    if (!fields.ContainsKey("_eventId")) fields["_eventId"] = "submit";
                    if (!fields.ContainsKey("not-an-username")) fields["not-an-username"] = "";
                    fields["submit"] = "Login";

                    var postReq = new HttpRequestMessage(HttpMethod.Post, postUri);
                    postReq.Content = new FormUrlEncodedContent(fields);
                    postReq.Headers.TryAddWithoutValidation("Origin", host);
                    postReq.Headers.Referrer = postUri;
                    Logger.Log("CAS: enviando credenciais (user=" + user + ", empresa=" + company + ")");
                    var postResp = await http.SendAsync(postReq);
                    Uri finalUri = postResp.RequestMessage.RequestUri;

                    if (finalUri.AbsoluteUri.IndexOf("/mvautenticador-cas/login", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // Continuou no CAS = credenciais recusadas
                        res.Message = ExtractCasError(await postResp.Content.ReadAsStringAsync());
                        Logger.Log("CAS: login recusado - " + res.Message);
                        return res;
                    }
                    Logger.Log("CAS: sessão soul-mv ok (url=" + Sanitize(finalUri.ToString()) + ")");
                }

                // 3) Bootstrap do contexto (empresa/usuário) via integrated-services
                await CarregarContextoAsync(http, host, company);

                // 4) Sessão do módulo de forms. O SSO do 2º serviço NÃO vem por 302: o CAS
                //    devolve uma página HTML "mv sso" com <a href="...?ticket=ST-...">. Seguimos esse link.
                var stResp = await http.GetAsync(host + "/soul-product-forms/services/status?ts=" + NowMs());
                stResp = await FollowSso(http, stResp, 4);

                var formsCookies = handler.CookieContainer.GetCookies(
                    new Uri(host + "/soul-product-forms/services/message/message"));
                bool temSessao = false;
                string ck = "";
                foreach (Cookie c in formsCookies) { ck += c.Name + " "; if (c.Name == "JSESSIONID") temSessao = true; }
                Logger.Log("Cookies -> forms: " + ck);

                if (!temSessao)
                {
                    res.Message = "Login OK, mas não obtive a sessão do módulo de formulários (sem JSESSIONID).";
                    return res;
                }

                res.Success = true;
                res.Session = new Session(http, host, user);
                res.Session.Cookies = handler.CookieContainer;
                res.Session.Empresa = company;
                res.Session.NomeCompleto = await ObterNomeCompleto(http, host, user);   // best-effort
                res.Message = "Conectado.";
                Logger.Log("CAS: login completo para " + user + (res.Session.NomeCompleto != null ? " (" + res.Session.NomeCompleto + ")" : ""));
                return res;
            }
            catch (Exception ex)
            {
                Logger.Log("CAS: erro - " + ex.Message);
                res.Message = "Falha de conexão: " + ex.Message;
                return res;
            }
        }

        // Passo 3 do login: carrega o contexto de empresa/usuário/menus no integrated-services.
        // É ele que o forms consulta para saber a empresa e os papéis do usuário.
        private static async Task CarregarContextoAsync(HttpClient http, string host, int company)
        {
            string isvc = host + "/soul-integrated-services";
            await SafeGet(http, isvc + "/config");
            await SafeGet(http, isvc + "/api/company/list");
            await SafeGet(http, isvc + "/api/contextInfo");
            await SafeGet(http, isvc + "/api/applications");
            await SafeGet(http, isvc + "/api/findSnSbisByCompany/" + company);
            await SafeGet(http, isvc + "/api/menus");
            Logger.Log("integrated-services: contexto carregado");
        }

        // Reconecta SEM senha uma sessão que caiu por inatividade, refazendo os passos 1, 3 e
        // 4 do login pelo SSO: enquanto o login único do CAS (cookie CASTGC) estiver vivo,
        // cada serviço recebe um ticket novo no mesmo HttpClient.
        //
        // Por que os três (teste ao vivo de 18/10/2026, sessão parada 80 min): o forms
        // respondia "Não existe Empresa cadastrada ou Usuário não esta cadastrado nesta
        // empresa" — o contexto de empresa tinha caído; renovando só o forms (passo 4) a
        // resposta virava "Acesso negado: o usuário não tem permissão para acessar a tela"
        // — faltavam os papéis, que vêm do contexto (passo 3).
        // LIMITE (2º teste ao vivo, 18/10/2026, 70 min parada): o login único do CAS TAMBÉM
        // vence por inatividade no mesmo prazo (~60–70 min) — o /soul-mv/ cai no formulário
        // de login. Então isto só recupera quedas em que o CAS segue vivo (ex.: o forms
        // reiniciou no servidor); a queda por inatividade quem evita é o ManterSessao. O PEP
        // reabrir por SSO horas depois do login só acontecia com o usuário em atividade.
        // Devolve false se o CAS também venceu (cai no formulário de login) — só com a senha.
        public static async Task<bool> RenovarSessaoAsync(Session s)
        {
            if (s == null) return false;
            try
            {
                // 1) soul-mv: com o CASTGC vivo o CAS devolve direto o ticket (sem formulário)
                var r1 = await s.Http.GetAsync(s.Host + "/soul-mv/");
                string d1 = r1.RequestMessage != null && r1.RequestMessage.RequestUri != null ? r1.RequestMessage.RequestUri.AbsoluteUri : "";
                if (d1.IndexOf("/mvautenticador-cas/login", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Log("Sessão: login único do MV também venceu — reconexão só com a senha.");
                    return false;
                }

                // 3) contexto de empresa/usuário
                await CarregarContextoAsync(s.Http, s.Host, s.Empresa);

                // 4) forms
                var r4 = await s.Http.GetAsync(s.Host + "/soul-product-forms/services/status?ts=" + NowMs());
                r4 = await FollowSso(s.Http, r4, 4);
                string d4 = r4.RequestMessage != null && r4.RequestMessage.RequestUri != null ? r4.RequestMessage.RequestUri.AbsoluteUri : "";
                bool ok = (int)r4.StatusCode == 200 &&
                          d4.IndexOf("/mvautenticador-cas/login", StringComparison.OrdinalIgnoreCase) < 0;
                Logger.Log("Sessão: reconexão sem senha " + (ok ? "OK" : "FALHOU") +
                    " (forms HTTP " + (int)r4.StatusCode + ", " + Sanitize(d4) + ").");
                if (ok) s.UltimaRenovacao = DateTime.Now;
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Log("Sessão: reconexão sem senha falhou: " + ex.Message);
                return false;
            }
        }

        // Sessão refeita a partir de cookies guardados (nome, valor, caminho, domínio) — sem senha
        // e sem login novo: fala com a MESMA sessão do servidor, se ela ainda existir. Usada para
        // liberar a reserva de uma solicitação depois que o programa caiu (ReservaAtendimento).
        // Sessão que já morreu cai no login do CAS: o MorphisClient lança SessaoExpiradaException.
        public static Session SessaoSalva(string host, string user, System.Collections.Generic.IEnumerable<string[]> cookies)
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 20,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            foreach (var c in cookies)
                if (c != null && c.Length >= 4) handler.CookieContainer.Add(new Cookie(c[0], c[1], c[2], c[3]));
            var http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "pt-BR,pt;q=0.9");
            var s = new Session(http, host, user);
            s.Cookies = handler.CookieContainer;
            return s;
        }

        // Nome completo do usuário: contextInfo do integrated-services (exige o header
        // x-profile-user). Best-effort — devolve null se não conseguir.
        private static async Task<string> ObterNomeCompleto(HttpClient http, string host, string user)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, host + "/soul-integrated-services/api/contextInfo");
                req.Headers.TryAddWithoutValidation("x-profile-user", user);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                var r = await http.SendAsync(req);
                string body = await r.Content.ReadAsStringAsync();
                var m = Regex.Match(body, "\"userFullName\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success)
                {
                    string nome = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                    return nome.Length > 0 ? nome : null;
                }
            }
            catch (Exception ex) { Logger.Log("Nome do usuário: " + ex.Message); }
            return null;
        }

        private static async Task SafeGet(HttpClient http, string url)
        {
            try { await http.GetAsync(url); }
            catch (Exception ex) { Logger.Log("  bootstrap falhou em " + url + ": " + ex.Message); }
        }

        // Segue a página interstitial de SSO ("mv sso"): extrai o href com ticket=ST e o segue.
        private static async Task<HttpResponseMessage> FollowSso(HttpClient http, HttpResponseMessage resp, int max)
        {
            for (int i = 0; i < max; i++)
            {
                string body = await resp.Content.ReadAsStringAsync();
                Match m = Regex.Match(body, "href=\"([^\"]*[?&]ticket=ST-[^\"]*)\"", RegexOptions.IgnoreCase);
                if (!m.Success) return resp;
                string url = WebUtility.HtmlDecode(m.Groups[1].Value);
                Logger.Log("SSO: seguindo ticket da página interstitial");
                resp = await http.GetAsync(url);
            }
            return resp;
        }

        private static Dictionary<string, string> ParseHiddenInputs(string html)
        {
            var d = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(html, "<input[^>]*>", RegexOptions.IgnoreCase))
            {
                string tag = m.Value;
                Match nm = Regex.Match(tag, "name=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (!nm.Success) continue;
                Match vm = Regex.Match(tag, "value=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                string val = vm.Success ? WebUtility.HtmlDecode(vm.Groups[1].Value) : "";
                d[nm.Groups[1].Value] = val;
            }
            return d;
        }

        private static string ExtractCasError(string html)
        {
            Match m = Regex.Match(html, "danger[^>]*>\\s*(?:<[^>]+>\\s*)*([^<]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string t = m.Groups[1].Value.Trim();
                if (t.Length > 1) return t;
            }
            return "Usuário ou senha inválidos.";
        }

        private static string GmtOffset()
        {
            TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            string sign = off < TimeSpan.Zero ? "-" : "+";
            return "GMT" + sign + Math.Abs(off.Hours).ToString("00") + Math.Abs(off.Minutes).ToString("00");
        }

        private static string NowMs()
        {
            return ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds).ToString();
        }

        private static string Sanitize(string url)
        {
            return Regex.Replace(url, "ticket=ST-[^&]+", "ticket=ST-***");
        }
    }
}
