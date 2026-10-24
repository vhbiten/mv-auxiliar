using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Resultado de uma varredura de antimicrobianos.
    public class VarreduraAntimicrobianos
    {
        public readonly List<ItemAntimicrobiano> Itens = new List<ItemAntimicrobiano>();
        public int Prescricoes;            // prescrições distintas com antimicrobiano
        public DateTime Desde, Ate;        // período coberto pelo feed do PEP
        public bool CoberturaCurta;        // o feed não alcançou 24h: pode faltar prescrição
        public string Aviso;

        // Cobertura da varredura por paciente — é o que diz se a lista está completa.
        public int AtendimentosConsultados;
        public int AtendimentosSemPaciente;   // o PEP não achou o atendimento (alta antiga, etc.)
        public int PacientesComAntimicrobiano;
        public int LinhasRepetidas;           // colapsadas pela deduplicação

        // Quem monta a lista de atendimentos TEM que marcar isto quando a varredura de
        // solicitações não veio inteira (Itens.Count < TotalServidor). Sem esse aviso a tela
        // mostraria uma previsão a menos com cara de completa — já vi acontecer: uma passada
        // trouxe 50 de 204 solicitações e a lista caiu de 19 para 17 pacientes em silêncio.
        public bool ListaDePacientesIncompleta;
    }

    // Um paciente a consultar no PEP. O atendimento (CD_ATENDIMENTO do ERP) é a mesma
    // chave que o PEP chama de hospitalAdmission — é por ele que se chega ao paciente.
    public class PacienteAlvo
    {
        public string Atendimento;
        public string Nome;      // o que a solicitação já trouxe (evita reperguntar ao PEP)
        public string Setor;
        public int PacienteId;   // resolvido aqui
    }

    // Cliente do PEP (/mvpep) — o outro mundo do MV, separado do soul-product-forms.
    // Fala REST/JSON e SÓ LÊ.
    //
    // Como se chega nos antimicrobianos do hospital inteiro (descoberto na marra, 19/08/2026):
    //   1. prescript-medicines sem filtro devolve o feed GLOBAL dos últimos ~1,5 dia;
    //      quem é antimicrobiano tem schemaItem.id = "MDA" e traz o cdPreMed da prescrição.
    //   2. prescriptions/{cdPreMed}/items devolve o item completo — e é SÓ ali que vem o
    //      administrationDays ("D5/7") e a quantidade por horário.
    // Não dependemos da lista de pacientes do PEP de propósito: ela é a lista pessoal do
    // prestador (encolheu de 32 para 4 no meio da manhã) e ignora o parâmetro de setor.
    // Como cada dia de internação gera uma prescrição nova, uma janela de 24h já pega
    // todo mundo que está em uso.
    public static class PepClient
    {
        private const string Pep = "/mvpep";
        private const int TamanhoFeed = 2000;     // teto do servidor: pedir mais devolve o mesmo
        private const int DiasDeFeed = 2;         // quanto passado varrer (uma prescrição pode ser de ontem)
        private const int MaxPaginas = 8;         // trava: 8 x 2000 itens já é muito mais que 2 dias
        private const int Paralelo = 6;

        private static Session _sessao;           // sessão do ERP que já abriu o PEP

        // Abre a sessão do PEP reaproveitando o login do CAS que a sessão já tem.
        // Três detalhes que custaram caro: o service precisa do ":80"; applications/current
        // tem que vir antes; e o hostname tem que ser uma estação cadastrada no MV.
        // Uma abertura por vez: a aba Urgência (PAM), o clique numa prescrição e a nova tentativa
        // podiam disparar o login do CAS ao mesmo tempo no mesmo HttpClient. Quem chega com uma
        // abertura em curso para a MESMA sessão espera o resultado dela.
        private static readonly object _travaEntrar = new object();
        private static Task<bool> _entrando;
        private static Session _entrandoCom;

        public static Task<bool> EntrarAsync(Session s)
        {
            if (s == null) return Task.FromResult(false);
            lock (_travaEntrar)
            {
                if (_entrando != null && !_entrando.IsCompleted && object.ReferenceEquals(_entrandoCom, s)) return _entrando;
                _entrandoCom = s;
                _entrando = EntrarAgoraAsync(s);
                return _entrando;
            }
        }

        private static async Task<bool> EntrarAgoraAsync(Session s)
        {
            if (object.ReferenceEquals(_sessao, s) && await SessaoValidaAsync(s)) return true;

            try
            {
                long ms = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
                string service = s.Host.TrimEnd('/') + ":80" + Pep + "/?t=" + ms;
                string url = s.Host + AppConfig.CasApp + "/login?service=" + Uri.EscapeDataString(service);

                var r = await s.Http.GetAsync(url);
                string corpo = await r.Content.ReadAsStringAsync();
                // O SSO do 2º serviço pode vir por página interstitial em vez de 302.
                for (int i = 0; i < 4; i++)
                {
                    var m = Regex.Match(corpo, "href=\"([^\"]*[?&]ticket=ST-[^\"]*)\"", RegexOptions.IgnoreCase);
                    if (!m.Success) break;
                    r = await s.Http.GetAsync(WebUtility.HtmlDecode(m.Groups[1].Value));
                    corpo = await r.Content.ReadAsStringAsync();
                }

                await GetAsync(s, Pep + "/api/applications/current");
                if (!await SessaoValidaAsync(s))
                {
                    Logger.Log("PEP: sessão não abriu para a estação \"" + AppConfig.Estacao + "\". "
                        + "Se o MV não conhece essa máquina, informe a estação no config.txt (estacao=...).");
                    return false;
                }
                _sessao = s;
                Logger.Log("PEP: sessão aberta (estação " + AppConfig.Estacao + ").");
                return true;
            }
            catch (Exception ex) { Logger.Log("PEP: falha ao abrir sessão: " + ex.Message); return false; }
        }

        // O PEP já foi aberto com esta sessão? (o keepalive só mantém o PEP de quem já usou)
        public static bool Aberto(Session s)
        {
            return s != null && object.ReferenceEquals(_sessao, s);
        }

        private static async Task<bool> SessaoValidaAsync(Session s)
        {
            string j = await GetAsync(s, Pep + "/api/session?hostname=" + Uri.EscapeDataString(AppConfig.Estacao));
            return j != null && j.IndexOf("\"provider\"", StringComparison.Ordinal) >= 0
                             && j.IndexOf("ACESSOPRD", StringComparison.Ordinal) < 0;
        }

        // ===== VARREDURA POR PACIENTE (é esta que vale) =====
        //
        // O feed global (ListarPeloFeedAsync, abaixo) enxerga o hospital inteiro, mas não diz
        // de quem é cada prescrição — e como a janela de 48h contém a prescrição de ontem E a
        // de hoje do mesmo paciente, o mesmo tratamento entrava duas vezes na conta.
        //
        // Aqui a entrada é o ATENDIMENTO, que a aba Solicitações já traz do hospital inteiro:
        //   atendimento -> prescriptions?hospitalAdmission= -> paciente
        //   paciente    -> prescript-medicines?patient=     -> os itens VIGENTES (só eles)
        //   cdPreMed    -> prescriptions/{cdPreMed}/items    -> o "D5/7" de cada item
        // O item tem o MESMO id nos dois endpoints, então o casamento é exato: nada de
        // adivinhar qual linha da prescrição corresponde a qual item vigente.
        public static async Task<VarreduraAntimicrobianos> ListarPorPacienteAsync(
            Session s, List<PacienteAlvo> alvos)
        {
            var res = new VarreduraAntimicrobianos();
            if (alvos == null || alvos.Count == 0) { res.Aviso = "Sem atendimentos para consultar."; return res; }
            if (!await EntrarAsync(s))
            {
                res.Aviso = "Não foi possível abrir a sessão do PEP. Confira a estação no config.txt.";
                return res;
            }

            // 1) atendimento -> paciente (com cache: a relação nunca muda)
            var pendentes = new List<PacienteAlvo>();
            foreach (var a in alvos)
            {
                if (string.IsNullOrEmpty(a.Atendimento)) continue;
                int pid;
                if (_pacientePorAtendimento.TryGetValue(a.Atendimento, out pid)) a.PacienteId = pid;
                else pendentes.Add(a);
            }
            for (int i = 0; i < pendentes.Count; i += Paralelo)
            {
                var lote = new List<Task>();
                for (int k = i; k < pendentes.Count && k < i + Paralelo; k++)
                {
                    var alvo = pendentes[k];
                    lote.Add(ResolverPacienteAsync(s, alvo));
                }
                await Task.WhenAll(lote);
            }

            var comPaciente = new List<PacienteAlvo>();
            foreach (var a in alvos)
            {
                if (string.IsNullOrEmpty(a.Atendimento)) continue;
                res.AtendimentosConsultados++;
                if (a.PacienteId > 0) comPaciente.Add(a); else res.AtendimentosSemPaciente++;
            }

            // 2) paciente -> itens VIGENTES; guarda quem é o dono de cada prescrição
            var itensVigentes = new Dictionary<int, PacienteAlvo>();     // itemId -> paciente
            var prescricoes = new Dictionary<int, PacienteAlvo>();       // cdPreMed -> paciente
            for (int i = 0; i < comPaciente.Count; i += Paralelo)
            {
                var lote = new List<Task<KeyValuePair<PacienteAlvo, string>>>();
                for (int k = i; k < comPaciente.Count && k < i + Paralelo; k++)
                {
                    var alvo = comPaciente[k];
                    lote.Add(GetAsync(s, Pep + "/api/prescript-medicines?page=0&size=200&patient=" + alvo.PacienteId)
                        .ContinueWith(t => new KeyValuePair<PacienteAlvo, string>(alvo, t.Result)));
                }
                foreach (var par in await Task.WhenAll(lote))
                {
                    if (string.IsNullOrEmpty(par.Value)) continue;
                    bool tem = false;
                    foreach (Match m in Regex.Matches(par.Value,
                        "\\{\"id\":(\\d+),\"schemaItem\":\\{\"id\":\"MDA\".{0,900}?\"cdPreMed\":(\\d+)", RegexOptions.Singleline))
                    {
                        int itemId = int.Parse(m.Groups[1].Value);
                        int cd = int.Parse(m.Groups[2].Value);
                        // "canceled" vem DEPOIS do cdPreMed, fora do trecho casado: olha adiante.
                        int ini = m.Index + m.Length;
                        string depois = par.Value.Substring(ini, Math.Min(200, par.Value.Length - ini));
                        if (depois.IndexOf("\"canceled\":true", StringComparison.Ordinal) >= 0) continue;
                        itensVigentes[itemId] = par.Key;
                        prescricoes[cd] = par.Key;
                        tem = true;
                    }
                    if (tem) res.PacientesComAntimicrobiano++;
                }
            }
            res.Prescricoes = prescricoes.Count;
            if (prescricoes.Count == 0) return res;

            // 3) detalhe de cada prescrição, ficando SÓ com os itens que estão vigentes
            var brutos = new List<ItemAntimicrobiano>();
            var chaves = new List<int>(prescricoes.Keys);
            for (int i = 0; i < chaves.Count; i += Paralelo)
            {
                var lote = new List<Task<KeyValuePair<int, string>>>();
                for (int k = i; k < chaves.Count && k < i + Paralelo; k++)
                {
                    int cd = chaves[k];
                    lote.Add(GetAsync(s, Pep + "/api/prescriptions/" + cd + "/items")
                        .ContinueWith(t => new KeyValuePair<int, string>(cd, t.Result)));
                }
                foreach (var par in await Task.WhenAll(lote))
                {
                    var doLote = new List<ItemAntimicrobiano>();
                    ExtrairItens(par.Key, par.Value, doLote);
                    foreach (var it in doLote)
                    {
                        PacienteAlvo dono;
                        // Só entra o que o próprio paciente listou como vigente. O resto da
                        // prescrição é histórico e não deve contar.
                        if (!itensVigentes.TryGetValue(it.ItemId, out dono)) continue;
                        it.PacienteId = dono.PacienteId;
                        it.Paciente = dono.Nome;
                        it.Setor = dono.Setor;
                        int at; int.TryParse(dono.Atendimento, out at);
                        it.Atendimento = at;
                        brutos.Add(it);
                    }
                }
            }

            // 4) Rede de segurança: mesma pessoa + mesmo remédio = um tratamento só, fica o de
            // maior "dia" (a prescrição mais recente). A dose avulsa fica FORA da chave, senão
            // a dose de ataque ("AGORA") e a manutenção ("12|12H") do mesmo antibiótico —
            // que são coisas diferentes e somam juntas hoje — se engoliriam.
            // Na prática isso quase nunca dispara: o passo 2 já traz só o que está vigente.
            var porChave = new Dictionary<string, ItemAntimicrobiano>();
            foreach (var it in brutos)
            {
                string chave = it.PacienteId + "|" + it.Produto + "|" + it.Unidade
                             + (it.NaoRecorrente ? "|avulsa" : "");
                ItemAntimicrobiano velho;
                if (porChave.TryGetValue(chave, out velho))
                {
                    res.LinhasRepetidas++;
                    if (it.DiaAtual > velho.DiaAtual) porChave[chave] = it;
                }
                else porChave[chave] = it;
            }
            res.Itens.AddRange(porChave.Values);
            return res;
        }

        // atendimento -> paciente. O PEP entrega o paciente junto de qualquer documento de
        // prescrição da internação; basta pedir um.
        private static readonly Dictionary<string, int> _pacientePorAtendimento =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private static async Task ResolverPacienteAsync(Session s, PacienteAlvo alvo)
        {
            string j = await GetAsync(s, Pep + "/api/prescriptions?page=0&size=1&hospitalAdmission="
                + Uri.EscapeDataString(alvo.Atendimento));
            if (string.IsNullOrEmpty(j)) return;
            var m = Regex.Match(j, "\"patient\":\\{\"id\":(\\d+)");
            if (!m.Success) return;
            alvo.PacienteId = int.Parse(m.Groups[1].Value);
            lock (_pacientePorAtendimento) _pacientePorAtendimento[alvo.Atendimento] = alvo.PacienteId;
        }

        public static void LimparCache() { lock (_pacientePorAtendimento) _pacientePorAtendimento.Clear(); }

        // ===== JUSTIFICATIVAS DE USO DE ANTIMICROBIANO =====
        //
        // São documentos clínicos do template 501 ("JUSTIFICATIVA ANTIMICROBIANO").
        //
        // Do servidor: `template` é o ÚNICO nome de filtro que pega — templateId, type e
        // documentTemplate são ignorados em silêncio e devolvem os 3 milhões de documentos
        // do hospital inteiro. `begin`/`end` existem no filtro (o erro de validação prova
        // que são DateTime), mas mesmo em ISO completo NÃO filtram nada: pedindo 7 dias
        // vieram documentos de três semanas atrás.
        //
        // Então o corte de período é nosso: `sort=documentDate,DESC` funciona (é o único
        // campo que o servidor aceita ordenar), então lemos de trás para frente e paramos
        // na primeira justificativa anterior ao início do período. Com ~15 a 25 por dia,
        // uma página de 200 já cobre mais de uma semana.
        private const int TemplateJustificativa = 501;
        private const int PaginaJustificativas = 200;
        private const int MaxPaginasJustificativas = 25;   // 5000 documentos = muito mais que qualquer período usual

        public static async Task<VarreduraJustificativas> ListarJustificativasAsync(Session s, DateTime de, DateTime ate)
        {
            var res = new VarreduraJustificativas();
            res.Desde = de.Date; res.Ate = ate.Date;
            if (!await EntrarAsync(s))
            {
                res.Aviso = "Não foi possível abrir a sessão do PEP. Confira a estação no config.txt.";
                return res;
            }

            DateTime inicio = de.Date;
            DateTime fim = ate.Date.AddDays(1);   // exclusivo
            var vistos = new HashSet<int>();

            for (int pagina = 0; pagina < MaxPaginasJustificativas; pagina++)
            {
                string json = await GetAsync(s, Pep + "/api/clinical-documents?page=" + pagina
                    + "&size=" + PaginaJustificativas + "&sort=documentDate,DESC"
                    + "&template=" + TemplateJustificativa);
                if (string.IsNullOrEmpty(json) || json.Length < 10) break;
                if (json.StartsWith("{\"code\"")) { res.Aviso = "O PEP recusou a consulta das justificativas."; break; }

                var daPagina = new List<Justificativa>();
                ExtrairJustificativas(json, daPagina, vistos);
                if (daPagina.Count == 0) break;

                bool passouDoInicio = false;
                foreach (var j in daPagina)
                {
                    if (j.Data != DateTime.MinValue && j.Data < inicio) { passouDoInicio = true; continue; }
                    if (j.Data >= fim) continue;                     // mais recente que a janela
                    res.Itens.Add(j);
                }
                if (passouDoInicio) return res;                      // a lista é decrescente: acabou
                if (daPagina.Count < PaginaJustificativas) return res;
                if (pagina == MaxPaginasJustificativas - 1) res.AtingiuTeto = true;
            }
            return res;
        }

        // Cada documento é um objeto grande e aninhado. Ancoramos no início do registro e
        // lemos os campos da janela — mesmo método usado nas prescrições.
        private static void ExtrairJustificativas(string json, List<Justificativa> destino, HashSet<int> vistos)
        {
            foreach (Match m in Regex.Matches(json, "\\{\"id\":(\\d+),\"status\":\"([A-Z]+)\",\"patient\":\\{\"id\":(\\d+),\"name\":\"([^\"]*)\""))
            {
                int id = int.Parse(m.Groups[1].Value);
                if (!vistos.Add(id)) continue;

                int fim = ProximoRegistro(json, m.Index + 1);
                string bloco = json.Substring(m.Index, fim - m.Index);

                var j = new Justificativa();
                j.Id = id;
                j.Situacao = m.Groups[2].Value;
                j.PacienteId = int.Parse(m.Groups[3].Value);
                j.Paciente = Descapar(m.Groups[4].Value);
                j.Data = Data(bloco, "documentDate");
                j.Fechado = Data(bloco, "closed");
                int at; if (int.TryParse(Texto(bloco, "\"hospitalAdmission\":\\{\"id\":(\\d+)"), out at)) j.Atendimento = at;
                j.Setor = Texto(bloco, "\"sectorName\":\"([^\"]*)\"");
                j.TipoAtendimento = Texto(bloco, "\"hospitalAdmission\":\\{\"id\":\\d+,\"type\":\"([A-Z_]*)\"");
                // O leito tem a unidade aninhada antes do próprio nome
                // ("bed":{"id":..,"unidInt":{..,"name":"ENFERMARIA"},"name":"ENF PED - D3",..),
                // então ancoramos no fecha-chaves da unidade para não pegar o nome dela.
                j.Leito = Texto(bloco, "\"bed\":\\{[\\s\\S]{0,300}?\\},\"name\":\"([^\"]*)\"");
                if (j.Leito.Length == 0) j.Leito = Texto(bloco, "\"bed\":\\{\"id\":\\d+,\"name\":\"([^\"]*)\"");
                // O emissor é o creationProvider; o "provider" solto é o responsável pela internação.
                j.Medico = Texto(bloco, "\"creationProvider\":\\{[^{}]*\"name\":\"([^\"]*)\"");
                string cons = Texto(bloco, "\"creationProvider\":[\\s\\S]{0,400}?\"council\":\\{\"id\":\\d+,\"description\":\"([^\"]*)\"");
                string reg = Texto(bloco, "\"creationProvider\":[\\s\\S]{0,500}?\"councilId\":\"([^\"]*)\"");
                j.Conselho = (cons + " " + reg).Trim();
                string cod = Texto(bloco, "\"icd\":\\{\"code\":\"([^\"]*)\"");
                string lab = Texto(bloco, "\"icd\":\\{\"code\":\"[^\"]*\",\"label\":\"([^\"]*)\"");
                j.Cid = cod.Length > 0 ? (lab.Length > 0 ? cod + " — " + lab : cod) : "";
                destino.Add(j);
            }
        }

        // Onde começa o próximo documento do array (ou o fim do JSON).
        private static int ProximoRegistro(string json, int depois)
        {
            var m = Regex.Match(json.Substring(depois), "\\},\\{\"id\":\\d+,\"status\":\"[A-Z]+\",\"patient\":");
            return m.Success ? depois + m.Index + 1 : json.Length;
        }

        private static DateTime Data(string s, string campo)
        {
            var m = Regex.Match(s, "\"" + campo + "\":\"([0-9T:\\-\\.]{19})");
            DateTime d;
            if (m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d))
                return d.ToLocalTime();
            return DateTime.MinValue;
        }

        // ===== VARREDURA PELO FEED GLOBAL =====
        // Enxerga o hospital todo sem depender de lista de pacientes, mas NÃO identifica o
        // paciente e pode contar o mesmo tratamento duas vezes (prescrição de ontem + a de
        // hoje). Serve como conferência de cobertura da varredura por paciente, não como fonte.
        public static async Task<VarreduraAntimicrobianos> ListarPeloFeedAsync(Session s)
        {
            var res = new VarreduraAntimicrobianos();
            if (!await EntrarAsync(s))
            {
                res.Aviso = "Não foi possível abrir a sessão do PEP. Confira a estação no config.txt.";
                return res;
            }

            // O feed vem do mais novo para o mais antigo e o servidor limita cada página a
            // ~2000 itens (~15h de hospital). Uma página só NÃO basta: quem está em D8/14 pode
            // ter tido a prescrição feita ontem cedo, fora da janela. Então paginamos até
            // cobrir DiasDeFeed dias — ou até o servidor repetir a página (sinal de que ele
            // ignora o parâmetro, e aí paramos em vez de girar à toa).
            DateTime limite = DateTime.UtcNow.AddDays(-DiasDeFeed);
            DateTime menor = DateTime.MaxValue, maior = DateTime.MinValue;
            var cds = new List<int>();
            var vistos = new HashSet<int>();
            string assinaturaAnterior = null;
            bool alcancou = false;

            for (int pagina = 0; pagina < MaxPaginas; pagina++)
            {
                string feed = await GetAsync(s, Pep + "/api/prescript-medicines?page=" + pagina + "&size=" + TamanhoFeed);
                if (string.IsNullOrEmpty(feed) || feed.Length < 10)
                {
                    if (pagina == 0) { res.Aviso = "O PEP não devolveu a lista de itens prescritos."; return res; }
                    alcancou = true; break;   // acabaram as páginas
                }
                string assinatura = feed.Substring(0, Math.Min(200, feed.Length));
                if (assinatura == assinaturaAnterior) break;   // servidor ignora "page"
                assinaturaAnterior = assinatura;

                DateTime menorDaPagina = DateTime.MaxValue;
                foreach (Match m in Regex.Matches(feed, "\"initialDate\":\"([0-9T:\\-\\.]{19})"))
                {
                    DateTime d;
                    if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out d)) continue;
                    if (d < menor) menor = d;
                    if (d > maior) maior = d;
                    if (d < menorDaPagina) menorDaPagina = d;
                }

                int pos = 0;
                while ((pos = feed.IndexOf("\"id\":\"MDA\"", pos, StringComparison.Ordinal)) >= 0)
                {
                    int fim = Math.Min(feed.Length, pos + 900);
                    string janela = feed.Substring(pos, fim - pos);
                    pos += 10;
                    if (Regex.IsMatch(janela, "\"canceled\":true")) continue;
                    var mc = Regex.Match(janela, "\"cdPreMed\":(\\d+)");
                    if (!mc.Success) continue;
                    int cd = int.Parse(mc.Groups[1].Value);
                    if (vistos.Add(cd)) cds.Add(cd);
                }

                if (menorDaPagina != DateTime.MaxValue && menorDaPagina <= limite) { alcancou = true; break; }
            }

            if (menor != DateTime.MaxValue) { res.Desde = menor.ToLocalTime(); res.Ate = maior.ToLocalTime(); }
            res.CoberturaCurta = !alcancou;
            if (res.CoberturaCurta)
                res.Aviso = "O PEP só devolveu prescrições desde " + res.Desde.ToString("dd/MM HH:mm")
                          + ". Tratamento cuja prescrição seja anterior a isso pode não estar na lista.";

            res.Prescricoes = cds.Count;
            if (cds.Count == 0) return res;

            // Detalhe de cada prescrição (é aqui que mora o "D5/7" e a quantidade por horário).
            for (int i = 0; i < cds.Count; i += Paralelo)
            {
                var lote = new List<Task<KeyValuePair<int, string>>>();
                for (int k = i; k < cds.Count && k < i + Paralelo; k++)
                {
                    int cd = cds[k];
                    lote.Add(GetAsync(s, Pep + "/api/prescriptions/" + cd + "/items")
                        .ContinueWith(t => new KeyValuePair<int, string>(cd, t.Result)));
                }
                foreach (var par in await Task.WhenAll(lote))
                    ExtrairItens(par.Key, par.Value, res.Itens);
            }
            return res;
        }

        // Recorta os itens MDA de uma resposta de /prescriptions/{cdPreMed}/items.
        // O JSON é grande e aninhado; em vez de um parser, ancoramos no marcador do
        // antimicrobiano e lemos os campos da janela seguinte — que é onde eles estão.
        private static void ExtrairItens(int cdPreMed, string json, List<ItemAntimicrobiano> destino)
        {
            if (string.IsNullOrEmpty(json)) return;
            const string marca = "\"schemeType\":{\"id\":\"MDA\"";
            int pos = 0;
            while ((pos = json.IndexOf(marca, pos, StringComparison.Ordinal)) >= 0)
            {
                string bloco = json.Substring(pos, Math.Min(1800, json.Length - pos));
                string antes = json.Substring(Math.Max(0, pos - 300), Math.Min(300, pos));
                pos += marca.Length;

                var it = new ItemAntimicrobiano();
                it.CdPreMed = cdPreMed;
                var mid = Regex.Match(antes, "\"id\":(\\d+),\"type\":\\{");
                if (mid.Success) it.ItemId = int.Parse(mid.Groups[1].Value);
                var nomes = Regex.Matches(antes, "\"name\":\"([^\"]*)\"");
                it.Produto = nomes.Count > 0 ? Descapar(nomes[nomes.Count - 1].Groups[1].Value) : "(sem nome)";

                it.Quantidade = Decimal(bloco, "\"quantity\":([0-9]+(?:\\.[0-9]+)?)");
                it.Unidade = Texto(bloco, "\"productUnit\":\\{\"id\":\\d+,\"name\":\"([^\"]*)\"");
                it.Frequencia = Texto(bloco, "\"frequency\":\"([^\"]*)\"");
                it.Via = Texto(bloco, "\"route\":\"([^\"]*)\"");
                it.Dias = Texto(bloco, "\"administrationDays\":\"([^\"]*)\"");
                it.Cronico = Regex.IsMatch(bloco, "\"chronic\":true");

                int atual, total;
                if (Posologia.LerDias(it.Dias, out atual, out total)) { it.DiaAtual = atual; it.DiasPrevistos = total; }
                bool estimada;
                it.AplicacoesPorDia = Posologia.AplicacoesPorDia(it.Frequencia, out estimada);
                it.FrequenciaEstimada = estimada;
                it.NaoRecorrente = Posologia.EhAvulsa(it.Frequencia);

                destino.Add(it);
            }
        }

        private static string Texto(string s, string re)
        { var m = Regex.Match(s, re); return m.Success ? Descapar(m.Groups[1].Value) : ""; }

        private static decimal Decimal(string s, string re)
        {
            var m = Regex.Match(s, re);
            decimal v;
            return m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0m;
        }

        private static string Descapar(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\\\", "\\").Trim();
        }

        // ===== PDF DA JUSTIFICATIVA =====
        //
        // Não existe rota REST para isto: das 214 rotas que o bundle do PEP declara,
        // nenhuma devolve o documento renderizado. O PDF sai do mesmo servlet que o
        // Flash usa ao clicar em "imprimir" — o que se descobre num net-export, porque
        // o PEP abre o resultado em OUTRA aba e o HAR da aba original não registra nada.
        //
        //   POST /mvpep/jasperServlet.pdf
        //   cdDocumentoClinico={id}&acao=printRelatorioDocumentoEletronico
        //
        // Pep-Company, Pep-Department e Pep-Hospital-Admission viajam junto no fluxo do
        // MV, mas são opcionais — verificado item a item. Só documento ASSINADO gera
        // folha: enquanto o médico não assina, o servlet responde 200 com zero byte
        // (mesma resposta de um id inexistente). Custa ~0,6 s.
        public static async Task<byte[]> BaixarJustificativaPdfAsync(Session s, int documento)
        {
            return await BaixarJustificativaPdfAsync(s, documento, 0);
        }

        // A PRESCRIÇÃO também é documento clínico, e o id que /prescriptions?hospitalAdmission=
        // devolve é o próprio cdDocumentoClinico: o mesmo servlet devolve a folha oficial
        // ("PRESCRICAO MEDICA", 1ª via, assinada ICP-Brasil, com o nº da prescrição do MV).
        // Validado ao vivo em 24/10/2026 em 3 prescrições; não altera o "printed" do PEP.
        // Prescrição de enfermagem fechada e prescrição em elaboração não rendem folha (null).
        public static async Task<byte[]> BaixarDocumentoPdfAsync(Session s, long documento)
        {
            if (documento <= 0 || documento > int.MaxValue) return null;
            return await BaixarJustificativaPdfAsync(s, (int)documento, 0);
        }

        // Nº da prescrição no MV (o CD_PRE_MED que a solicitação à farmácia carrega) e itens de
        // uma prescrição do PEP. Nenhuma rota REST devolve esse número — ele só aparece impresso
        // na folha ("Prescrição.: 1234567"; a linha "Prescrição......: N" é a da prescrição
        // anterior, por isso o padrão exige UM ponto). Com o número, /prescriptions/{nº}/items
        // dá os itens e o tipo de cada um; a SUSPENSÃO de item só a folha mostra ("Suspenso
        // dia.: 24/10" logo abaixo do item). Prescrição assinada não muda, então fica em cache
        // e cada folha é lida uma vez só. null = não deu para ler. Folha que não veio (ou veio
        // sem o nº) só é tentada de novo depois de MinutosNovaTentativaFolha — antes era baixada
        // a cada minuto, com uma linha de log a cada vez. Tem item de prescrição: LimparFolhas
        // no logout/expiração.
        private static readonly Dictionary<long, FolhaPrescricao> _folhaPorDocumento = new Dictionary<long, FolhaPrescricao>();
        private static readonly Dictionary<long, DateTime> _falhaPorDocumento = new Dictionary<long, DateTime>();
        private const int MinutosNovaTentativaFolha = 10;

        public static void LimparFolhas()
        {
            lock (_folhaPorDocumento) { _folhaPorDocumento.Clear(); _falhaPorDocumento.Clear(); }
            _sessao = null;
        }

        public static async Task<FolhaPrescricao> LerFolhaPrescricaoAsync(Session s, long documento)
        {
            lock (_folhaPorDocumento)
            {
                FolhaPrescricao ja;
                if (_folhaPorDocumento.TryGetValue(documento, out ja)) return ja;
                DateTime falhou;
                if (_falhaPorDocumento.TryGetValue(documento, out falhou)
                    && (DateTime.Now - falhou).TotalMinutes < MinutosNovaTentativaFolha) return null;
            }
            byte[] pdf = await BaixarDocumentoPdfAsync(s, documento);
            if (pdf == null) { FolhaFalhou(documento); return null; }
            // Extrair e normalizar o texto custa CPU: fora da thread da tela (a primeira carga lê
            // até 6 folhas por vez e a grade estaria animando).
            string texto = await Task.Run(() => TextoDaFolha(pdf, documento));
            var folha = new FolhaPrescricao();
            if (texto != null)
            {
                var m = Regex.Match(texto, "PRESCRICAO\\.\\s*:\\s*(\\d{5,})");
                if (m.Success) long.TryParse(m.Groups[1].Value, out folha.CdPreMed);
            }
            if (folha.CdPreMed <= 0) { FolhaFalhou(documento); return null; }

            folha.Itens = await ItensPrescricaoAsync(s, folha.CdPreMed);
            if (folha.Itens == null) return folha;          // número sem itens (PEP oscilou): não guarda, relê depois
            await Task.Run(() => MarcarSuspensos(texto, folha.Itens));
            lock (_folhaPorDocumento) _folhaPorDocumento[documento] = folha;
            return folha;
        }

        private static string TextoDaFolha(byte[] pdf, long documento)
        {
            try { return PainelPam.SemAcento(string.Join("\n", PdfKits.ExtrairTextos(pdf).ToArray())); }
            catch (Exception ex) { Logger.Log("PEP: nº da prescrição " + documento + ": " + ex.Message); return null; }
            finally { Array.Clear(pdf, 0, pdf.Length); }   // a folha tem dado de paciente: não fica na memória
        }

        private static void FolhaFalhou(long documento)
        {
            lock (_folhaPorDocumento) _falhaPorDocumento[documento] = DateTime.Now;
        }

        // Itens da prescrição (pelo nº do MV). Cada item traz "type":{"id","name","schemeType":
        // {"id":"MDN",...,"medicineType":true,...}}; o medicineType separa o que é da farmácia
        // (medicamento, soro, droga vasoativa) de exame, cuidado, dieta, gás e hemocomponente.
        // DEP (nutrição parenteral) vem com medicineType false, mas é a farmácia que dispensa.
        private static async Task<List<ItemPrescrito>> ItensPrescricaoAsync(Session s, long cdPreMed)
        {
            string j = await GetAsync(s, Pep + "/api/prescriptions/" + cdPreMed + "/items?page=0&size=500");
            if (j == null || !j.TrimStart().StartsWith("[")) return null;
            var res = new List<ItemPrescrito>();
            foreach (Match m in Regex.Matches(j,
                "\"type\":\\{\"id\":\\d+,\"name\":\"((?:[^\"\\\\]|\\\\.)*)\",\"schemeType\":\\{\"id\":\"([A-Za-z0-9_]+)\"([^}]*)\\}"))
            {
                var it = new ItemPrescrito();
                string nome = m.Groups[1].Value;
                try { nome = Regex.Unescape(nome); } catch { }
                it.Nome = nome.Trim();
                it.Esquema = m.Groups[2].Value.ToUpperInvariant();
                it.Medicamento = m.Groups[3].Value.Contains("\"medicineType\":true") || it.Esquema == "DEP";
                res.Add(it);
            }
            return res;
        }

        // Acha cada item na folha pelo começo do nome e dá cada "SUSPENSO DIA" ao item que vem
        // logo antes dele. A folha agrupa por tipo e a rota não, então cada nome é procurado
        // desde o começo da lista de itens (o repetido, depois da ocorrência anterior).
        // Na dúvida, NÃO marca: um item que não se acha na folha faria a marca cair no vizinho,
        // e um medicamento ativo marcado como suspenso esconderia o "Medicamento sem pedido".
        private static void MarcarSuspensos(string texto, List<ItemPrescrito> itens)
        {
            if (string.IsNullOrEmpty(texto) || itens.Count == 0) return;
            string t = Regex.Replace(texto, "\\s+", " ");
            int ini = t.IndexOf("DATAS/HORARIOS", StringComparison.Ordinal);   // fim do cabeçalho
            if (ini < 0) ini = 0;
            var marcas = new List<int>();
            for (int p = t.IndexOf("SUSPENSO DIA", ini, StringComparison.Ordinal); p >= 0;
                 p = t.IndexOf("SUSPENSO DIA", p + 1, StringComparison.Ordinal))
                marcas.Add(p);
            if (marcas.Count == 0) return;
            if (marcas.Count >= itens.Count)
            {
                foreach (var it in itens) it.Suspenso = true;   // uma marca por item: todos suspensos
                return;
            }

            var pos = new int[itens.Count];
            var ultima = new Dictionary<string, int>();
            for (int i = 0; i < itens.Count; i++)
            {
                string k = Regex.Replace(PainelPam.SemAcento(itens[i].Nome), "\\s+", " ").Trim();
                if (k.Length > 30) k = k.Substring(0, 30);
                int de = ini, u;
                if (ultima.TryGetValue(k, out u)) de = u + 1;
                pos[i] = k.Length > 0 ? t.IndexOf(k, de, StringComparison.Ordinal) : -1;
                if (pos[i] < 0) return;
                ultima[k] = pos[i];
            }
            foreach (int marca in marcas)
            {
                int dono = -1;
                for (int i = 0; i < itens.Count; i++)
                    if (pos[i] >= 0 && pos[i] < marca && (dono < 0 || pos[i] > pos[dono])) dono = i;
                if (dono >= 0) itens[dono].Suspenso = true;
            }
        }

        // Com registro > 0 a chamada também serve as justificativas SEM assinatura: é o
        // cdEditorRegistro que o servlet exige quando não consegue localizar o documento
        // sozinho. Ver PepAmf, que é de onde esse número vem.
        public static async Task<byte[]> BaixarJustificativaPdfAsync(Session s, int documento, int registro)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, s.Host + Pep + "/jasperServlet.pdf");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
                req.Content = new StringContent(
                    (registro > 0 ? "cdEditorRegistro=" + registro + "&" : "")
                        + "cdDocumentoClinico=" + documento + "&acao=printRelatorioDocumentoEletronico"
                        + "&Pep-Company=" + AppConfig.Company,
                    System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
                var r = await s.Http.SendAsync(req);
                byte[] b = await r.Content.ReadAsByteArrayAsync();
                if (b.Length > 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return b;
                Logger.Log("PEP: justificativa " + documento + " não rendeu PDF ("
                    + (int)r.StatusCode + ", " + b.Length + " bytes) — provavelmente sem assinatura.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log("PEP: erro ao baixar a justificativa " + documento + ": " + ex.Message);
                return null;
            }
        }

        // ===== PRESCRIÇÕES DE UM ATENDIMENTO (aba Urgência (PAM)) =====
        // /prescriptions?hospitalAdmission= devolve TODAS as prescrições do atendimento com o
        // status: OPENED enquanto o médico escreve, SIGNED/CLOSED depois de assinada (validado
        // ao vivo em 24/10/2026: a solicitação à farmácia nasce no minuto da assinatura).
        // null = o PEP não respondeu (quem chama reabre a sessão UMA vez e repete — as chamadas
        // rodam em paralelo, e reabrir em cada uma dispararia vários logins ao mesmo tempo);
        // lista vazia = nenhuma prescrição.
        // Um registro por objeto do array: ancora no início de cada prescrição ({"id":N,"status":...)
        // e lê os campos até a próxima — os objetos aninhados (paciente, atendimento, médico)
        // não têm "status", "created" nem "template", então não confundem a leitura.
        public static async Task<List<PrescricaoPep>> PrescricoesDoAtendimentoAsync(Session s, string atendimento)
        {
            string caminho = Pep + "/api/prescriptions?hospitalAdmission=" + Uri.EscapeDataString(atendimento) + "&page=0&size=100";
            string j = await GetAsync(s, caminho);
            if (j == null || !j.TrimStart().StartsWith("[")) return null;
            var res = new List<PrescricaoPep>();
            var ancoras = Regex.Matches(j, "\\{\"id\":(\\d+),\"status\":\"([A-Za-z_]+)\"");
            for (int i = 0; i < ancoras.Count; i++)
            {
                int ini = ancoras[i].Index;
                int fim = i + 1 < ancoras.Count ? ancoras[i + 1].Index : j.Length;
                string trecho = j.Substring(ini, fim - ini);
                var p = new PrescricaoPep();
                p.Id = long.Parse(ancoras[i].Groups[1].Value);
                p.Status = ancoras[i].Groups[2].Value;
                p.Criada = DataIso(Regex.Match(trecho, "\"created\":\"([^\"]+)\""));
                p.Fechada = DataIso(Regex.Match(trecho, "\"closed\":\"([^\"]+)\""));
                var mt = Regex.Match(trecho, "\"template\":\\{\"id\":\\d+,\"name\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (mt.Success)
                {
                    string nome = mt.Groups[1].Value;
                    try { nome = Regex.Unescape(nome); } catch { }
                    p.Modelo = nome;
                }
                res.Add(p);
            }
            return res;
        }

        private static DateTime? DataIso(Match m)
        {
            if (!m.Success) return null;
            DateTime d;
            if (DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out d))
                return d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;
            return null;
        }

        private static async Task<string> GetAsync(Session s, string caminho)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, s.Host + caminho);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
                req.Headers.TryAddWithoutValidation("Pep-Company", AppConfig.Company.ToString());
                var r = await s.Http.SendAsync(req);
                return await r.Content.ReadAsStringAsync();
            }
            catch (Exception ex) { Logger.Log("PEP: erro em " + caminho + ": " + ex.Message); return null; }
        }
    }
}
