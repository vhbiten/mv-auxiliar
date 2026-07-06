using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Cliente do protocolo SOAP "Morphis" do SoulMV (motor de forms).
    // Mantém o estado da sessão de forms: task atual e reqId (encadeamento das mensagens).
    public class MorphisClient
    {
        private const string ENV_OPEN =
            "<soapenv:Envelope xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" " +
            "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" " +
            "xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\"><soapenv:Body>" +
            "<tns:MessageRequest xmlns:tns=\"urn:schemas:morphis:message\">";
        private const string ENV_CLOSE = "</tns:MessageRequest></soapenv:Body></soapenv:Envelope>";

        private readonly Session _s;
        private readonly string _url;

        public string WorkspaceTask;
        public string FormTask;
        public string LastReqId;
        public string EstoqueRecordId;   // GUID do registro MVTO_ESTOQUE (criado ao abrir o form)
        public string ItemRecordId;      // GUID do registro ITMVTO_ESTOQUE (linha de produto em digitação)
        public string ModalTask;         // task do modal de comprovante (usado para fechar e seguir p/ etiqueta)

        public MorphisClient(Session s)
        {
            _s = s;
            _url = s.Host + "/soul-product-forms/services/message/message?user=" + s.User;
        }

        // POST de um envelope SOAP, devolve a resposta parseada.
        public async Task<MorphisResponse> PostAsync(string innerHeaderBody)
        {
            string envelope = ENV_OPEN + innerHeaderBody + ENV_CLOSE;
            var req = new HttpRequestMessage(HttpMethod.Post, _url);
            req.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            req.Headers.TryAddWithoutValidation("SOAPAction", "\"\"");
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Origin", _s.Host);

            var resp = await _s.Http.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();

            // Sessão morta? O servidor redireciona para o login do CAS (o HttpClient segue
            // o redirect e recebe a página HTML) ou nega o acesso com 401/403.
            if (RespostaEhLogin(resp, text))
            {
                Logger.Log("Morphis: sessão expirada (HTTP " + (int)resp.StatusCode + ", destino " + DestinoFinal(resp) + ").");
                throw new SessaoExpiradaException();
            }

            if ((int)resp.StatusCode != 200)
            {
                Logger.Log("Morphis: HTTP " + (int)resp.StatusCode);
                throw new Exception("Servidor respondeu HTTP " + (int)resp.StatusCode + " na mensagem Morphis.");
            }

            MorphisResponse parsed;
            try { parsed = MorphisResponse.Parse(text); }
            catch (System.Xml.XmlException ex)
            {
                // resposta que não é o XML do Morphis: quase sempre é a sessão que caiu
                Logger.Log("Morphis: resposta não-XML (sessão expirada?): " + ex.Message);
                throw new SessaoExpiradaException();
            }
            if (parsed.ReqId != null) LastReqId = parsed.ReqId;
            return parsed;
        }

        private static string DestinoFinal(HttpResponseMessage resp)
        {
            return (resp.RequestMessage != null && resp.RequestMessage.RequestUri != null)
                ? resp.RequestMessage.RequestUri.AbsoluteUri : "?";
        }

        // Heurística de sessão expirada: 401/403, redirect que terminou na página do
        // autenticador (CAS), ou corpo HTML no lugar do XML.
        private static bool RespostaEhLogin(HttpResponseMessage resp, string corpo)
        {
            int st = (int)resp.StatusCode;
            if (st == 401 || st == 403) return true;

            string destino = DestinoFinal(resp).ToLowerInvariant();
            if (destino.Contains("autenticador") || destino.Contains("/cas/") || destino.Contains("/login")) return true;

            string ini = corpo == null ? "" : corpo.TrimStart();
            if (ini.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                ini.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        private string MonitorInfo()
        {
            if (string.IsNullOrEmpty(LastReqId)) return "";
            return "<monitorInfo reqId=\"" + LastReqId + "\" data=\"CLIENT=auxiliar\"></monitorInfo>";
        }

        // 1) Inicializa a área de trabalho (obtém o task do $MAIN$_BLOCK).
        public async Task<MorphisResponse> WorkspaceInitAsync()
        {
            string header =
                "<header><control isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" +
                "<action name=\"WORKSPACE_INIT\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                "<parameter name=\"MAQUINA\" value=\"" + Xml(Environment.MachineName) + "\" datatype=\"String\"></parameter>" +
                "<parameter name=\"user\" value=\"" + Xml(_s.User) + "\" datatype=\"String\"></parameter>" +
                "</action></control></header><body></body>";
            var r = await PostAsync(header);
            if (r.IsOk) WorkspaceTask = r.Task;
            Logger.Log("Morphis WORKSPACE_INIT: outcome=" + r.Outcome + " task=" + r.Task);
            return r;
        }

        // 2) Abre o form (M_PRODUZIR_KIT). Usa o task da área de trabalho.
        public async Task<MorphisResponse> CallFormAsync(string formName, string menuId)
        {
            string header =
                "<header><control block=\"$MAIN$_BLOCK\" item=\"MENU_TREE\" task=\"" + WorkspaceTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" +
                "<action block=\"$MAIN$_BLOCK\" item=\"MENU_TREE\" name=\"CALL_FORM\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>" +
                MonitorInfo() + "</control></header>" +
                "<body><callForm taskName=\"" + Xml(formName) + "\"><parameters>" +
                "<parameter value=\"" + Xml(menuId) + "\" name=\"menuId\"></parameter>" +
                "</parameters></callForm></body>";
            var r = await PostAsync(header);
            if (r.IsOk)
            {
                FormTask = r.Task;
                var blk = r.Block_("MVTO_ESTOQUE");
                if (blk != null) EstoqueRecordId = blk.Selected != null ? blk.Selected : (blk.Records.Count > 0 ? blk.Records[0].Id : null);
            }
            Logger.Log("Morphis CALL_FORM " + formName + ": outcome=" + r.Outcome + " task=" + r.Task + " estoqueRec=" + EstoqueRecordId);
            return r;
        }

        private static string Param(string n, string v)
        {
            return "<parameter name=\"" + n + "\" value=\"" + Xml(v) + "\" datatype=\"string\"></parameter>";
        }

        // Monta o header+body de uma mensagem (com o task informado).
        private string MsgT(string cblock, string citem, string task, string action, string body)
        {
            return "<header><control block=\"" + cblock + "\" item=\"" + citem + "\" task=\"" + task +
                   "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + MonitorInfo() +
                   "</control></header><body>" + body + "</body>";
        }

        // Mensagem no task do form (atalho).
        private string Msg(string cblock, string citem, string action, string body)
        {
            return MsgT(cblock, citem, FormTask, action, body);
        }

        // LOV_OK para a LOV de lote (seleciona a linha 'index').
        public async Task<MorphisResponse> SelecionarLoteAsync(int index)
        {
            string action = "<action name=\"LOV_OK\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            string body = "<list name=\"LV_LOTE_PRODUZIDO\"><item name=\"searchText\">%</item><data selected=\"" + index + "\"></data></list>";
            return await PostAsync(Msg("ITMVTO_ESTOQUE", "CD_LOTE", action, body));
        }

        // NEXT_ITEM dentro da linha de produto; opcionalmente seta um valor (ex.: QT_MOVIMENTACAO).
        public async Task<MorphisResponse> NextItemAsync(string currentItem, string setItem, string setValue)
        {
            string action = "<action block=\"ITMVTO_ESTOQUE\" item=\"" + currentItem + "\" name=\"NEXT_ITEM\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            string body = setItem != null
                ? "<block selected=\"" + ItemRecordId + "\" name=\"ITMVTO_ESTOQUE\"><record status=\"C\" id=\"" + ItemRecordId + "\"><item name=\"" + setItem + "\">" + Xml(setValue) + "</item></record></block>"
                : "";
            return await PostAsync(Msg("ITMVTO_ESTOQUE", currentItem, action, body));
        }

        // Monta o PREVIEW do que seria produzido: para cada item da fórmula, lê os lotes,
        // aplica a regra (maior saldo), seleciona o lote e seta a quantidade (= QT_FORMULA * numKits).
        // ATENÇÃO: isto NÃO finaliza (não chama btnImprimir) — nada é gravado/baixado de estoque.
        public async Task<System.Collections.Generic.List<PreviewItem>> MontarPreviewAsync(int estoque, int kitCdProduto, int numKits)
        {
            var resp = await AbrirKitAsync(estoque, kitCdProduto);
            var formula = LerFormula(resp);
            await IniciarProdutoAsync();

            var preview = new System.Collections.Generic.List<PreviewItem>();
            foreach (var item in formula)
            {
                // O form "Produzir Kit" EXIGE a quantidade da receita por item (1 kit). Não dá
                // para multiplicar QT_MOVIMENTACAO (isso zera a LOV de lote do próximo item).
                // Então entramos SEMPRE a receita (1 kit) e aferimos os N kits pela soma dos saldos.
                decimal qtdPorKit = item.QtFormula;
                decimal qtdTotal = qtdPorKit * numKits;

                await GotoItemAsync("ITMVTO_ESTOQUE", "CD_PRODUTO", "CD_LOTE", ItemRecordId, "CD_PRODUTO", item.CdProduto.ToString());
                var lv = await ListValuesAsync("ITMVTO_ESTOQUE", "CD_LOTE");
                var lotes = ParseLotes(lv.List_("LV_LOTE_PRODUZIDO"));

                var escolha = EscolhaLote.Escolher(lotes, qtdPorKit);   // lote p/ 1 kit (maior saldo)
                decimal disponivel = 0; foreach (var l in lotes) disponivel += l.SaldoAtual;

                var pi = new PreviewItem();
                pi.Produto = item;
                pi.QtdPorKit = qtdPorKit;
                pi.Quantidade = qtdTotal;
                pi.Disponivel = disponivel;
                pi.Lote = escolha.Lote;
                pi.Suficiente = (escolha.Lote != null) && (disponivel >= qtdTotal);
                pi.Aviso = escolha.Aviso;
                preview.Add(pi);

                if (escolha.Lote == null)
                {
                    Logger.Log("Produto " + item.CdProduto + " sem lote disponível — preview interrompido.");
                    break;  // não dá nem 1 kit — o form não avança
                }

                // entra 1 kit (receita) no form, só p/ manter o form válido e ler os demais itens
                int idx = 0;
                for (int i = 0; i < lotes.Count; i++) if (lotes[i].CdLote == escolha.Lote.CdLote) { idx = i; break; }
                await SelecionarLoteAsync(idx);
                await NextItemAsync("CD_LOTE", null, null);
                await NextItemAsync("DSP_DS_UNIDADE", null, null);
                var r = await NextItemAsync("QT_MOVIMENTACAO", "QT_MOVIMENTACAO", qtdPorKit.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));

                var blk = r.Block_("ITMVTO_ESTOQUE");
                if (blk != null && blk.Selected != null) ItemRecordId = blk.Selected;
            }
            Logger.Log("Preview montado: " + preview.Count + " itens p/ " + numKits + " kit(s) (NÃO finalizado).");
            return preview;
        }

        // GOTOITEM: navega de currentItem para targetItem; opcionalmente seta um valor no campo atual.
        public async Task<MorphisResponse> GotoItemAsync(string block, string currentItem, string targetItem,
            string recordId, string setItem, string setValue)
        {
            string action =
                "<action block=\"" + block + "\" item=\"" + currentItem + "\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", currentItem) + Param("previousBlock", block) + Param("previousRecord", recordId == null ? "" : recordId) +
                Param("item", targetItem) + Param("block", block) + Param("record", recordId == null ? "" : recordId) +
                Param("actionValue", "") + Param("fireItemAction", "") + "</action>";
            string body = "";
            if (setItem != null)
                body = "<block selected=\"" + recordId + "\" name=\"" + block + "\"><record status=\"C\" id=\"" + recordId + "\"><item name=\"" + setItem + "\">" + Xml(setValue) + "</item></record></block>";
            string header = "<header><control block=\"" + block + "\" item=\"" + currentItem + "\" task=\"" + FormTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + MonitorInfo() + "</control></header><body>" + body + "</body>";
            return await PostAsync(header);
        }

        // LIST_VALUES: abre a LOV de um campo; a resposta traz a lista (Lists[...]).
        public async Task<MorphisResponse> ListValuesAsync(string block, string item)
        {
            string action = "<action block=\"" + block + "\" item=\"" + item + "\" name=\"LIST_VALUES\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            string header = "<header><control block=\"" + block + "\" item=\"" + item + "\" task=\"" + FormTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + MonitorInfo() + "</control></header><body></body>";
            return await PostAsync(header);
        }

        // LOV_OK: confirma a seleção de uma linha (index) da LOV.
        public async Task<MorphisResponse> LovOkAsync(string block, string item, string listName, int selectedIndex)
        {
            string action = "<action name=\"LOV_OK\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            string body = "<list name=\"" + listName + "\"><item name=\"searchText\">%</item><data selected=\"" + selectedIndex + "\"></data></list>";
            string header = "<header><control block=\"" + block + "\" item=\"" + item + "\" task=\"" + FormTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + MonitorInfo() + "</control></header><body>" + body + "</body>";
            return await PostAsync(header);
        }

        // Fluxo READ-ONLY: seleciona estoque + kit e devolve a resposta com a fórmula (ITFORMULA).
        // NÃO adiciona produtos, NÃO seta quantidade, NÃO finaliza — nada é gravado.
        public async Task<MorphisResponse> AbrirKitAsync(int estoque, int kitCdProduto)
        {
            await GotoItemAsync("MVTO_ESTOQUE", "CD_ESTOQUE", "DT_MVTO_ESTOQUE", EstoqueRecordId, "CD_ESTOQUE", estoque.ToString());
            await GotoItemAsync("MVTO_ESTOQUE", "DT_MVTO_ESTOQUE", "CD_KIT", EstoqueRecordId, null, null);
            var lv = await ListValuesAsync("MVTO_ESTOQUE", "CD_KIT");
            var lista = lv.List_("LOV_FORMULA");
            if (lista == null) throw new Exception("Lista de kits (LOV_FORMULA) não retornou.");

            int idx = -1;
            foreach (var rec in lista.Records)
                if (rec.Get("CD_PRODUTO") == kitCdProduto.ToString()) { int.TryParse(rec.Id, out idx); break; }
            if (idx < 0) throw new Exception("Kit " + kitCdProduto + " não encontrado na lista do estoque " + estoque + ".");

            Logger.Log("Morphis: kit " + kitCdProduto + " no índice " + idx + " da LOV_FORMULA");
            var r = await LovOkAsync("MVTO_ESTOQUE", "CD_KIT", "LOV_FORMULA", idx);
            return r;
        }

        // Alto nível: a partir de uma sessão logada, abre o form e monta o preview do kit.
        // NÃO finaliza nada.
        public static async Task<System.Collections.Generic.List<PreviewItem>> GerarPreviewAsync(
            Session s, int estoque, int kitCdProduto, int numKits)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            await m.CallFormAsync("M_PRODUZIR_KIT", "MV.12.01.01.06.01.#");
            return await m.MontarPreviewAsync(estoque, kitCdProduto, numKits);
        }

        // ⚠️ PRODUZ N KITS = N produções SEPARADAS (cada kit gera o seu próprio código de barras).
        // Um kit não pode ser feito com quantidades multiplicadas numa única requisição — o form
        // exige a receita por item. Então repetimos abrir->preview(1 kit)->finalizar, N vezes.
        // GRAVA estoque a cada kit. Devolve as URLs dos comprovantes; em caso de falha, para e
        // devolve o que JÁ foi produzido (kits anteriores já existem — irreversível).
        public static async Task<ProducaoVariosResult> ProduzirVariosKitsAsync(Session s, int estoque, int kitCdProduto, int numKits)
        {
            var res = new ProducaoVariosResult();
            res.Solicitados = numKits;
            for (int k = 0; k < numKits; k++)
            {
                try
                {
                    var m = new MorphisClient(s);
                    await m.WorkspaceInitAsync();
                    await m.CallFormAsync("M_PRODUZIR_KIT", "MV.12.01.01.06.01.#");
                    var pv = await m.MontarPreviewAsync(estoque, kitCdProduto, 1);   // receita p/ 1 kit
                    foreach (var p in pv)
                        if (!p.Suficiente)
                        {
                            res.Erro = "Kit " + (k + 1) + ": sem saldo para \"" + p.Produto.DsProduto + "\".";
                            Logger.Log("ProduzirVarios: " + res.Erro + " (já produzidos: " + res.Produzidos + ")");
                            return res;
                        }
                    string url = await m.FinalizarAsync();   // grava ESTE kit (1 código de barras)
                    var kp = new KitProduzido();
                    kp.ComprovanteUrl = url;
                    // etiqueta (ZPL) deste kit — best-effort: o kit JÁ foi criado, então uma
                    // falha aqui não derruba a produção (a etiqueta pode sair pelo sistema).
                    try { kp.Etiqueta = await m.ObterEtiquetaZplAsync(); }
                    catch (Exception ee) { Logger.Log("ProduzirVarios: etiqueta do kit " + (k + 1) + " não gerada (não-fatal): " + ee.Message); }
                    res.Kits.Add(kp);
                    Logger.Log("ProduzirVarios: kit " + (k + 1) + "/" + numKits + " produzido -> " + url + (kp.Etiqueta != null ? " (+etiqueta)" : ""));
                }
                catch (Exception ex)
                {
                    // Sessão expirada sem nenhum kit criado: propaga (a tela volta ao login).
                    // Com kits já criados, NÃO propaga: devolve o resultado parcial para o
                    // usuário ver os comprovantes/etiquetas do que JÁ existe (irreversível).
                    if (ex is SessaoExpiradaException && res.Produzidos == 0) throw;
                    res.Erro = "Falha ao produzir o kit " + (k + 1) + ": " + ex.Message;
                    Logger.Log("ProduzirVarios: " + res.Erro + " (já produzidos: " + res.Produzidos + ")");
                    return res;
                }
            }
            return res;
        }

        // ===== CONFERÊNCIA DE LOTES (R_CONF_LOTE) — só gera relatório PDF, NÃO mexe em estoque =====

        // Header com windowCtrl (aba), como o form de conferência exige.
        private string MsgWin(string cblock, string citem, string action, string body, string tabPage)
        {
            string win = "<windowCtrl name=\"WIN_PRINCIPAL\"><canvas tabPage=\"" + tabPage + "\" name=\"CV_TAB_CANVAS_PAI\"></canvas></windowCtrl>";
            return "<header><control block=\"" + cblock + "\" item=\"" + citem + "\" task=\"" + FormTask +
                   "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + win + MonitorInfo() +
                   "</control></header><body>" + body + "</body>";
        }

        private string GotoAction(string block, string cur, string next)
        {
            return "<action block=\"" + block + "\" item=\"" + cur + "\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", cur) + Param("previousBlock", block) + Param("previousRecord", "") +
                Param("item", next) + Param("block", block) + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "") + "</action>";
        }

        private string ParamBody(string block, string id, string item, string val)
        {
            string sel = id.Length > 0 ? " selected=\"" + id + "\"" : "";
            return "<block" + sel + " name=\"" + block + "\"><record status=\"C\" id=\"" + id + "\"><item name=\"" + item + "\">" + Xml(val) + "</item></record></block>";
        }

        private static string SelectedOf(MorphisResponse r, string blockName)
        {
            var b = r.Block_(blockName);
            if (b == null) return null;
            return b.Selected != null ? b.Selected : (b.Records.Count > 0 ? b.Records[0].Id : null);
        }

        // Gera o relatório de Conferência de Lotes para a lista de produtos. Devolve a URL do PDF.
        public async Task<string> ConferenciaLotesAsync(int estoque, System.Collections.Generic.List<int> produtos)
        {
            await CallFormAsync("R_CONF_LOTE", "MV.12.01.09.02.04.#");

            // Parâmetros (aba padrão)
            await PostAsync(MsgWin("PARAMETROS", "CD_ESTOQUE", GotoAction("PARAMETROS", "CD_ESTOQUE", "CD_ESPECIE"),
                ParamBody("PARAMETROS", "", "CD_ESTOQUE", estoque.ToString()), "CV_TAB_PADRAO"));
            await PostAsync(MsgWin("PARAMETROS", "CD_ESPECIE", GotoAction("PARAMETROS", "CD_ESPECIE", "TP_ORDEM"), "", "CV_TAB_PADRAO"));
            await PostAsync(MsgWin("PARAMETROS", "TP_ORDEM", GotoAction("PARAMETROS", "TP_ORDEM", "SN_IMPRIMIR_SEM_ESTOQUE"),
                ParamBody("PARAMETROS", "", "TP_ORDEM", "Descricao"), "CV_TAB_PADRAO"));

            // Marca "não imprimir sem estoque" = N e vai para a aba de produtos
            string changeAct = "<action name=\"change\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var rChange = await PostAsync(MsgWin("PARAMETROS", "SN_IMPRIMIR_SEM_ESTOQUE", changeAct,
                ParamBody("PARAMETROS", "", "SN_IMPRIMIR_SEM_ESTOQUE", "N"), "CV_TAB_PRODUTO"));

            string prodGuid = SelectedOf(rChange, "PRODUTO");

            // Adiciona cada produto (NEXT_ITEM set CD_SEL)
            foreach (int cod in produtos)
            {
                if (prodGuid == null) { Logger.Log("Conferência: sem GUID de produto — abortando add."); break; }
                string act = "<action block=\"PRODUTO\" item=\"CD_SEL\" name=\"NEXT_ITEM\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
                string body = ParamBody("PRODUTO", prodGuid, "CD_SEL", cod.ToString());
                var r = await PostAsync(MsgWin("PRODUTO", "CD_SEL", act, body, "CV_TAB_PRODUTO"));

                var alert = r.Command("SHOW_ALERT");
                if (alert != null)
                {
                    string an = alert.Params.ContainsKey("name") ? alert.Params["name"] : "CFG_WARNING";
                    Logger.Log("Conferência: alerta ao adicionar " + cod + " (" + an + ") — fechando.");
                    string closeAct = "<action name=\"CLOSE_ALERT\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
                    var rc = await PostAsync(MsgWin("PRODUTO", "CD_SEL", closeAct, "<alert name=\"" + an + "\"><selected>0</selected></alert>", "CV_TAB_PRODUTO"));
                    string g2 = SelectedOf(rc, "PRODUTO"); if (g2 != null) prodGuid = g2;
                }
                else
                {
                    string ng = SelectedOf(r, "PRODUTO"); if (ng != null) prodGuid = ng;
                }
            }

            // Gera o relatório (btnGerarRelatorio)
            string genAct = "<action block=\"PRODUTO\" item=\"CD_SEL\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_SEL") + Param("previousBlock", "PRODUTO") + Param("previousRecord", prodGuid == null ? "" : prodGuid) +
                Param("item", "BTN_GERAR_RELATORIO") + Param("block", "TOOLBAR") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnGerarRelatorio_click") + "</action>";
            var rGen = await PostAsync(MsgWin("PRODUTO", "CD_SEL", genAct, "", "CV_TAB_PRODUTO"));
            var rep = rGen.Command("REPORT_GENERATED");
            string url = (rep != null && rep.Params.ContainsKey("report_uri")) ? rep.Params["report_uri"] : null;
            if (url == null)
                throw new Exception("Conferência: relatório não gerado (sem REPORT_GENERATED). Mensagens: " + string.Join("; ", rGen.Messages));
            Logger.Log("Conferência: relatório gerado em " + url);
            return url;
        }

        // Alto nível: a partir de uma sessão logada, gera a Conferência de Lotes.
        public static async Task<string> GerarConferenciaAsync(Session s, int estoque, System.Collections.Generic.List<int> produtos)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            return await m.ConferenciaLotesAsync(estoque, produtos);
        }

        // Baixa o PDF da conferência recém-gerada e APRENDE os pares código -> nome
        // (persistidos no cache local do ProdutoNomes). Melhor esforço; devolve quantos
        // nomes eram inéditos.
        public static async Task<int> AprenderNomesConferenciaAsync(Session s, string urlPdf)
        {
            var m = new MorphisClient(s);
            byte[] pdf = await m.BaixarBytesAsync(urlPdf).ConfigureAwait(false);
            var pares = PdfConfLotes.ExtrairNomes(pdf);
            int novos = ProdutoNomes.Aprender(pares);
            Logger.Log("Nomes de produto: " + pares.Count + " no PDF, " + novos + " novos no cache.");
            return novos;
        }

        // CONSULTA DE KITS (form R_KIT_PROD): a partir de um produto comum a todos os kits
        // (ex.: 4992 = agulha 40x12), gera o relatório que lista os kits em estoque com
        // nome + código de barras + quantidade. READ-ONLY (só gera o relatório). Devolve a URL do PDF.
        public async Task<string> ConsultarKitsAsync(int estoque, int cdProduto)
        {
            await CallFormAsync("R_KIT_PROD", "MV.12.01.09.02.11.#");

            // CD_ESTOQUE -> CD_PRODUTO
            await PostAsync(MsgWin("PARAMETROS", "CD_ESTOQUE", GotoAction("PARAMETROS", "CD_ESTOQUE", "CD_PRODUTO"),
                ParamBody("PARAMETROS", "", "CD_ESTOQUE", estoque.ToString()), "CV_TAB_PADRAO"));
            // CD_PRODUTO -> CD_LOTE
            await PostAsync(MsgWin("PARAMETROS", "CD_PRODUTO", GotoAction("PARAMETROS", "CD_PRODUTO", "CD_LOTE"),
                ParamBody("PARAMETROS", "", "CD_PRODUTO", cdProduto.ToString()), "CV_TAB_PADRAO"));

            // CD_LOTE -> dispara o relatório (btnGerarRelatorio_click)
            string genAct = "<action block=\"PARAMETROS\" item=\"CD_LOTE\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_LOTE") + Param("previousBlock", "PARAMETROS") + Param("previousRecord", "") +
                Param("item", "BTN_GERAR_RELATORIO") + Param("block", "TOOLBAR") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnGerarRelatorio_click") + "</action>";
            var rGen = await PostAsync(MsgWin("PARAMETROS", "CD_LOTE", genAct, "", "CV_TAB_PADRAO"));
            var rep = rGen.Command("REPORT_GENERATED");
            string url = (rep != null && rep.Params.ContainsKey("report_uri")) ? rep.Params["report_uri"] : null;
            if (url == null)
                throw new Exception("Consulta de kits: relatório não gerado (sem REPORT_GENERATED). Mensagens: " + string.Join("; ", rGen.Messages));
            Logger.Log("Consulta de kits: relatório gerado em " + url);
            return url;
        }

        public static async Task<string> GerarConsultaKitsAsync(Session s, int estoque, int cdProduto)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            return await m.ConsultarKitsAsync(estoque, cdProduto);
        }

        // Baixa o conteúdo de uma URL usando a sessão autenticada (ex.: o PDF do relatório).
        public async Task<byte[]> BaixarBytesAsync(string url)
        {
            var resp = await _s.Http.GetAsync(url).ConfigureAwait(false);
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            // sessão morta: em vez do arquivo vem a página de login (HTML) ou 401/403
            int st = (int)resp.StatusCode;
            string ini = bytes.Length > 0
                ? Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).TrimStart()
                : "";
            if (st == 401 || st == 403 ||
                ini.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                ini.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("BaixarBytes: sessão expirada (HTTP " + st + ", destino " + DestinoFinal(resp) + ").");
                throw new SessaoExpiradaException();
            }
            return bytes;
        }

        // Alto nível: consulta + baixa o PDF + interpreta a lista de kits (nome/cód. barras/qtd).
        // Devolve a lista E a URL do relatório (para o botão "Abrir o PDF"). READ-ONLY.
        public static async Task<ConsultaKitsResult> ConsultarKitsCompletoAsync(Session s, int estoque, int cdProduto)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            string url = await m.ConsultarKitsAsync(estoque, cdProduto);
            byte[] pdf = await m.BaixarBytesAsync(url);
            var res = new ConsultaKitsResult();
            res.ReportUrl = url;
            res.Kits = PdfKits.Interpretar(pdf);
            return res;
        }

        // REIMPRIME a etiqueta de um kit já existente, pelo código de barras. READ-ONLY:
        // abre M_PRODUZIR_KIT em modo CONSULTA, busca pelo barras, clica "Imprimir etiqueta"
        // e devolve o ZPL (PRINT_ZEBRA_ARQ). NÃO cria/altera kit. (Fluxo do HAR REIMPRESSAO.)
        public async Task<EtiquetaZpl> ReimprimirEtiquetaAsync(string codBarras)
        {
            await CallFormAsync("M_PRODUZIR_KIT", "MV.12.01.01.06.01.#");

            // 1) SEARCH (entra em modo consulta) -> GUIDs dos registros de consulta
            string searchAct = "<action name=\"SEARCH\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var rSearch = await PostAsync(Msg("MVTO_ESTOQUE", "CD_ESTOQUE", searchAct, ""));
            string mvtoRec = SelectedOf(rSearch, "MVTO_ESTOQUE");
            string itRec = SelectedOf(rSearch, "ITMVTO_ESTOQUE");
            if (mvtoRec == null) throw new Exception("Reimpressão: não entrou em modo de consulta (sem registro MVTO_ESTOQUE).");

            // 2) GOTOITEM CD_MVTO_ESTOQUE -> DSP_CD_BARRAS
            string gotoAct = "<action block=\"MVTO_ESTOQUE\" item=\"CD_MVTO_ESTOQUE\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_MVTO_ESTOQUE") + Param("previousBlock", "MVTO_ESTOQUE") + Param("previousRecord", mvtoRec) +
                Param("item", "DSP_CD_BARRAS") + Param("block", "MVTO_ESTOQUE") + Param("record", mvtoRec) +
                Param("actionValue", "") + Param("fireItemAction", "") + "</action>";
            await PostAsync(Msg("MVTO_ESTOQUE", "CD_MVTO_ESTOQUE", gotoAct, ReimpQueryBody(itRec, mvtoRec, null)));

            // 3) EXECUTE_QUERY com DSP_CD_BARRAS = código de barras
            string execAct = "<action name=\"EXECUTE_QUERY\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var rExec = await PostAsync(Msg("MVTO_ESTOQUE", "DSP_CD_BARRAS", execAct, ReimpQueryBody(itRec, mvtoRec, codBarras)));

            // achou o kit? (registro MVTO_ESTOQUE com CD_KIT preenchido)
            bool achou = false;
            var blk = rExec.Block_("MVTO_ESTOQUE");
            if (blk != null)
                foreach (var rec in blk.Records)
                {
                    string ck = rec.Get("CD_KIT");
                    if (ck != null && ck.Trim().Length > 0) { achou = true; break; }
                }
            if (!achou) throw new Exception("Kit com código de barras " + codBarras + " não encontrado no estoque.");
            Logger.Log("Reimpressão: kit do barras " + codBarras + " encontrado.");

            // 4) btnImprimeEtiqueta_click -> TASK_OPEN (tela de etiqueta)
            string impAct = "<action block=\"MVTO_ESTOQUE\" item=\"BTN_IMPRIME_ETIQUETA\" name=\"btnImprimeEtiqueta_click\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var rImp = await PostAsync(Msg("ITMVTO_ESTOQUE", "CD_PRODUTO", impAct, ""));
            var to = rImp.Command("TASK_OPEN");
            string etqTask = (to != null && to.Params.ContainsKey("task")) ? to.Params["task"] : null;
            if (etqTask == null) throw new Exception("Reimpressão: a tela de etiqueta não abriu (sem TASK_OPEN). Msgs: " + string.Join("; ", rImp.Messages));

            // 5) GOTOITEM CD_ETIQUETA -> btnGerarRelatorio_click -> PRINT_ZEBRA_ARQ (ZPL)
            string genAct = "<action block=\"PARAMETROS\" item=\"CD_ETIQUETA\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_ETIQUETA") + Param("previousBlock", "PARAMETROS") + Param("previousRecord", "") +
                Param("item", "BTN_GERAR_RELATORIO") + Param("block", "TOOLBAR") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnGerarRelatorio_click") + "</action>";
            var rZpl = await PostAsync(MsgT("PARAMETROS", "CD_ETIQUETA", etqTask, genAct, ""));
            var pz = rZpl.Command("PRINT_ZEBRA_ARQ");
            if (pz == null) throw new Exception("Reimpressão: o servidor não retornou o ZPL (PRINT_ZEBRA_ARQ). Msgs: " + string.Join("; ", rZpl.Messages));

            var etq = new EtiquetaZpl();
            etq.Zpl = pz.Params.ContainsKey("Texto") ? pz.Params["Texto"] : null;
            etq.PrinterId = pz.Params.ContainsKey("Arquivo") ? pz.Params["Arquivo"] : "LPT2";
            etq.Copies = 1;
            Logger.Log("Reimpressão: ZPL obtido (" + (etq.Zpl != null ? etq.Zpl.Length : 0) + " chars), impressora=" + etq.PrinterId);

            // 6) fecha o alerta CFG_WARNING_A (best-effort)
            try
            {
                string closeAct = "<action name=\"CLOSE_ALERT\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
                await PostAsync(MsgT("TOOLBAR", "BTN_GERAR_RELATORIO", etqTask, closeAct, "<alert name=\"CFG_WARNING_A\"><selected>0</selected></alert>"));
            }
            catch { }

            return etq;
        }

        // Blocos de consulta (ITMVTO_ESTOQUE + MVTO_ESTOQUE) com campos vazios; o barras vai em DSP_CD_BARRAS.
        private string ReimpQueryBody(string itRec, string mvtoRec, string barras)
        {
            string it = "<block selected=\"" + itRec + "\" name=\"ITMVTO_ESTOQUE\"><record status=\"C\" id=\"" + itRec + "\">" +
                "<item name=\"DSP_CODIGO_DE_BARRAS\"></item><item name=\"CD_PRODUTO\"></item><item name=\"CD_FORNECEDOR\"></item>" +
                "<item name=\"DSP_NM_FORNECEDOR\"></item><item name=\"CD_LOTE\"></item><item name=\"DT_VALIDADE\"></item>" +
                "<item name=\"DSP_DS_UNIDADE\"></item><item name=\"QT_MOVIMENTACAO\"></item></record></block>";
            string barItem = "<item name=\"DSP_CD_BARRAS\">" + (barras != null ? Xml(barras) : "") + "</item>";
            string mv = "<block selected=\"" + mvtoRec + "\" name=\"MVTO_ESTOQUE\"><record status=\"C\" id=\"" + mvtoRec + "\">" +
                "<item name=\"CD_MVTO_ESTOQUE\"></item><item name=\"CD_ESTOQUE\"></item><item name=\"DT_MVTO_ESTOQUE\"></item>" +
                "<item name=\"HR_MVTO_ESTOQUE\"></item><item name=\"DT_VALIDADE\"></item><item name=\"CD_USUARIO\"></item>" + barItem +
                "<item name=\"CD_KIT\"></item><item name=\"CD_FORMULA\"></item><item name=\"CD_MVTO_ESTOQUE_TEM\"></item>" +
                "<item name=\"CD_BARRAS_DEV\"></item><item name=\"CD_MVTO_ESTOQUE_DEV\"></item></record></block>";
            return it + mv;
        }

        // Alto nível: reimprime a etiqueta de um kit pelo código de barras (devolve o ZPL).
        public static async Task<EtiquetaZpl> GerarReimpressaoAsync(Session s, string codBarras)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            return await m.ReimprimirEtiquetaAsync(codBarras);
        }

        // ===== RASTREIO DE SOLICITAÇÕES (form M_BAIXASOL, bloco SOLSAI_PRO) — READ-ONLY =====
        // Lista as solicitações de saída de um estoque via EXECUTE_QUERY (query-by-example).
        // NÃO confirma produtos nem fecha baixa: só lê o cabeçalho de cada solicitação.
        //
        // Paginação por JANELA DE NÚMERO: a consulta devolve 50 por vez, em ordem crescente
        // de CD_SOLSAI_PRO; avançamos o piso do número para (maior+1) até vir menos de 50.
        // Assim buscamos TODAS as solicitações do período (sem duplicar nem pular).
        private static readonly string[] SolCampos = {
            "DSP_TP_ORIGEM_SOLICITACAO","CD_SOLSAI_PRO","CD_PRE_MED","CD_ESTOQUE","TP_SITUACAO","TP_SOLSAI_PRO",
            "CD_ESTOQUE_SOLICITANTE","CD_ATENDIMENTO","CD_AVISO_CIRURGIA","CD_UNID_INT","DT_SOLSAI_PRO","HR_SOLSAI_PRO",
            "DS_PRIMEIRA_NECESSIDADE","SN_EMITIDO2","SN_IMPRIMIR","SN_URGENTE","SN_CONFIRMA_PRODUCAO","CD_SETOR",
            "CD_AGRUPAMENTO","TP_ORIGEM_SOLICITACAO","CD_PACIENTE_INTEGRA","NM_PACIENTE_INTEGRA","CD_ATENDIMENTO_INTEGRA",
            "NM_USUARIO_SOLICITACAO","NM_USUARIO_MVTO","CD_TP_SOLICITACAO","CD_REQUISICAO"
        };

        // situacao: "P"/"S"/"A"/null(todas); origem: "PRE"/"AVU"/"TRA"/null(todas).
        // de/ate: intervalo por data (aplicado no CLIENTE). tetoJanelas: proteção anti-loop.
        //
        // Rastreia solicitações do estoque. Quando NÃO se filtra a situação ("Todas"), o
        // servidor ordena cada página por (situação/data), NÃO por número puro — então uma
        // varredura por janela de número SEM filtro de situação PULA registros (validado ao
        // vivo: perdia ~30% dos pendentes, inclusive datados de hoje). Correção: varremos
        // cada situação (P/A/S) separadamente — aí cada página fica em ordem de número
        // dentro da situação e a janela pagina completo — e unimos os resultados.
        public static async Task<SolicitacoesResult> RastrearSolicitacoesAsync(
            Session s, int estoque, string situacao, string origem, DateTime? de, DateTime? ate, int tetoJanelas)
        {
            if (!string.IsNullOrEmpty(situacao))
                return await ScanSituacaoAsync(s, estoque, situacao, origem, de, ate, tetoJanelas);

            // "Todas": une as três situações (cada uma pagina de forma confiável por número).
            var todas = new SolicitacoesResult();
            var vistosTodas = new System.Collections.Generic.HashSet<long>();
            foreach (var sit in new[] { "P", "A", "S" })
            {
                var parcial = await ScanSituacaoAsync(s, estoque, sit, origem, de, ate, tetoJanelas);
                if (parcial.AtingiuTeto) todas.AtingiuTeto = true;
                foreach (var it in parcial.Itens)
                    if (vistosTodas.Add(it.Numero)) todas.Itens.Add(it);
            }
            todas.Itens.Sort(delegate(Solicitacao a, Solicitacao b) { return b.Numero.CompareTo(a.Numero); });
            todas.TotalServidor = todas.Itens.Count;
            return todas;
        }

        // IMPORTANTE (aprendido na engenharia reversa): a consulta com filtro de DATA
        // devolve os registros ESPALHADOS (não pelo índice de número), o que quebra a
        // paginação por janela. Já a consulta SEM filtro de data devolve sempre os 50
        // MENORES números >= piso (índice) DENTRO da situação consultada. Por isso: paginamos
        // por número sem data e filtramos a data no cliente. Uma sonda inicial (com data) dá
        // um ponto de partida para o piso.
        private static async Task<SolicitacoesResult> ScanSituacaoAsync(
            Session s, int estoque, string situacao, string origem, DateTime? de, DateTime? ate, int tetoJanelas)
        {
            var res = new SolicitacoesResult();

            // FASE 1 — sonda COM filtro de data só para ancorar o MAIOR número do período
            // (um registro real, confiável). ATENÇÃO: o totalRecords do servidor NÃO é
            // confiável (na eng. reversa dava 378 onde só existiam 200), então NÃO é usado.
            // Sonda com filtro de data: ancora o MAIOR e o MENOR número do período.
            long anchorMin = long.MaxValue, anchorMax = 0;
            if (de.HasValue)
            {
                var probe = await ConsultaSolAsync(s, estoque, situacao, origem, de, 1);
                foreach (var it in probe.Itens)
                {
                    if (it.Numero < anchorMin) anchorMin = it.Numero;
                    if (it.Numero > anchorMax) anchorMax = it.Numero;
                }
            }
            if (de.HasValue && anchorMax == 0) return res;   // nada no período

            // OTIMIZAÇÃO: piso o mais PRÓXIMO possível do início do período, para não varrer
            // registros de dias anteriores à toa (cada janela custa ~0,6s×3 no servidor lento).
            // Usa o MENOR nº da sonda (com folga p/ a amostragem) e, como rede de segurança,
            // não passa do backlog: nunca abaixo de anchorMax - (dias+1)*600.
            int dias = de.HasValue ? Math.Max(0, (DateTime.Today - de.Value.Date).Days) : 0;
            long floorSonda = (anchorMin == long.MaxValue) ? 1 : anchorMin - 300;
            long floorMax = (anchorMax == 0) ? 1 : anchorMax - (long)(dias + 1) * 600;
            long floor = Math.Max(1, Math.Max(floorSonda, floorMax));   // o mais ALTO (menos varredura) que ainda cobre
            long ceil = (anchorMax == 0) ? long.MaxValue : anchorMax + 500;

            // FASE 2 — janela ASCENDENTE por número, SEM filtro de data (ordem estável pelo
            // índice = 50 menores >= piso), filtrando a data no CLIENTE. Para quando esvazia
            // a página (topo dos dados), passa do fim do período, ou bate o teto de janelas.
            var vistos = new System.Collections.Generic.HashSet<long>();
            long piso = floor;
            int janelas = 0;
            while (janelas < tetoJanelas)
            {
                janelas++;
                var w = await ConsultaSolAsync(s, estoque, situacao, origem, null, piso);
                long maxNum = piso;
                foreach (var sol in w.Itens)
                {
                    if (sol.Numero > maxNum) maxNum = sol.Numero;
                    if (!vistos.Add(sol.Numero)) continue;
                    if (DentroDoIntervalo(sol.Data, de, ate)) res.Itens.Add(sol);
                }
                Logger.Log("Solicitações: janela " + janelas + " (piso>=" + piso + ") -> " + w.Itens.Count + " recs, no filtro=" + res.Itens.Count);

                if (w.Itens.Count < 50) break;               // chegou no topo dos dados
                long novoPiso = maxNum + 1;
                if (novoPiso <= piso) break;                  // trava anti-loop
                if (novoPiso > ceil) break;                   // passou do fim do período
                piso = novoPiso;
            }
            if (janelas >= tetoJanelas) res.AtingiuTeto = true;
            res.TotalServidor = res.Itens.Count;

            res.Itens.Sort(delegate(Solicitacao a, Solicitacao b) { return b.Numero.CompareTo(a.Numero); }); // mais recentes 1º
            return res;
        }

        // Uma consulta EXECUTE_QUERY em um form NOVO (WorkspaceInit + CALL_FORM próprios).
        // Reabrir o form a cada janela é o que funciona de forma confiável (reusar o mesmo
        // form OU a mesma área de trabalho para muitos forms falha: as consultas seguintes
        // vêm vazias/ignoram o filtro). de!=null aplica a data (só na sonda); pisoCd aplica
        // CD_SOLSAI_PRO>=piso.
        private class ConsultaSol { public System.Collections.Generic.List<Solicitacao> Itens; public int Total; }
        private static async Task<ConsultaSol> ConsultaSolAsync(Session s, int estoque, string situacao, string origem, DateTime? de, long pisoCd)
        {
            var r = new ConsultaSol();
            r.Itens = new System.Collections.Generic.List<Solicitacao>();
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            var cf = await m.CallFormAsync("M_BAIXASOL", "MV.12.01.02.01.#");
            var blk = cf.Block_("SOLSAI_PRO");
            string recId = blk != null ? (blk.Selected != null ? blk.Selected : (blk.Records.Count > 0 ? blk.Records[0].Id : null)) : null;
            if (recId == null) { Logger.Log("Solicitações: form sem registro inicial."); return r; }

            var filtro = new System.Collections.Generic.Dictionary<string, string>();
            filtro["CD_ESTOQUE"] = estoque.ToString();
            if (!string.IsNullOrEmpty(situacao)) filtro["TP_SITUACAO"] = situacao;
            // Origem no SERVIDOR: PRE/TRA filtram por TP_ORIGEM; "DEV" (devolução) filtra
            // por TP_SOLSAI_PRO=C (a origem vem vazia); "AVU" (Material) NÃO filtra no
            // servidor — os pedidos de setor também são "material" e têm origem vazia,
            // então o refino fica no cliente (OrigemEfetiva).
            if (origem == "PRE" || origem == "TRA") filtro["TP_ORIGEM_SOLICITACAO"] = origem;
            else if (origem == "DEV") filtro["TP_SOLSAI_PRO"] = "C";
            if (de.HasValue) filtro["DT_SOLSAI_PRO"] = ">=" + de.Value.ToString("dd/MM/yyyy");
            if (pisoCd > 1) filtro["CD_SOLSAI_PRO"] = ">=" + pisoCd;

            var sb = new StringBuilder();
            foreach (var c in SolCampos)
            {
                string v; if (!filtro.TryGetValue(c, out v)) v = "";
                sb.Append("<item name=\"").Append(c).Append("\">").Append(Xml(v)).Append("</item>");
            }
            string body = "<body><block selected=\"" + recId + "\" name=\"SOLSAI_PRO\"><record status=\"C\" id=\"" + recId + "\">" + sb + "</record></block></body>";
            string header = "<header><control block=\"SOLSAI_PRO\" item=\"CD_SOLSAI_PRO\" task=\"" + m.FormTask +
                "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" +
                "<action name=\"EXECUTE_QUERY\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>" +
                "</control></header>";
            var q = await m.PostAsync(header + body);
            r.Total = TotalRegistros(q.Raw);
            r.Itens = ParseSolicitacoes(q.Raw, estoque);
            return r;
        }

        private static bool DentroDoIntervalo(string data, DateTime? de, DateTime? ate)
        {
            if (!de.HasValue && !ate.HasValue) return true;
            DateTime dt;
            if (!TryData(data, out dt)) return true;   // sem data legível: não descarta
            if (de.HasValue && dt.Date < de.Value.Date) return false;
            if (ate.HasValue && dt.Date > ate.Value.Date) return false;
            return true;
        }

        // CONSULTA INCREMENTAL (leve): busca só as solicitações com número MAIOR que o
        // informado (CD_SOLSAI_PRO é sequencial/crescente). Quase sempre 1 janela (~1-2s).
        // Sem filtro de situação a página vem ordenada por situação/data e a janela por
        // número PULA registros (mesmo defeito da carga completa); por isso, se a primeira
        // página vier CHEIA (>=50), refaz por situação (P/A/S) — cada uma pagina certinho.
        // AtingiuTeto=true quando alguma varredura estourou o teto de janelas: o chamador
        // NÃO deve usar o resultado para somar itens nem para diff de situação. READ-ONLY.
        public static async Task<SolicitacoesResult> RastrearNovasAsync(
            Session s, int estoque, string situacao, string origem, long aposNumero)
        {
            var res = new SolicitacoesResult();
            if (aposNumero <= 0) return res;

            if (string.IsNullOrEmpty(situacao))
            {
                // Tentativa rápida: uma página sem filtro de situação. Se veio incompleta
                // (<50), o servidor devolveu TUDO que existe acima do piso — nada foi pulado.
                var w = await ConsultaSolAsync(s, estoque, null, origem, null, aposNumero + 1);
                if (w.Itens.Count < 50)
                {
                    var vistos = new System.Collections.Generic.HashSet<long>();
                    foreach (var sol in w.Itens)
                        if (sol.Numero > aposNumero && vistos.Add(sol.Numero)) res.Itens.Add(sol);
                    return res;
                }
                // Muita coisa nova (>=50): varre por situação para não pular registros.
                var todos = new System.Collections.Generic.HashSet<long>();
                foreach (var sit in new[] { "P", "A", "S" })
                {
                    var parcial = await ScanNovasSituacaoAsync(s, estoque, sit, origem, aposNumero);
                    if (parcial.AtingiuTeto) res.AtingiuTeto = true;
                    foreach (var it in parcial.Itens)
                        if (todos.Add(it.Numero)) res.Itens.Add(it);
                }
                return res;
            }

            return await ScanNovasSituacaoAsync(s, estoque, situacao, origem, aposNumero);
        }

        // Varre UMA situação por janela de número ascendente a partir de aposNumero+1.
        private static async Task<SolicitacoesResult> ScanNovasSituacaoAsync(
            Session s, int estoque, string situacao, string origem, long aposNumero)
        {
            var res = new SolicitacoesResult();
            var vistos = new System.Collections.Generic.HashSet<long>();
            long piso = aposNumero + 1;
            int janelas = 0;
            while (true)
            {
                if (janelas >= 8) { res.AtingiuTeto = true; break; }   // teto: resultado INCOMPLETO
                janelas++;
                var w = await ConsultaSolAsync(s, estoque, situacao, origem, null, piso);
                long maxNum = piso - 1;
                foreach (var sol in w.Itens)
                {
                    if (sol.Numero > maxNum) maxNum = sol.Numero;
                    if (sol.Numero < piso) continue;            // segurança
                    if (!vistos.Add(sol.Numero)) continue;
                    res.Itens.Add(sol);
                }
                if (w.Itens.Count < 50) break;                  // acabaram
                long np = maxNum + 1;
                if (np <= piso) break;
                piso = np;
            }
            return res;
        }

        // Gera o PDF da solicitação (relatório R_SOLSAI_PRO) e devolve a URL para abrir no
        // navegador. READ-ONLY: reproduz o fluxo do sistema (consulta → btnImprimeEtq_click
        // abre o canvas CG$IMP_ETQ → solicitacao_click gera o relatório). NÃO dá baixa.
        public static async Task<string> GerarPdfSolicitacaoAsync(Session s, long numero)
        {
            var m = new MorphisClient(s);
            await m.WorkspaceInitAsync();
            var cf = await m.CallFormAsync("M_BAIXASOL", "MV.12.01.02.01.#");
            var blk = cf.Block_("SOLSAI_PRO");
            string recId0 = blk != null ? (blk.Selected != null ? blk.Selected : (blk.Records.Count > 0 ? blk.Records[0].Id : null)) : null;
            if (recId0 == null) throw new Exception("Formulário de solicitações não abriu.");

            // 1) EXECUTE_QUERY pelo número exato
            var sb = new StringBuilder();
            foreach (var c in SolCampos)
                sb.Append("<item name=\"").Append(c).Append("\">").Append(c == "CD_SOLSAI_PRO" ? numero.ToString() : "").Append("</item>");
            string qbody = "<body><block selected=\"" + recId0 + "\" name=\"SOLSAI_PRO\"><record status=\"C\" id=\"" + recId0 + "\">" + sb + "</record></block></body>";
            string qhdr = "<header><control block=\"SOLSAI_PRO\" item=\"CD_SOLSAI_PRO\" task=\"" + FormTaskOf(m) +
                "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" +
                "<action name=\"EXECUTE_QUERY\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action></control></header>";
            var q = await m.PostAsync(qhdr + qbody);
            var qb = q.Block_("SOLSAI_PRO");
            string recId = null;
            if (qb != null)
                foreach (var rec in qb.Records)
                {
                    string v; if (rec.Items.TryGetValue("CD_SOLSAI_PRO", out v) && v == numero.ToString()) { recId = rec.Id; break; }
                }
            if (recId == null && qb != null && qb.Records.Count > 0) recId = qb.Records[0].Id;
            if (recId == null) throw new Exception("Solicitação " + numero + " não encontrada no estoque.");

            // 2) marca imprimir (SN_IMPRIMIR=S) — como no fluxo do sistema
            string a5 = "<action block=\"SOLSAI_PRO\" item=\"CD_SOLSAI_PRO\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_SOLSAI_PRO") + Param("previousBlock", "SOLSAI_PRO") + Param("previousRecord", recId) +
                Param("item", "SN_IMPRIMIR") + Param("block", "SOLSAI_PRO") + Param("record", recId) +
                Param("actionValue", "S") + Param("fireItemAction", "") + "</action>";
            await m.PostAsync(m.MsgT("SOLSAI_PRO", "CD_SOLSAI_PRO", m.FormTask, a5, ""));

            // 3) btnImprimeEtq_click → abre o canvas de impressão (CG$IMP_ETQ)
            string a6 = "<action block=\"SOLSAI_PRO\" item=\"BTN_IMPRIME_ETQ\" name=\"btnImprimeEtq_click\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            await m.PostAsync(m.MsgT("SOLSAI_PRO", "SN_IMPRIMIR", m.FormTask, a6, ""));

            // 4) solicitacao_click → gera o relatório R_SOLSAI_PRO (REPORT_GENERATED)
            string a7 = "<action block=\"CG$IMP_ETQ\" item=\"ETIQUETA\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "ETIQUETA") + Param("previousBlock", "CG$IMP_ETQ") + Param("previousRecord", "null") +
                Param("item", "SOLICITACAO") + Param("block", "CG$IMP_ETQ") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "solicitacao_click") + "</action>";
            var r7 = await m.PostAsync(m.MsgT("CG$IMP_ETQ", "ETIQUETA", m.FormTask, a7, ""));
            var rep = r7.Command("REPORT_GENERATED");
            string url = (rep != null && rep.Params.ContainsKey("report_uri")) ? rep.Params["report_uri"] : null;
            if (url == null)
                throw new Exception("O servidor não gerou o PDF da solicitação (sem REPORT_GENERATED). Mensagens: " + string.Join("; ", r7.Messages));
            Logger.Log("Solicitação " + numero + ": PDF gerado em " + url);
            return url;
        }

        private static string FormTaskOf(MorphisClient m) { return m.FormTask; }

        private static int TotalRegistros(string xml)
        {
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<page[^>]*totalRecords=\"(\\d+)\"");
            int n; return (m.Success && int.TryParse(m.Groups[1].Value, out n)) ? n : 0;
        }

        private static bool TryData(string dt, out DateTime val)
        {
            val = DateTime.MinValue;
            if (string.IsNullOrEmpty(dt)) return false;
            string d = dt.Length >= 10 ? dt.Substring(0, 10) : dt;   // "dd/MM/yyyy ..." -> "dd/MM/yyyy"
            return DateTime.TryParseExact(d, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out val);
        }

        // Extrai as solicitações do bloco SOLSAI_PRO (só registros com número e do estoque pedido).
        private static System.Collections.Generic.List<Solicitacao> ParseSolicitacoes(string xml, int estoque)
        {
            var res = new System.Collections.Generic.List<Solicitacao>();
            var doc = new System.Xml.XmlDocument();
            try { doc.LoadXml(xml); } catch { return res; }
            foreach (System.Xml.XmlElement block in doc.GetElementsByTagName("block"))
            {
                if (MorphisResponse.Attr(block, "name") != "SOLSAI_PRO") continue;
                foreach (System.Xml.XmlElement rec in block.GetElementsByTagName("record"))
                {
                    var v = new System.Collections.Generic.Dictionary<string, string>();
                    foreach (System.Xml.XmlElement it in rec.GetElementsByTagName("item"))
                        if (MorphisResponse.Attr(it, "type") == "value")
                        {
                            string nm = MorphisResponse.Attr(it, "name");
                            if (nm != null && !v.ContainsKey(nm)) v[nm] = it.InnerText;
                        }
                    string num; if (!v.TryGetValue("CD_SOLSAI_PRO", out num) || num.Length == 0) continue;
                    long n; if (!long.TryParse(num, out n)) continue;
                    var sol = new Solicitacao();
                    sol.Numero = n;
                    sol.Estoque = ParseInt(Val(v, "CD_ESTOQUE"), estoque);
                    sol.Situacao = Val(v, "TP_SITUACAO");
                    sol.Origem = Val(v, "TP_ORIGEM_SOLICITACAO");
                    sol.TpSol = Val(v, "TP_SOLSAI_PRO").Trim();
                    sol.Setor = Val(v, "DSP_NM_SETOR").Trim();
                    sol.Data = Val(v, "DT_SOLSAI_PRO");
                    sol.Hora = Val(v, "HR_SOLSAI_PRO");
                    sol.Urgente = Val(v, "SN_URGENTE") == "S";
                    sol.Solicitante = Val(v, "NM_USUARIO_SOLICITACAO").Trim();
                    sol.Atendimento = Val(v, "CD_ATENDIMENTO");
                    // O nome do paciente vem no campo de EXIBIÇÃO (DSP_NM_PACIENTE);
                    // NM_PACIENTE_INTEGRA volta vazio na listagem (validado ao vivo).
                    sol.Paciente = Val(v, "DSP_NM_PACIENTE").Trim();
                    if (sol.Paciente.Length == 0) sol.Paciente = Val(v, "NM_PACIENTE_INTEGRA").Trim();
                    res.Add(sol);
                }
            }
            return res;
        }

        private static string Val(System.Collections.Generic.Dictionary<string, string> d, string k)
        { string v; return d.TryGetValue(k, out v) && v != null ? v : ""; }
        private static int ParseInt(string s, int def) { int n; return int.TryParse(s, out n) ? n : def; }

        // ⚠️ FINALIZA a produção = CRIA O KIT e baixa estoque. Chamar UMA única vez.
        // Sequência: btnImprimir_click -> abre modal de parâmetro -> confirma (dispara o
        // btnImprimir do modal) -> REPORT_GENERATED com a URL do comprovante (PDF).
        // Devolve a URL do PDF do comprovante.
        public async Task<string> FinalizarAsync()
        {
            Logger.Log("FINALIZAR: clicando btnImprimir (COMMIT) ...");
            string a1 = "<action block=\"MVTO_ESTOQUE\" item=\"BTN_IMPRIMIR\" name=\"btnImprimir_click\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var r1 = await PostAsync(Msg("ITMVTO_ESTOQUE", "CD_PRODUTO", a1, ""));
            var taskOpen = r1.Command("TASK_OPEN");
            string modalTask = (taskOpen != null && taskOpen.Params.ContainsKey("task")) ? taskOpen.Params["task"] : null;
            if (modalTask == null)
                throw new Exception("Finalização: o modal de comprovante não abriu (sem TASK_OPEN). Mensagens: " + string.Join("; ", r1.Messages));
            ModalTask = modalTask;
            Logger.Log("FINALIZAR: modal task=" + modalTask);

            string a2 = "<action block=\"COMUM\" item=\"TP_SAIDA\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "TP_SAIDA") + Param("previousBlock", "COMUM") + Param("previousRecord", "") +
                Param("item", "BTN_IMPRIMIR") + Param("block", "COMUM") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnImprimir_click") + "</action>";
            string header2 = "<header><control block=\"COMUM\" item=\"TP_SAIDA\" task=\"" + modalTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + a2 + MonitorInfo() + "</control></header><body></body>";
            var r2 = await PostAsync(header2);

            var rep = r2.Command("REPORT_GENERATED");
            string url = (rep != null && rep.Params.ContainsKey("report_uri")) ? rep.Params["report_uri"] : null;
            if (url == null)
                throw new Exception("Finalização não gerou comprovante (sem REPORT_GENERATED). Mensagens: " + string.Join("; ", r2.Messages));
            Logger.Log("FINALIZAR: comprovante gerado em " + url);
            return url;
        }

        // Após FinalizarAsync: fecha o modal, abre a task da etiqueta, gera o relatório e
        // devolve o ZPL (PRINT_ZEBRA_ARQ). NÃO testado nesta máquina (sem LAS) — validar na farmácia.
        public async Task<EtiquetaZpl> ObterEtiquetaZplAsync()
        {
            if (ModalTask == null) throw new Exception("Etiqueta: finalize o kit primeiro (sem ModalTask).");

            // #76 fecha o modal do comprovante (btnSair) -> alerta + TASK_CLOSE
            string a76 = "<action block=\"COMUM\" item=\"BTN_IMPRIMIR\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "BTN_IMPRIMIR") + Param("previousBlock", "COMUM") + Param("previousRecord", "") +
                Param("item", "BTN_SAIR") + Param("block", "COMUM") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnSair_click") + "</action>";
            await PostAsync(MsgT("COMUM", "BTN_IMPRIMIR", ModalTask, a76, ""));

            // #77 fecha o alerta (CFG_INFORMATION) -> abre a task da etiqueta
            string a77 = "<action name=\"CLOSE_ALERT\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            var r77 = await PostAsync(MsgT("MVTO_ESTOQUE", "BTN_IMPRIMIR", FormTask, a77, "<alert name=\"CFG_INFORMATION\"><selected>0</selected></alert>"));
            var to = r77.Command("TASK_OPEN");
            string etq = (to != null && to.Params.ContainsKey("task")) ? to.Params["task"] : null;
            if (etq == null) throw new Exception("Etiqueta: a tela de etiqueta não abriu (sem TASK_OPEN).");
            Logger.Log("Etiqueta: task=" + etq);

            // #78 navega CD_ETIQUETA -> DSP_ESTOQUE_DO_LOTE
            string a78 = "<action block=\"PARAMETROS\" item=\"CD_ETIQUETA\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_ETIQUETA") + Param("previousBlock", "PARAMETROS") + Param("previousRecord", "") +
                Param("item", "DSP_ESTOQUE_DO_LOTE") + Param("block", "PARAMETROS") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "") + "</action>";
            await PostAsync(MsgT("PARAMETROS", "CD_ETIQUETA", etq, a78, ""));

            // #79 navega DSP_ESTOQUE_DO_LOTE -> CD_ESTOQUE (seta DSP_ESTOQUE_DO_LOTE=1)
            string a79 = "<action block=\"PARAMETROS\" item=\"DSP_ESTOQUE_DO_LOTE\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "DSP_ESTOQUE_DO_LOTE") + Param("previousBlock", "PARAMETROS") + Param("previousRecord", "") +
                Param("item", "CD_ESTOQUE") + Param("block", "PARAMETROS") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "") + "</action>";
            await PostAsync(MsgT("PARAMETROS", "DSP_ESTOQUE_DO_LOTE", etq, a79,
                "<block name=\"PARAMETROS\"><record status=\"C\" id=\"\"><item name=\"DSP_ESTOQUE_DO_LOTE\">1</item></record></block>"));

            // #80 dispara btnGerarRelatorio -> PRINT_ZEBRA_ARQ (ZPL pronto)
            string a80 = "<action block=\"PARAMETROS\" item=\"CD_ESTOQUE\" name=\"PROC:GOTOITEM\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\">" +
                Param("previousItem", "CD_ESTOQUE") + Param("previousBlock", "PARAMETROS") + Param("previousRecord", "") +
                Param("item", "BTN_GERAR_RELATORIO") + Param("block", "TOOLBAR") + Param("record", "") +
                Param("actionValue", "") + Param("fireItemAction", "btnGerarRelatorio_click") + "</action>";
            var r80 = await PostAsync(MsgT("PARAMETROS", "CD_ESTOQUE", etq, a80, ""));
            var pz = r80.Command("PRINT_ZEBRA_ARQ");
            if (pz == null)
                throw new Exception("Etiqueta: o servidor não retornou o ZPL (PRINT_ZEBRA_ARQ). Mensagens: " + string.Join("; ", r80.Messages));

            var res = new EtiquetaZpl();
            res.Zpl = pz.Params.ContainsKey("Texto") ? pz.Params["Texto"] : null;
            res.PrinterId = pz.Params.ContainsKey("Arquivo") ? pz.Params["Arquivo"] : "LPT2";
            res.Copies = 1;
            Logger.Log("Etiqueta: ZPL obtido (" + (res.Zpl != null ? res.Zpl.Length : 0) + " chars), impressora=" + res.PrinterId);

            // fecha o alerta CFG_WARNING_A (não crítico)
            try
            {
                string a81 = "<action name=\"CLOSE_ALERT\" kind=\"Action\" validation=\"false\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
                await PostAsync(MsgT("PARAMETROS", "CD_ESTOQUE", etq, a81, "<alert name=\"CFG_WARNING_A\"><selected>0</selected></alert>"));
            }
            catch { }

            return res;
        }

        // Baixa o PDF do comprovante usando a sessão autenticada e salva em destPath.
        public async Task<string> BaixarComprovanteAsync(string reportUri, string destPath)
        {
            // ConfigureAwait(false): defesa contra travar a UI caso este método seja
            // aguardado de forma síncrona (a UI já chama com await, isto é reforço).
            var resp = await _s.Http.GetAsync(reportUri).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            System.IO.File.WriteAllBytes(destPath, bytes);
            Logger.Log("Comprovante salvo em " + destPath + " (" + bytes.Length + " bytes)");
            return destPath;
        }

        // Clica "Dig. o Cód. do Prod." — começa a entrada de produtos; cria a linha ITMVTO_ESTOQUE.
        public async Task<MorphisResponse> IniciarProdutoAsync()
        {
            string action = "<action block=\"MVTO_ESTOQUE\" item=\"BTN_CD_PRODUTO\" name=\"btnCdProduto_click\" kind=\"Action\" validation=\"true\" recordValidation=\"false\" taskValidation=\"false\" validateNewRow=\"false\"></action>";
            string header = "<header><control block=\"ITMVTO_ESTOQUE\" item=\"DSP_CODIGO_DE_BARRAS\" task=\"" + FormTask + "\" isChanged=\"false\" modal=\"false\" isSuspended=\"false\">" + action + MonitorInfo() + "</control></header><body></body>";
            var r = await PostAsync(header);
            var blk = r.Block_("ITMVTO_ESTOQUE");
            if (blk != null) ItemRecordId = blk.Selected != null ? blk.Selected : (blk.Records.Count > 0 ? blk.Records[0].Id : ItemRecordId);
            Logger.Log("Morphis btnCdProduto: itemRec=" + ItemRecordId);
            return r;
        }

        // READ-ONLY: digita o produto e abre a LOV de lote; devolve os lotes disponíveis.
        // NÃO seleciona lote nem seta quantidade.
        public async Task<System.Collections.Generic.List<LoteOption>> LerLotesAsync(int cdProduto)
        {
            await GotoItemAsync("ITMVTO_ESTOQUE", "CD_PRODUTO", "CD_LOTE", ItemRecordId, "CD_PRODUTO", cdProduto.ToString());
            var lv = await ListValuesAsync("ITMVTO_ESTOQUE", "CD_LOTE");
            return ParseLotes(lv.List_("LV_LOTE_PRODUZIDO"));
        }

        public static System.Collections.Generic.List<LoteOption> ParseLotes(MorphisList lista)
        {
            var res = new System.Collections.Generic.List<LoteOption>();
            if (lista == null) return res;
            foreach (var rec in lista.Records)
            {
                var lo = new LoteOption();
                lo.CdLote = rec.Get("CD_LOTE");
                lo.Validade = ParseData(rec.Get("DT_VALIDADE"));
                decimal s; decimal.TryParse(rec.Get("QT_ESTOQUE_ATUAL"), out s); lo.SaldoAtual = s;
                if (!string.IsNullOrEmpty(lo.CdLote)) res.Add(lo);
            }
            return res;
        }

        private static DateTime? ParseData(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DateTime d;
            string[] fmts = { "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy" };
            if (DateTime.TryParseExact(s.Trim(), fmts, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d))
                return d;
            if (DateTime.TryParse(s, out d)) return d;
            return null;
        }

        // Extrai a fórmula do kit (lista de produtos) da resposta do AbrirKit.
        // Campos reais: CD_PRODUTO_TEM, DSP_DS_PRODUTO, QT_FORMULA, DSP_DS_UNIDADE.
        public static System.Collections.Generic.List<ItemFormula> LerFormula(MorphisResponse r)
        {
            var lista = new System.Collections.Generic.List<ItemFormula>();
            var blk = r.Block_("ITFORMULA");
            if (blk == null) return lista;
            foreach (var rec in blk.Records)
            {
                var it = new ItemFormula();
                int cd; int.TryParse(rec.Get("CD_PRODUTO_TEM"), out cd); it.CdProduto = cd;
                it.DsProduto = rec.Get("DSP_DS_PRODUTO");
                it.Unidade = rec.Get("DSP_DS_UNIDADE");
                decimal q; decimal.TryParse(rec.Get("QT_FORMULA"), out q); it.QtFormula = q;
                if (it.CdProduto != 0) lista.Add(it);
            }
            return lista;
        }

        public static string Xml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
