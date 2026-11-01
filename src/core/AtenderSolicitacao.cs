using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Uma solicitação de produtos como a aba Atender a mostra: cabeçalho e itens pedidos, lidos
    // da consulta C_SOLSAI_PRO (só leitura; ver MorphisClient.LerSolicitacaoParaAtenderAsync).
    public class SolicitacaoAtender
    {
        // Textos começam vazios (nunca null): pedido de setor não tem paciente, leito nem prestador.
        public long Numero;
        public string Tipo = "";         // TP_SOLSAI_PRO: P, S, G, E, T (pedidos); C, D, L (devoluções)
        public string Situacao = "";     // TP_SITUACAO: P pedido, C atend. parcial, S confirmado, A cancelado
        public int Estoque;
        public string DsEstoque = "";
        public string Setor = "";
        public string UnidadeInternacao = "";
        public string Leito = "";
        public string Paciente = "";
        public string Prestador = "";
        public string Atendimento = "";
        public string Solicitante = "";  // login de quem pediu (CD_USUARIO)
        public string SolicitanteNome = "";   // nome completo de quem pediu (só o M_BAIXASOL traz)
        public string Atendente = "";    // nome de quem atendeu por último (NM_USUARIO_MVTO do M_BAIXASOL)
        public DateTime? Data;           // data e hora do pedido
        public bool Urgente;
        public List<ItemSolicitado> Itens = new List<ItemSolicitado>();

        public bool Devolucao { get { return Tipo == "C" || Tipo == "D" || Tipo == "L"; } }

        // Pedido (P) e parcial (C) ainda têm o que atender; confirmada e cancelada, não.
        public bool Aberta { get { return Situacao == "P" || Situacao == "C"; } }

        public string TipoDescricao { get { return AtenderSolicitacao.DescreverTipo(Tipo); } }
        public string SituacaoDescricao { get { return AtenderSolicitacao.DescreverSituacao(Situacao); } }

        public ItemSolicitado Item(int produto)
        {
            foreach (var it in Itens) if (it.Produto == produto) return it;
            return null;
        }
    }

    // Um PRODUTO pedido: o mesmo produto em vários itens da solicitação (a agulha de cada
    // medicamento da prescrição) vem somado numa linha só (MorphisClient.LerSolicitacaoParaAtenderAsync).
    public class ItemSolicitado
    {
        public int Produto;
        public string Descricao;
        public string Unidade;
        public decimal Solicitado;       // soma dos itens do produto
        public decimal Atendido;         // já atendido em baixa anterior (solicitação parcial)
        public int ItensNoMv = 1;        // quantos itens da solicitação somam nesta linha

        public decimal Pendente { get { return Solicitado > Atendido ? Solicitado - Atendido : 0; } }
    }

    // Um lote com saldo no estoque, do relatório Conferência de Lotes (PdfConferenciaLotes).
    public class LoteDisponivel
    {
        public string Lote;
        public DateTime? Validade;
        public decimal Quantidade;       // saldo do lote no estoque ("Quantidade" do relatório)
        public decimal EmKit;            // parte montada em kit ("Qt Kit")

        public bool Vencido { get { return Validade.HasValue && Validade.Value.Date < DateTime.Today; } }
    }

    public class LotesProduto
    {
        public int Produto;
        public string Nome;
        public string Unidade;
        public decimal EstoqueAtual;
        public List<LoteDisponivel> Lotes = new List<LoteDisponivel>();

        public LoteDisponivel Achar(string lote)
        {
            string l = (lote ?? "").Trim();
            if (l.Length == 0) return null;
            foreach (var x in Lotes) if (string.Equals(x.Lote, l, StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }
    }

    // Uma linha que o atendente monta na grade: produto, lote e quantidade a atender. Outro lote
    // do mesmo produto é outra linha com o mesmo código (incluída pela linha vazia do fim).
    public class LinhaAtender
    {
        public int Produto;              // 0 = a linha vazia do fim, esperando um produto
        public string Descricao;
        public string Unidade;
        public bool ForaDoPedido;        // incluída pelo atendente; o produto não estava na solicitação
        public string Lote;
        public DateTime? Validade;
        public decimal? Quantidade;
        public string Kit;               // etiqueta do kit bipado (null = linha comum): lote e quantidade travados
        public bool KitInteiro;          // o pedido é do próprio produto kit: a linha é 1 kit (Lote = etiqueta)
        public string Etiqueta;          // etiqueta de PRODUTO bipada (lote da etiqueta; cada bipe soma EtiquetaPasso)
        public decimal EtiquetaPasso;
    }

    // Uma etiqueta (identificador 2000000...) como o cadastro "Identificador Etiqueta" do MV
    // (M_ID_ETIQUETA) a mostra: de produto (produto + lote + validade, ativa enquanto o lote é
    // usado) ou de kit (produto kit + movimentação da produção; inativa depois de baixado).
    public class EtiquetaMv
    {
        public string Codigo = "";       // o código de barras bipado
        public long Identificador;       // CD_IDENTIFICADOR (o código sem o "2" e sem o dígito final)
        public int Produto;
        public string Descricao = "";
        public string Unidade = "";
        public string Lote = "";
        public DateTime? Validade;
        public decimal QtdPadrao;        // QTD_PADRAO (vazio = 1 por bipe)
        public bool Ativa;               // SN_ATIVO
        public string Mvto = "";         // CD_MVTO_ESTOQUE (produção do kit)

        public bool DeKit { get { return Lote.Length == 0 && Mvto.Length > 0; } }
        public decimal Passo { get { return QtdPadrao > 0 ? QtdPadrao : 1; } }
    }

    // Um kit produzido na farmácia (etiqueta 2000000...), como a consulta do MV o devolve pelo
    // código de barras (MorphisClient.LerKitAsync): o kit e os componentes com lote e quantidade —
    // exatamente as linhas que o MV abre ao bipar a etiqueta na baixa.
    public class KitBipado
    {
        public string Barras = "";
        public int CdKit;                // produto do kit (ex.: 13490 KIT SONDA VESICAL DE DEMORA)
        public string Nome = "";
        public int Estoque;
        public DateTime? Validade;
        public List<ComponenteKit> Componentes = new List<ComponenteKit>();
    }

    public class ComponenteKit
    {
        public int Produto;
        public string Descricao = "";
        public string Unidade = "";
        public string Lote = "";
        public DateTime? Validade;
        public decimal Quantidade;
    }

    // O que o "Preencher automaticamente" fez.
    public class ResultadoPreencher
    {
        public int Preenchidos;          // itens que ganharam lote e quantidade
        public int ComMaisDeUmLote;
        public int JaDigitados;          // itens com alguma linha já digitada: não tocados
        public List<string> Parciais = new List<string>();   // "13780 (2 de 4)": o saldo fora de kit não deu
        public List<int> SemSaldo = new List<int>();         // nenhum lote com saldo fora de kit
        public int PrimeiraLinha = -1;   // primeira linha preenchida (para o cursor)
    }

    // Uma linha que o "Fechar a Baixa" lança no MV (já conferida).
    public class LinhaBaixa
    {
        public int Indice;               // linha da grade (para apontar o problema)
        public int Produto;
        public string Descricao = "";
        public string Unidade = "";      // a do pedido (ou da Posição de Estoque, se fora do pedido)
        public string Lote = "";
        public decimal Quantidade;
        public bool ForaDoPedido;        // o MV pergunta "não foi solicitado neste pedido" -> Sim
        public bool AMais;               // o produto vai a mais -> "maior que a solicitada" -> Sim
        public decimal Pendente;         // o que faltava atender do PRODUTO na solicitação (0 = fora do pedido)
        public KitBipado Kit;            // não nulo = bipar esta etiqueta (o MV abre as linhas do kit); o resto não vale
        public string Etiqueta;          // não nulo = bipar esta etiqueta de produto (Quantidade / EtiquetaPasso vezes)
        public decimal EtiquetaPasso = 1;
    }

    // O que a gravação deu.
    public class ResultadoBaixa
    {
        public bool Gravada;             // o MV respondeu "Solicitação atendida com sucesso!"
        public bool Incerta;             // o Fechar saiu e a resposta não deu certeza: conferir ANTES de qualquer coisa
        public bool AntesDoFechar = true;   // parou no lançamento: nada gravado, o Retornar descarta
        public string Situacao = "";     // TP_SITUACAO depois de gravar (S confirmada, C parcial)
        public string Mensagem = "";
        public int LinhaComProblema = -1;   // Indice da LinhaBaixa
    }

    public enum SituacaoConferida { Completo, Parcial, AMais, ForaDoPedido, NaoAtendido }

    // Um produto na tabela da conferência: o pedido contra o que esta baixa entrega.
    public class ItemConferido
    {
        public int Produto;
        public string Descricao = "";
        public string Unidade = "";
        public decimal Solicitado;       // 0 = fora do pedido
        public decimal JaAtendido;       // em baixa anterior (solicitação parcial)
        public decimal Pendente;         // o que faltava antes desta baixa
        public decimal Atendido;         // nesta baixa (soma das linhas)
        public List<KeyValuePair<string, decimal>> Lotes = new List<KeyValuePair<string, decimal>>();   // lote -> quantidade
        public SituacaoConferida Situacao;

        public decimal Diferenca { get { return Atendido - Pendente; } }
    }

    // O que a conferência antes do "Fechar a Baixa" encontrou.
    public class ResumoAtender
    {
        public List<string> Erros = new List<string>();     // impedem fechar
        public int PrimeiraLinhaComErro = -1;
        public int Linhas;                                  // linhas com quantidade
        public List<ItemConferido> Itens = new List<ItemConferido>();   // ordem da impressa; fora do pedido no fim

        public int Conta(SituacaoConferida s)
        {
            int n = 0;
            foreach (var it in Itens) if (it.Situacao == s) n++;
            return n;
        }
    }

    // Regras e caches da aba Atender. A LEITURA fala com o servidor de forms: quem chama segura
    // o MvGate. Nada aqui grava no MV.
    public static class AtenderSolicitacao
    {
        public const int MinutosCacheLotes = 3;       // saldo de lote muda a cada atendimento
        public const int MinutosCacheProdutos = 10;   // a lista do F9 de produtos (Posição de Estoque)
        public const int DiasVencendo = 90;           // validade a menos que isso: realce na grade

        public static string DescreverTipo(string t)
        {
            switch (t)
            {
                case "P": return "Pedido - Paciente";
                case "S": return "Pedido - Setores";
                case "G": return "Pedido - Gasto Sala";
                case "E": return "Pedido - Estoque";
                case "T": return "Pedido - Empresa";
                case "C": return "Devol - Paciente";
                case "D": return "Devol - Setor";
                case "L": return "Devol - Gasto Sala";
                default: return string.IsNullOrEmpty(t) ? "" : t;
            }
        }

        public static string DescreverSituacao(string s)
        {
            switch (s)
            {
                case "P": return "Pendente";
                case "C": return "Atendida parcialmente";
                case "S": return "Confirmada";
                case "A": return "Cancelada";
                default: return string.IsNullOrEmpty(s) ? "" : s;
            }
        }

        // Os dois primeiros nomes, levando junto a partícula do meio: "ANA GABRIELA ROCHA ..." ->
        // "ANA GABRIELA"; "MARIA DE LOURDES ..." -> "MARIA DE LOURDES".
        private static readonly string[] Particulas = { "DE", "DA", "DO", "DAS", "DOS", "E", "D'" };

        public static string DoisNomes(string nome)
        {
            var p = (nome ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length <= 2) return string.Join(" ", p);
            var r = new List<string>();
            r.Add(p[0]);
            for (int i = 1; i < p.Length; i++)
            {
                r.Add(p[i]);
                if (Array.IndexOf(Particulas, p[i].ToUpperInvariant()) < 0) break;
            }
            return string.Join(" ", r.ToArray());
        }

        // A solicitação impressa (R_SOLSAI_PRO) lista os itens em ordem ALFABÉTICA da descrição —
        // conferido em 28/10/2026 num pedido de setor (484416, 20 itens) e num de paciente
        // (484357); a consulta C_SOLSAI_PRO devolve pelo código. Desempate pelo código.
        public static void OrdenarComoImpressa(SolicitacaoAtender sol)
        {
            if (sol == null) return;
            sol.Itens.Sort(delegate(ItemSolicitado a, ItemSolicitado b)
            {
                int c = string.CompareOrdinal((a.Descricao ?? "").ToUpperInvariant(), (b.Descricao ?? "").ToUpperInvariant());
                return c != 0 ? c : a.Produto.CompareTo(b.Produto);
            });
        }

        // ===== CACHES (sem dado de paciente, mas somem no logout com o resto) =====
        private class Lido<T> { public DateTime Quando; public T Valor; }
        private static readonly Dictionary<string, Lido<LotesProduto>> _lotes = new Dictionary<string, Lido<LotesProduto>>();
        private static readonly Dictionary<int, Lido<List<SaldoProduto>>> _produtos = new Dictionary<int, Lido<List<SaldoProduto>>>();

        public static void LimparCache()
        {
            lock (_lotes) _lotes.Clear();
            lock (_produtos) _produtos.Clear();
            lock (_kitsEmEstoque) _kitsEmEstoque.Clear();
        }

        // Etiquetas de kit em estoque, pela Consulta de Kits (R_KIT_PROD) de um produto que o kit
        // tem: a consulta por etiqueta acha também kit já baixado, e o MV só recusa na hora de bipar
        // ("não foi produzido ou já foi movimentado"). ~1–2 s; vale 2 minutos.
        private static readonly Dictionary<string, Lido<HashSet<string>>> _kitsEmEstoque = new Dictionary<string, Lido<HashSet<string>>>();

        public static async Task<bool> KitEmEstoqueAsync(Session s, KitBipado kit, int estoque, bool forcar)
        {
            if (kit.Componentes.Count == 0) return false;
            int produto = kit.Componentes[0].Produto;
            string chave = Chave(estoque, produto);
            HashSet<string> codigos = null;
            lock (_kitsEmEstoque)
            {
                Lido<HashSet<string>> l;
                if (!forcar && _kitsEmEstoque.TryGetValue(chave, out l) && (DateTime.Now - l.Quando).TotalMinutes < 2) codigos = l.Valor;
            }
            if (codigos == null)
            {
                var res = await MorphisClient.ConsultarKitsCompletoAsync(s, estoque, produto);
                codigos = new HashSet<string>();
                foreach (var k in res.Kits) foreach (var u in k.Unidades) if (!string.IsNullOrEmpty(u.CodBarras)) codigos.Add(u.CodBarras.Trim());
                lock (_kitsEmEstoque) { var l = new Lido<HashSet<string>>(); l.Quando = DateTime.Now; l.Valor = codigos; _kitsEmEstoque[chave] = l; }
            }
            return codigos.Contains(kit.Barras);
        }

        private static string Chave(int estoque, int produto) { return estoque + ":" + produto; }

        // Lotes já lidos e ainda frescos (null = ler de novo).
        public static LotesProduto LotesEmCache(int estoque, int produto)
        {
            lock (_lotes)
            {
                Lido<LotesProduto> l;
                if (_lotes.TryGetValue(Chave(estoque, produto), out l) && (DateTime.Now - l.Quando).TotalMinutes < MinutosCacheLotes)
                    return l.Valor;
                return null;
            }
        }

        // Lotes de vários produtos num relatório só (R_CONF_LOTE). Produto sem saldo não sai no
        // relatório: volta com a lista de lotes vazia. forcar = ignora o cache (abrir de novo a
        // solicitação relê o saldo).
        public static async Task<Dictionary<int, LotesProduto>> LotesAsync(Session s, int estoque, IEnumerable<int> produtos, bool forcar)
        {
            var res = new Dictionary<int, LotesProduto>();
            var faltam = new List<int>();
            foreach (int p in produtos)
            {
                if (p <= 0 || res.ContainsKey(p) || faltam.Contains(p)) continue;
                var c = forcar ? null : LotesEmCache(estoque, p);
                if (c != null) res[p] = c; else faltam.Add(p);
            }
            if (faltam.Count == 0) return res;

            var lidos = await MorphisClient.LerLotesDoEstoqueAsync(s, estoque, faltam);
            DateTime agora = DateTime.Now;
            lock (_lotes)
            {
                foreach (int p in faltam)
                {
                    LotesProduto lp;
                    if (!lidos.TryGetValue(p, out lp)) { lp = new LotesProduto(); lp.Produto = p; }
                    var l = new Lido<LotesProduto>(); l.Quando = agora; l.Valor = lp;
                    _lotes[Chave(estoque, p)] = l;
                    res[p] = lp;
                }
            }
            return res;
        }

        public static List<SaldoProduto> ProdutosEmCache(int estoque)
        {
            lock (_produtos)
            {
                Lido<List<SaldoProduto>> l;
                if (_produtos.TryGetValue(estoque, out l) && (DateTime.Now - l.Quando).TotalMinutes < MinutosCacheProdutos)
                    return l.Valor;
                return null;
            }
        }

        // Produtos com saldo no estoque, para o F9 de "Incluir produto": a Posição de Estoque
        // (R_POS_EST_S) dá código, nome, unidade e saldo de todos — 446 na Farmácia Central em
        // 28/10/2026, ~3 s. A lista de produtos do MV (LV_PRODUTO2) só existe dentro da baixa,
        // depois de "Confirmar produtos", que já trava a solicitação.
        public static async Task<List<SaldoProduto>> ProdutosAsync(Session s, int estoque)
        {
            var c = ProdutosEmCache(estoque);
            if (c != null) return c;
            var pos = await MorphisClient.GerarPosicaoPorEstoqueAsync(s, estoque, 0, 0);
            var lista = new List<SaldoProduto>(pos.Produtos);
            lista.Sort(delegate(SaldoProduto a, SaldoProduto b) { return string.Compare(a.Nome, b.Nome, StringComparison.OrdinalIgnoreCase); });
            lock (_produtos)
            {
                var l = new Lido<List<SaldoProduto>>(); l.Quando = DateTime.Now; l.Valor = lista;
                _produtos[estoque] = l;
            }
            return lista;
        }

        // ===== PREENCHER AUTOMATICAMENTE =====
        // Pedido do usuário (01/11/2026), revendo o "não sugerir lote nem quantidade" de 28/10:
        // só quando o atendente pede. Cada item pedido sem nada digitado recebe o lote de MAIOR
        // saldo fora de kit (saldo − em kit; vencido e lote todo em kit ficam de fora — o MV também
        // esconde este) e, se não bastar, o próximo, em outra linha do mesmo produto. Faltando
        // saldo, preenche o que tem e o item fica parcial. Produto com alguma linha já digitada,
        // fora do pedido ou sem pendência não é tocado. Usa as linhas vazias do produto que já
        // existem; as que faltam entram logo abaixo delas.
        public static ResultadoPreencher Preencher(SolicitacaoAtender sol, List<LinhaAtender> linhas, Func<int, LotesProduto> lotes)
        {
            var r = new ResultadoPreencher();
            if (sol == null) return r;

            // Produtos na ordem da grade, com as linhas de cada um. Linha de kit bipado não é
            // reaproveitada nem conta como digitada: o que o kit já cobre sai do que falta.
            var ordem = new List<int>();
            var linhasDo = new Dictionary<int, List<int>>();
            var comunsDo = new Dictionary<int, List<int>>();
            var doKit = new Dictionary<int, decimal>();
            for (int i = 0; i < linhas.Count; i++)
            {
                int p = linhas[i].Produto;
                if (p <= 0) continue;
                List<int> ls;
                if (!linhasDo.TryGetValue(p, out ls)) { ls = new List<int>(); linhasDo[p] = ls; comunsDo[p] = new List<int>(); ordem.Add(p); }
                ls.Add(i);
                if (linhas[i].Kit == null) comunsDo[p].Add(i);
                else { decimal k; doKit.TryGetValue(p, out k); doKit[p] = k + (linhas[i].Quantidade ?? 0); }
            }

            // Quanto de cada lote vai em cada produto (sem mexer na lista ainda).
            var plano = new Dictionary<int, List<KeyValuePair<LoteDisponivel, decimal>>>();
            foreach (int p in ordem)
            {
                var it = sol.Item(p);
                if (it == null || it.Pendente <= 0) continue;
                bool digitado = false;
                foreach (int i in comunsDo[p])
                    if (linhas[i].Quantidade.HasValue || !string.IsNullOrEmpty(linhas[i].Lote)) digitado = true;
                if (digitado) { r.JaDigitados++; continue; }
                decimal cobertoPorKit; doKit.TryGetValue(p, out cobertoPorKit);
                if (cobertoPorKit >= it.Pendente) continue;

                var lp = lotes(p);
                var livres = new List<LoteDisponivel>();
                if (lp != null)
                    foreach (var lo in lp.Lotes) if (!lo.Vencido && Livre(lo) > 0) livres.Add(lo);
                livres.Sort(delegate(LoteDisponivel a, LoteDisponivel b)
                {
                    int c = Livre(b).CompareTo(Livre(a));   // maior saldo fora de kit primeiro
                    if (c != 0) return c;
                    DateTime va = a.Validade.HasValue ? a.Validade.Value : DateTime.MaxValue;
                    DateTime vb = b.Validade.HasValue ? b.Validade.Value : DateTime.MaxValue;
                    return va.CompareTo(vb);                // empate: vence antes
                });
                if (livres.Count == 0) { r.SemSaldo.Add(p); continue; }

                var uso = new List<KeyValuePair<LoteDisponivel, decimal>>();
                decimal alvo = it.Pendente - cobertoPorKit;
                decimal falta = alvo;
                foreach (var lo in livres)
                {
                    if (falta <= 0) break;
                    decimal q = Math.Min(falta, Livre(lo));
                    uso.Add(new KeyValuePair<LoteDisponivel, decimal>(lo, q));
                    falta -= q;
                }
                plano[p] = uso;
                r.Preenchidos++;
                if (uso.Count > 1) r.ComMaisDeUmLote++;
                if (falta > 0) r.Parciais.Add(p + " (" + Num(alvo - falta) + " de " + Num(alvo) + ")");
            }

            // Aplica de baixo para cima: linha inserida não desloca as de produtos acima.
            for (int k = ordem.Count - 1; k >= 0; k--)
            {
                List<KeyValuePair<LoteDisponivel, decimal>> uso;
                if (!plano.TryGetValue(ordem[k], out uso)) continue;
                var ls = comunsDo[ordem[k]];
                var todas = linhasDo[ordem[k]];
                var modelo = linhas[todas[0]];
                int depois = todas[todas.Count - 1] + 1;
                for (int j = 0; j < uso.Count; j++)
                {
                    LinhaAtender l;
                    if (j < ls.Count) l = linhas[ls[j]];
                    else
                    {
                        l = new LinhaAtender();
                        l.Produto = modelo.Produto; l.Descricao = modelo.Descricao; l.Unidade = modelo.Unidade; l.ForaDoPedido = modelo.ForaDoPedido;
                        linhas.Insert(depois++, l);
                    }
                    l.Lote = uso[j].Key.Lote; l.Validade = uso[j].Key.Validade; l.Quantidade = uso[j].Value;
                }
            }
            for (int i = 0; i < linhas.Count && r.PrimeiraLinha < 0; i++)
                if (linhas[i].Produto > 0 && plano.ContainsKey(linhas[i].Produto)) r.PrimeiraLinha = i;
            return r;
        }

        // Saldo do lote que não está montado em kit.
        public static decimal Livre(LoteDisponivel lo) { return lo.Quantidade > lo.EmKit ? lo.Quantidade - lo.EmKit : 0; }

        // ===== KIT BIPADO (etiqueta 2000000...) =====
        // Pedido do usuário (01/11/2026): bipar a etiqueta do kit num campo ao lado do "Preencher
        // automaticamente"; os itens do kit entram SOB cada produto, como outro lote, travados.
        // Dois jeitos de pedir um kit (vistos no MV): o pedido do próprio produto kit (486025: 13490
        // KIT SONDA VESICAL DE DEMORA) -> UMA linha do kit sob ele (Lote = etiqueta, Atender 1); ou
        // o pedido dos componentes (485945: seringa, agulha, água...) -> cada componente sob o
        // produto dele. Componente fora do pedido entra no fim, como fora do pedido. Kit que não
        // tem nada a ver com a solicitação não entra. null = incluído; senão, o motivo.
        public static string IncluirKit(SolicitacaoAtender sol, List<LinhaAtender> linhas, KitBipado kit)
        {
            if (sol == null || kit == null) return "Nenhuma solicitação aberta.";
            foreach (var l in linhas) if (l.Kit == kit.Barras) return "Esse kit já foi bipado.";
            if (kit.Estoque > 0 && sol.Estoque > 0 && kit.Estoque != sol.Estoque) return "Esse kit é de outro estoque.";
            if (kit.Componentes.Count == 0) return "O MV não trouxe os itens desse kit.";

            var pedidoKit = sol.Item(kit.CdKit);
            if (pedidoKit != null)
            {
                var l = new LinhaAtender();
                l.Produto = kit.CdKit; l.Descricao = pedidoKit.Descricao; l.Unidade = pedidoKit.Unidade;
                l.Lote = kit.Barras; l.Validade = kit.Validade; l.Quantidade = 1;
                l.Kit = kit.Barras; l.KitInteiro = true;
                linhas.Insert(PosicaoSob(linhas, kit.CdKit), l);
                return null;
            }
            bool algum = false;
            foreach (var c in kit.Componentes) if (sol.Item(c.Produto) != null) algum = true;
            if (!algum) return "Esse kit (" + kit.Nome + ") não faz parte desta solicitação.";
            foreach (var c in kit.Componentes)
            {
                var it = sol.Item(c.Produto);
                var l = new LinhaAtender();
                l.Produto = c.Produto;
                l.Descricao = it != null ? it.Descricao : c.Descricao;
                l.Unidade = it != null ? it.Unidade : c.Unidade;
                l.Lote = c.Lote; l.Validade = c.Validade; l.Quantidade = c.Quantidade;
                l.Kit = kit.Barras; l.ForaDoPedido = it == null;
                linhas.Insert(PosicaoSob(linhas, c.Produto), l);
            }
            return null;
        }

        // Logo abaixo da última linha do produto; sem linha dele, antes da linha vazia do fim.
        private static int PosicaoSob(List<LinhaAtender> linhas, int produto)
        {
            int pos = -1;
            for (int i = 0; i < linhas.Count; i++) if (linhas[i].Produto == produto) pos = i + 1;
            if (pos >= 0) return pos;
            int fim = linhas.Count;
            if (fim > 0 && linhas[fim - 1].Produto <= 0) fim--;
            return fim;
        }

        // Tira todas as linhas de um kit. Devolve quantas saíram.
        public static int RemoverKit(List<LinhaAtender> linhas, string barras)
        {
            return linhas.RemoveAll(delegate(LinhaAtender l) { return l.Kit != null && l.Kit == barras; });
        }

        // Etiqueta interna do MV (kits e produtos): EAN-13 = "2" + identificador em 11 dígitos +
        // dígito verificador (2000000211633 = identificador 21163; conferido em 01/11/2026).
        public static bool PareceEtiqueta(string codigo)
        {
            return IdentificadorDaEtiqueta(codigo) > 0;
        }

        // 0 = não é etiqueta do MV (tamanho, prefixo ou dígito verificador errado).
        public static long IdentificadorDaEtiqueta(string codigo)
        {
            string c = (codigo ?? "").Trim();
            if (c.Length != 13 || c[0] != '2') return 0;
            int soma = 0;
            for (int i = 0; i < 13; i++)
            {
                if (!char.IsDigit(c[i])) return 0;
                if (i < 12) soma += (c[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }
            if ((10 - soma % 10) % 10 != c[12] - '0') return 0;
            long id;
            return long.TryParse(c.Substring(1, 11), out id) ? id : 0;
        }

        // Etiqueta de PRODUTO bipada: cada bipe soma o passo da etiqueta (1, salvo QTD_PADRAO) na
        // linha daquela etiqueta. A 1ª vai na linha vazia do produto, se houver (fica a linha do
        // pedido com lote e quantidade); senão, numa linha nova logo abaixo. Como no MV, o bipe
        // repetido soma na mesma linha (teste na 486025, 01/11/2026). Devolve a linha.
        public static int AdicionarEtiqueta(SolicitacaoAtender sol, List<LinhaAtender> linhas, EtiquetaMv et, bool foraDoPedido)
        {
            for (int i = 0; i < linhas.Count; i++)
                if (linhas[i].Etiqueta == et.Codigo)
                {
                    linhas[i].Quantidade = (linhas[i].Quantidade ?? 0) + et.Passo;
                    return i;
                }
            var it = sol.Item(et.Produto);
            for (int i = 0; i < linhas.Count; i++)
            {
                var l = linhas[i];
                if (l.Produto != et.Produto || l.Kit != null || l.Etiqueta != null) continue;
                if (l.Quantidade.HasValue || !string.IsNullOrEmpty(l.Lote)) continue;
                l.Lote = et.Lote; l.Validade = et.Validade; l.Quantidade = et.Passo;
                l.Etiqueta = et.Codigo; l.EtiquetaPasso = et.Passo;
                return i;
            }
            var n = new LinhaAtender();
            n.Produto = et.Produto;
            n.Descricao = it != null ? it.Descricao : et.Descricao;
            n.Unidade = it != null ? it.Unidade : et.Unidade;
            n.ForaDoPedido = foraDoPedido && it == null;
            n.Lote = et.Lote; n.Validade = et.Validade; n.Quantidade = et.Passo;
            n.Etiqueta = et.Codigo; n.EtiquetaPasso = et.Passo;
            int pos = PosicaoSob(linhas, et.Produto);
            linhas.Insert(pos, n);
            return pos;
        }

        // Tira a etiqueta: a linha do pedido volta a ficar vazia; linha a mais sai.
        public static void RemoverEtiqueta(List<LinhaAtender> linhas, int indice)
        {
            var l = linhas[indice];
            int doProduto = 0;
            foreach (var x in linhas) if (x.Produto == l.Produto) doProduto++;
            if (doProduto == 1 && !l.ForaDoPedido)
            {
                l.Lote = null; l.Validade = null; l.Quantidade = null; l.Etiqueta = null; l.EtiquetaPasso = 0;
            }
            else linhas.RemoveAt(indice);
        }

        // ===== CONFERÊNCIA ANTES DE FECHAR =====
        // lotes(produto) devolve os lotes lidos (null = não lidos). Linhas sem lote e sem
        // quantidade são itens que o atendente deixou de atender.
        public static ResumoAtender Conferir(SolicitacaoAtender sol, List<LinhaAtender> linhas, Func<int, LotesProduto> lotes)
        {
            var r = new ResumoAtender();
            var usadoPorLote = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var totalPorProduto = new Dictionary<int, decimal>();
            var lotesPorProduto = new Dictionary<int, List<string>>();   // na ordem das linhas
            var foraDoPedido = new List<LinhaAtender>();                  // 1ª linha de cada produto fora do pedido

            for (int i = 0; i < linhas.Count; i++)
            {
                var l = linhas[i];
                bool temQtd = l.Quantidade.HasValue && l.Quantidade.Value > 0;
                bool temLote = !string.IsNullOrEmpty((l.Lote ?? "").Trim());
                if (!temQtd && !temLote) continue;
                string onde = "Linha " + (i + 1) + (l.Produto > 0 ? " (" + l.Produto + ")" : "") + ": ";

                if (l.Produto <= 0) { Erro(r, i, onde + "escolha o produto (F9)."); continue; }
                if (temQtd && temLote && l.Kit != null)
                {
                    // Linha de kit bipado: lote e saldo o MV confere ao bipar a etiqueta (o saldo
                    // desses lotes está "em kit"). Conta no atendido do produto como as outras.
                    // Na conferência o "lote" de item de kit é o kit (fim da etiqueta): é o que o atendente tem na mão.
                    string nomeLote = "kit " + (l.Kit.Length > 6 ? l.Kit.Substring(l.Kit.Length - 6) : l.Kit);
                    string chaveKit = l.Produto + "|" + nomeLote;
                    decimal usadoKit; usadoPorLote.TryGetValue(chaveKit, out usadoKit);
                    usadoPorLote[chaveKit] = usadoKit + l.Quantidade.Value;
                    decimal totKit; totalPorProduto.TryGetValue(l.Produto, out totKit);
                    if (totKit == 0 && sol.Item(l.Produto) == null) foraDoPedido.Add(l);
                    totalPorProduto[l.Produto] = totKit + l.Quantidade.Value;
                    List<string> lsKit;
                    if (!lotesPorProduto.TryGetValue(l.Produto, out lsKit)) { lsKit = new List<string>(); lotesPorProduto[l.Produto] = lsKit; }
                    if (!lsKit.Contains(nomeLote)) lsKit.Add(nomeLote);
                    r.Linhas++;
                    continue;
                }
                if (!temQtd) { Erro(r, i, onde + "digite a quantidade a atender."); continue; }
                if (!temLote) { Erro(r, i, onde + "escolha o lote (F9)."); continue; }

                var lp = lotes(l.Produto);
                var lote = lp != null ? lp.Achar(l.Lote) : null;
                if (lp == null) { Erro(r, i, onde + "os lotes do produto ainda não foram lidos."); continue; }
                if (lote == null) { Erro(r, i, onde + "o lote " + l.Lote.Trim() + " não tem saldo neste estoque."); continue; }
                if (lote.Vencido) { Erro(r, i, onde + "o lote " + lote.Lote + " está vencido."); continue; }

                string chave = l.Produto + "|" + lote.Lote;
                decimal usado; usadoPorLote.TryGetValue(chave, out usado);
                usado += l.Quantidade.Value;
                usadoPorLote[chave] = usado;
                if (usado > lote.Quantidade)
                    Erro(r, i, onde + "a quantidade passa do saldo do lote " + lote.Lote + " (" + Num(lote.Quantidade) + ").");

                decimal tot; totalPorProduto.TryGetValue(l.Produto, out tot);
                if (tot == 0 && sol.Item(l.Produto) == null) foraDoPedido.Add(l);
                totalPorProduto[l.Produto] = tot + l.Quantidade.Value;
                List<string> ls;
                if (!lotesPorProduto.TryGetValue(l.Produto, out ls)) { ls = new List<string>(); lotesPorProduto[l.Produto] = ls; }
                if (!ls.Contains(lote.Lote)) ls.Add(lote.Lote);
                r.Linhas++;
            }

            if (r.Linhas == 0 && r.Erros.Count == 0) r.Erros.Add("Nenhuma linha com lote e quantidade: não há o que atender.");

            foreach (var it in sol.Itens)
            {
                var c = Conferido(it.Produto, it.Descricao, it.Unidade, totalPorProduto, lotesPorProduto, usadoPorLote);
                c.Solicitado = it.Solicitado; c.JaAtendido = it.Atendido; c.Pendente = it.Pendente;
                if (c.Atendido <= 0) c.Situacao = SituacaoConferida.NaoAtendido;
                else if (c.Atendido < it.Pendente) c.Situacao = SituacaoConferida.Parcial;
                else if (c.Atendido > it.Pendente) c.Situacao = SituacaoConferida.AMais;
                else c.Situacao = SituacaoConferida.Completo;
                r.Itens.Add(c);
            }
            foreach (var l in foraDoPedido)
            {
                var c = Conferido(l.Produto, l.Descricao, l.Unidade, totalPorProduto, lotesPorProduto, usadoPorLote);
                c.Situacao = SituacaoConferida.ForaDoPedido;
                r.Itens.Add(c);
            }
            return r;
        }

        // As linhas que vão para o MV, na ordem da grade (a da impressa): só as com lote e
        // quantidade. A conferência diz quais produtos vão a mais (o MV pergunta e a aba confirma).
        public static List<LinhaBaixa> LinhasParaGravar(SolicitacaoAtender sol, List<LinhaAtender> linhas, ResumoAtender conferencia)
        {
            return LinhasParaGravar(sol, linhas, conferencia, null);
        }

        // kits: as etiquetas bipadas (o que a consulta devolveu). Cada kit vira UMA LinhaBaixa
        // (bipar a etiqueta), na posição da 1ª linha dele; o MV abre as linhas do kit sozinho.
        public static List<LinhaBaixa> LinhasParaGravar(SolicitacaoAtender sol, List<LinhaAtender> linhas, ResumoAtender conferencia,
            Dictionary<string, KitBipado> kits)
        {
            var aMais = new HashSet<int>();
            foreach (var it in conferencia.Itens) if (it.Situacao == SituacaoConferida.AMais) aMais.Add(it.Produto);
            var res = new List<LinhaBaixa>();
            var kitsVistos = new HashSet<string>();
            for (int i = 0; i < linhas.Count; i++)
            {
                var l = linhas[i];
                if (l.Kit != null)
                {
                    KitBipado kit;
                    if (!kitsVistos.Add(l.Kit)) continue;
                    if (kits == null || !kits.TryGetValue(l.Kit, out kit)) throw new InvalidOperationException("O kit " + l.Kit + " da linha " + (i + 1) + " não foi lido.");
                    var bk = new LinhaBaixa();
                    bk.Indice = i; bk.Kit = kit; bk.Produto = kit.CdKit; bk.Descricao = kit.Nome; bk.Lote = kit.Barras; bk.Quantidade = 1;
                    res.Add(bk);
                    continue;
                }
                if (l.Produto <= 0 || !l.Quantidade.HasValue || l.Quantidade.Value <= 0 || string.IsNullOrEmpty((l.Lote ?? "").Trim())) continue;
                var b = new LinhaBaixa();
                b.Indice = i; b.Produto = l.Produto; b.Descricao = l.Descricao ?? ""; b.Unidade = l.Unidade ?? "";
                b.Lote = l.Lote.Trim(); b.Quantidade = l.Quantidade.Value;
                var it = sol.Item(l.Produto);
                b.ForaDoPedido = l.ForaDoPedido || it == null;
                b.AMais = aMais.Contains(l.Produto);
                b.Pendente = it != null ? it.Pendente : 0;
                if (l.Etiqueta != null) { b.Etiqueta = l.Etiqueta; b.EtiquetaPasso = l.EtiquetaPasso > 0 ? l.EtiquetaPasso : 1; }
                res.Add(b);
            }
            return res;
        }

        // O quadro de divergência que o MV mostra no "Fechar" bate com a baixa lançada? null = bate;
        // senão, o motivo. O quadro lista só os ITENS da solicitação com diferença (o item
        // atendido certinho não aparece), um registro por item: o produto pedido em 3 itens dos
        // quais a baixa atendeu 1 aparece 2 vezes com 0 atendido (485907, 01/11/2026). Por isso a
        // conta é por produto: a soma das diferenças (solicitado − atendido) dos registros dele tem
        // que dar o pendente − o lançado (fora do pedido: pendente 0; a mais: negativa). Produto
        // do quadro que a baixa não lançou: só confere que o MV não o deu como atendido.
        // lancado e pendente: por produto, das linhas lançadas (LinhaBaixa.Quantidade/Pendente).
        public static string ConferirQuadro(List<MorphisRecord> quadro, Dictionary<int, decimal> lancado, Dictionary<int, decimal> pendente)
        {
            return ConferirQuadro(quadro, lancado, pendente, null);
        }

        // porKit: o que os kits bipados lançaram de cada componente. No quadro o MV NÃO conta o
        // que veio por kit (01/11/2026: no HAR da 485945 e no teste da 486025 todos os componentes
        // do kit apareceram com 0 atendido; depois do OK a 485945 ficou confirmada). Aceita também a
        // conta com o kit, se um dia o MV passar a mostrá-lo.
        public static string ConferirQuadro(List<MorphisRecord> quadro, Dictionary<int, decimal> lancado, Dictionary<int, decimal> pendente,
            Dictionary<int, decimal> porKit)
        {
            if (porKit == null) porKit = new Dictionary<int, decimal>();
            var diferenca = new Dictionary<int, decimal>();
            var atendido = new Dictionary<int, decimal>();
            foreach (var rq in quadro)
            {
                int cod;
                if (!int.TryParse(Limpo(rq.Get("CD_PRODUTO")), out cod)) continue;
                var qs = LerNumero(Limpo(rq.Get("QTD_SOLICITADA")));
                var qa = LerNumero(Limpo(rq.Get("QTD_ATENDIDA")));
                if (!qs.HasValue || !qa.HasValue) return "o quadro do MV veio sem a quantidade do produto " + cod;
                decimal d; diferenca.TryGetValue(cod, out d); diferenca[cod] = d + qs.Value - qa.Value;
                decimal a; atendido.TryGetValue(cod, out a); atendido[cod] = a + qa.Value;
            }
            foreach (var kv in atendido)
            {
                decimal k; porKit.TryGetValue(kv.Key, out k);
                if (!lancado.ContainsKey(kv.Key) && kv.Value != 0 && kv.Value != k)
                    return "o MV mostra " + Num(kv.Value) + " atendido(s) do produto " + kv.Key + ", que a baixa não lançou";
            }
            foreach (var kv in lancado)
            {
                decimal p; pendente.TryGetValue(kv.Key, out p);
                decimal d; diferenca.TryGetValue(kv.Key, out d);
                decimal k; porKit.TryGetValue(kv.Key, out k);
                decimal esperado = p - kv.Value;
                if (d != esperado && !(k > 0 && d == esperado - k))
                    return "no produto " + kv.Key + " o MV mostra diferença de " + Num(d) + " e a baixa esperava " + Num(esperado) +
                        " (pendente " + Num(p) + ", lançado " + Num(kv.Value) + ")";
            }
            return null;
        }

        private static string Limpo(string v)
        {
            v = (v ?? "").Trim();
            return v == "xsi:nil" ? "" : v;
        }

        private static ItemConferido Conferido(int produto, string descricao, string unidade, Dictionary<int, decimal> total,
            Dictionary<int, List<string>> lotes, Dictionary<string, decimal> usadoPorLote)
        {
            var c = new ItemConferido();
            c.Produto = produto; c.Descricao = descricao ?? ""; c.Unidade = unidade ?? "";
            total.TryGetValue(produto, out c.Atendido);
            List<string> ls;
            if (lotes.TryGetValue(produto, out ls))
                foreach (var lo in ls)
                {
                    decimal q; usadoPorLote.TryGetValue(produto + "|" + lo, out q);
                    c.Lotes.Add(new KeyValuePair<string, decimal>(lo, q));
                }
            return c;
        }

        private static void Erro(ResumoAtender r, int linha, string msg)
        {
            r.Erros.Add(msg);
            if (r.PrimeiraLinhaComErro < 0) r.PrimeiraLinhaComErro = linha;
        }

        // 200 -> "200"; 1,5 -> "1,5" (quantidade como o atendente digita).
        public static string Num(decimal v)
        {
            return v.ToString(v == decimal.Truncate(v) ? "0" : "0.###", new CultureInfo("pt-BR"));
        }

        // "200", "1,5", "1.5", "1.500,000" -> decimal (null = não é número).
        public static decimal? LerNumero(string s)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0) return null;
            if (t.Contains(",")) t = t.Replace(".", "").Replace(",", ".");
            decimal v;
            if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out v)) return v;
            return null;
        }
    }
}
