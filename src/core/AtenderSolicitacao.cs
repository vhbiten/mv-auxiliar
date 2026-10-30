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

    public class ItemSolicitado
    {
        public int Produto;
        public string Descricao;
        public string Unidade;
        public decimal Solicitado;
        public decimal Atendido;         // já atendido em baixa anterior (solicitação parcial)

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
            var aMais = new HashSet<int>();
            foreach (var it in conferencia.Itens) if (it.Situacao == SituacaoConferida.AMais) aMais.Add(it.Produto);
            var res = new List<LinhaBaixa>();
            for (int i = 0; i < linhas.Count; i++)
            {
                var l = linhas[i];
                if (l.Produto <= 0 || !l.Quantidade.HasValue || l.Quantidade.Value <= 0 || string.IsNullOrEmpty((l.Lote ?? "").Trim())) continue;
                var b = new LinhaBaixa();
                b.Indice = i; b.Produto = l.Produto; b.Descricao = l.Descricao ?? ""; b.Unidade = l.Unidade ?? "";
                b.Lote = l.Lote.Trim(); b.Quantidade = l.Quantidade.Value;
                b.ForaDoPedido = l.ForaDoPedido || sol.Item(l.Produto) == null;
                b.AMais = aMais.Contains(l.Produto);
                res.Add(b);
            }
            return res;
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
