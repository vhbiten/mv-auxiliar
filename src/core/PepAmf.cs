using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // O que a consulta devolve: o documento clínico e o registro do editor.
    public class RegistroEditor
    {
        public int Documento;        // cdDocumentoClinico
        public int Registro;         // cdEditorRegistro — a chave da impressão
        public string Situacao;      // tpStatus
    }

    // Flex Remoting (AMF) contra o PEP.
    //
    // Existe por um motivo só: a justificativa SEM assinatura não imprime com o id do
    // documento — o jasperServlet exige o cdEditorRegistro, e esse número não aparece em
    // nenhuma das 214 rotas REST do PEP. Ele sai daqui:
    //
    //   prescricaoDocumentoEletronicoController
    //     .consultarCodigoEditorClinicoRegistroDocumentoObrigatorio(cdItpreMed, cdDocumento, 0)
    //
    // A consulta é chaveada pelo ITEM da prescrição, não pelo documento, então resolvemos
    // ao contrário: varremos os itens antimicrobianos do paciente até achar o que aponta
    // para o documento procurado. Tudo aqui é consulta — nada é gravado no MV.
    public static class PepAmf
    {
        private const string Endpoint = "/mvpep/messagebroker/amf.mv";
        private const string Destino = "prescricaoDocumentoEletronicoController";
        private const string Operacao = "consultarCodigoEditorClinicoRegistroDocumentoObrigatorio";

        // Teto de consultas por documento: sem isso um paciente com muitas prescrições
        // antigas viraria dezenas de chamadas num clique só.
        private const int MaxConsultas = 14;

        // Código do documento eletrônico obrigatório do antimicrobiano. Veio da captura e
        // vale para o hospital todo; fica em config.txt caso um dia mude.
        private static int _documentoObrigatorio = -1;
        public static int DocumentoObrigatorio
        {
            get
            {
                if (_documentoObrigatorio < 0)
                {
                    int n;
                    string v = AppConfig.ValorConfig("documento-justificativa");
                    _documentoObrigatorio = (!string.IsNullOrEmpty(v) && int.TryParse(v, out n) && n > 0) ? n : 926;
                }
                return _documentoObrigatorio;
            }
        }

        private static int _sequencia;
        private static string _dsId;

        // documento clínico -> registro do editor. Cada consulta traz o par inteiro, então
        // guardamos todos os que aparecem no caminho, não só o procurado: o segundo clique
        // no mesmo paciente costuma sair de graça.
        private static readonly Dictionary<int, int> _cache = new Dictionary<int, int>();

        public static void LimparCache()
        {
            lock (_cache) _cache.Clear();
            _dsId = null; _sequencia = 0;
        }

        // ===== O QUE A VIEW USA =====

        public static async Task<int> ResolverRegistroAsync(Session s, int paciente, int documento, int atendimento)
        {
            if (documento <= 0) return 0;
            lock (_cache) { int achado; if (_cache.TryGetValue(documento, out achado)) return achado; }

            var gasto = new Contador();

            // 1) itens vigentes do paciente — caminho curto, resolve a maioria.
            if (paciente > 0)
            {
                string pm = await GetAsync(s, "/mvpep/api/prescript-medicines?page=0&size=100&patient=" + paciente);
                int r = await Varrer(s, Ids(pm, MarcaVigente, IdVigente), documento, atendimento, gasto);
                if (r > 0) return r;
            }

            // 2) o médico pode ter suspendido o antibiótico, ou o paciente ter tido alta —
            //    aí o item sai dos vigentes e só sobra no histórico de prescrições.
            if (atendimento > 0 && gasto.N < MaxConsultas)
            {
                string pres = await GetAsync(s, "/mvpep/api/prescriptions?hospitalAdmission=" + atendimento + "&page=0&size=20");
                foreach (int cd in Codigos(pres))
                {
                    if (gasto.N >= MaxConsultas) break;
                    string itens = await GetAsync(s, "/mvpep/api/prescriptions/" + cd + "/items");
                    int r = await Varrer(s, Ids(itens, MarcaHistorico, IdHistorico), documento, atendimento, gasto);
                    if (r > 0) return r;
                }
            }

            lock (_cache) { int achado; if (_cache.TryGetValue(documento, out achado)) return achado; }
            Logger.Log("AMF: não achei o item de prescrição do documento " + documento
                + " (paciente " + paciente + ", atendimento " + atendimento + ", " + gasto.N + " consultas).");
            return 0;
        }

        private class Contador { public int N; }

        private static async Task<int> Varrer(Session s, List<int> itens, int documento, int atendimento, Contador gasto)
        {
            foreach (int item in itens)
            {
                if (gasto.N >= MaxConsultas) return 0;
                gasto.N++;
                var r = await ConsultarAsync(s, item, DocumentoObrigatorio, atendimento);
                if (r == null || r.Registro <= 0 || r.Documento <= 0) continue;
                lock (_cache) _cache[r.Documento] = r.Registro;
                if (r.Documento == documento) return r.Registro;
            }
            return 0;
        }

        // Os dois endpoints marcam o antimicrobiano de formas diferentes:
        //   prescript-medicines      {"id":N,"schemaItem":{"id":"MDA"
        //   prescriptions/{cd}/items {"id":N,"type":{...  ..."schemeType":{"id":"MDA"
        private const string MarcaVigente = "\"schemaItem\":{\"id\":\"MDA\"";
        private const string MarcaHistorico = "\"schemeType\":{\"id\":\"MDA\"";
        private static readonly Regex IdVigente = new Regex("\"id\":(\\d+),$", RegexOptions.RightToLeft);
        private static readonly Regex IdHistorico = new Regex("\"id\":(\\d+),\"type\":\\{", RegexOptions.RightToLeft);

        // Ancora no marcador e lê o id imediatamente antes dele — o JSON é grande e
        // aninhado demais para valer um parser completo só por causa de um número.
        private static List<int> Ids(string json, string marca, Regex padrao)
        {
            var saida = new List<int>();
            if (string.IsNullOrEmpty(json)) return saida;
            int pos = 0;
            while ((pos = json.IndexOf(marca, pos, StringComparison.Ordinal)) >= 0)
            {
                string antes = json.Substring(Math.Max(0, pos - 300), Math.Min(300, pos));
                pos += marca.Length;
                var m = padrao.Match(antes);
                int n;
                if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n > 0 && !saida.Contains(n)) saida.Add(n);
            }
            return saida;
        }

        private static List<int> Codigos(string json)
        {
            var saida = new List<int>();
            if (string.IsNullOrEmpty(json)) return saida;
            foreach (Match m in Regex.Matches(json, "\"id\":(\\d{6,9})"))
            {
                int n;
                if (int.TryParse(m.Groups[1].Value, out n) && n > 0 && !saida.Contains(n)) saida.Add(n);
            }
            return saida;
        }

        // ===== A CHAMADA =====

        // O "source" carrega a configuração de serialização que o servidor espera; sem ele
        // o BlazeDS devolve o grafo inteiro e a resposta explode de tamanho.
        private static string Fonte(int atendimento)
        {
            return "fieldsToIgnore:;/depth:10/sourceName:br.com.mv.sma.prescricao.documento_eletronico"
                 + "::DocumentoEletronicoPrescricao." + Destino
                 + "/startPage:0/pageSize:0/paging:false"
                 + "/Pep-Company:" + AppConfig.Company
                 + "/Pep-Department:0"
                 + (atendimento > 0 ? "/Pep-Hospital-Admission:" + atendimento : "")
                 + "/Pep-Accept-Language:pt_BR";
        }

        public static async Task<RegistroEditor> ConsultarAsync(Session s, int itemPrescricao,
                                                                int documentoObrigatorio, int atendimento)
        {
            object corpo = await ChamarAsync(s, Operacao,
                new object[] { itemPrescricao, documentoObrigatorio, 0 }, atendimento);

            var msg = corpo as AmfObjeto;
            if (msg == null) return null;

            var dto = msg.Campo("body") as AmfObjeto;
            if (dto == null) return null;

            int reg = ParaInt(dto.Buscar("cdEditorClinico", 3));
            if (reg <= 0) return null;
            return new RegistroEditor
            {
                Documento = ParaInt(dto.Buscar("cdDocumentoClinico", 3)),
                Registro = reg,
                Situacao = dto.Buscar("tpStatus", 3) as string
            };
        }

        // Chamada genérica. Pública porque é por aqui que se sonda qualquer outra operação
        // do controlador quando o MV mudar de ideia sobre algum fluxo.
        public static async Task<object> ChamarAsync(Session s, string operacao, object[] args, int atendimento)
        {
            return await ChamarAsync(s, Destino, operacao, args, atendimento);
        }

        public static async Task<object> ChamarAsync(Session s, string destino, string operacao, object[] args, int atendimento)
        {
            try
            {
                byte[] pedido = Amf.MontarChamada(operacao, Fonte(atendimento), destino,
                    _dsId, "channel-amf", args, ++_sequencia);

                var req = new HttpRequestMessage(HttpMethod.Post, s.Host + Endpoint);
                req.Content = new ByteArrayContent(pedido);
                req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x-amf");
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "ShockwaveFlash/32.0.0.371");
                req.Headers.TryAddWithoutValidation("Referer", s.Host + "/mvpep/5/FlexLoaderMVPEP2.swf/[[DYNAMIC]]/8");

                var r = await s.Http.SendAsync(req);
                byte[] bruto = await r.Content.ReadAsByteArrayAsync();
                if (bruto.Length < 16)
                {
                    Logger.Log("AMF: resposta curta em " + operacao + " (" + (int)r.StatusCode
                        + ", " + bruto.Length + " bytes).");
                    return null;
                }

                string alvo;
                object corpo = Amf.LerResposta(bruto, out alvo);
                if (alvo != null && alvo.EndsWith("/onStatus", StringComparison.Ordinal))
                {
                    Logger.Log("AMF: o servidor recusou " + operacao + ": " + Resumo(corpo));
                    return null;
                }

                // Guarda o DSId que o servidor atribuiu, para as próximas chamadas.
                var msg = corpo as AmfObjeto;
                if (msg != null)
                {
                    object id = msg.Buscar("DSId", 2);
                    if (id is string && !string.IsNullOrEmpty((string)id)) _dsId = (string)id;
                }
                return corpo;
            }
            catch (Exception ex)
            {
                Logger.Log("AMF: falha em " + operacao + ": " + ex.Message);
                return null;
            }
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
            catch (Exception ex) { Logger.Log("AMF: erro em " + caminho + ": " + ex.Message); return null; }
        }

        private static int ParaInt(object v)
        {
            if (v is int) return (int)v;
            if (v is double) return (int)Math.Round((double)v);
            if (v is string) { int n; return int.TryParse((string)v, out n) ? n : 0; }
            return 0;
        }

        private static string Resumo(object o)
        {
            var a = o as AmfObjeto;
            if (a == null) return o == null ? "(vazio)" : o.ToString();
            object m = a.Buscar("faultString", 3) ?? a.Buscar("message", 3);
            return m != null ? m.ToString() : a.Classe;
        }
    }
}
