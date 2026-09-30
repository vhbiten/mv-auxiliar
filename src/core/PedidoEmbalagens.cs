using System;
using System.Collections.Generic;
using System.IO;

namespace SoulmvKit.Core
{
    // Um cadastro de embalagem: código + quantos vêm na embalagem + nome (p/ exibição).
    // Qtd 0 = produto LISTADO mas ainda sem quantidade informada (aguardando o farmacêutico);
    // a heurística do pedido só usa quantidades >= 2.
    public class EmbalagemItem
    {
        public int Codigo;
        public int Qtd;
        public string Nome;
    }

    // Cadastro local de EMBALAGEM FECHADA por produto (dados/pedido-embalagens.txt).
    // Editado na tela "Cadastro de embalagens" do Pedido CAF; a lista pode ser populada
    // com os produtos movimentados (sem quantidade) para o usuário só preencher.
    // Formato do arquivo (legível/editável à mão): uma linha por produto
    //   codigo=qtd   # nome        (qtd 0 = listado sem quantidade; qtd -1 = semente removida)
    public static class PedidoEmbalagens
    {
        private static string _caminho;
        private static readonly Dictionary<int, int> _mapa = new Dictionary<int, int>();
        private static readonly Dictionary<int, string> _nomes = new Dictionary<int, string>();

        // Semente embutida: embalagens padrão de fábrica. Editáveis/removíveis na tela
        // (a remoção grava um tombstone "codigo=-1" para a semente não voltar no Init).
        // CONFERIR com a equipe e ajustar conforme as embalagens reais dos fornecedores.
        private static readonly Dictionary<int, int> _sementeQtd = new Dictionary<int, int>
        {
            { 13666, 12 },    // ALCOOL 70% 1000ML: caixa c/ 12 frascos
            { 5835, 500 },    // COMPRESSA GAZE 7,5X7,5: pacote c/ 500
            { 5738, 50 },     // FITA P/ GLICEMIA CAPILAR: frasco c/ 50
            { 13779, 10 },    // LUVA DE PROCEDIMENTO G: fardo c/ 10 caixas
            { 12091, 100 },   // SERINGA DESCARTAVEL 10ML SLIP: caixa c/ 100
        };
        private static readonly Dictionary<int, string> _sementeNome = new Dictionary<int, string>
        {
            { 13666, "ALCOOL 70% 1000ML" },
            { 5835, "COMPRESSA GAZE 7,5CMX7,5CM 13 FIOS ESTERIL 10UNID" },
            { 5738, "FITA P/ GLICEMIA CAPILAR" },
            { 13779, "LUVA DE PROCEDIMENTO G" },
            { 12091, "SERINGA DESCARTAVEL 10 ML SLIP" },
        };

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "pedido-embalagens.txt");
            _mapa.Clear(); _nomes.Clear();

            // 1) semente de fábrica; 2) arquivo do usuário por cima (inclusive tombstones)
            foreach (var kv in _sementeQtd)
            {
                _mapa[kv.Key] = kv.Value;
                string nm; _nomes[kv.Key] = _sementeNome.TryGetValue(kv.Key, out nm) ? nm : "";
            }
            try
            {
                if (File.Exists(_caminho))
                    foreach (var linha in File.ReadAllLines(_caminho))
                    {
                        string s = linha.Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        int ig = s.IndexOf('=');
                        if (ig <= 0) continue;
                        int cod, qtd;
                        if (!int.TryParse(s.Substring(0, ig).Trim(), out cod) || cod <= 0) continue;
                        string resto = s.Substring(ig + 1);
                        string nome = "";
                        int cerq = resto.IndexOf('#');
                        if (cerq >= 0) { nome = resto.Substring(cerq + 1).Trim(); resto = resto.Substring(0, cerq); }
                        if (!int.TryParse(resto.Trim(), out qtd)) continue;
                        if (qtd < 0) { _mapa.Remove(cod); _nomes.Remove(cod); continue; }   // tombstone
                        _mapa[cod] = qtd <= 1 ? 0 : qtd;   // 0/1 = listado sem quantidade
                        if (nome.Length > 0) _nomes[cod] = nome;
                    }
                Logger.Log("Embalagens: " + _mapa.Count + " produto(s) listados, " + ComQuantidade() + " com quantidade.");
            }
            catch (Exception ex) { Logger.Log("Embalagens: falha ao ler (" + ex.Message + "). Usando só a semente."); }
        }

        private static int ComQuantidade()
        {
            int n = 0;
            foreach (var kv in _mapa) if (kv.Value >= 2) n++;
            return n;
        }

        // Quantidade da embalagem usada pela HEURÍSTICA: 0 = sem cadastro utilizável.
        public static int Obter(int codigo)
        {
            int q;
            return _mapa.TryGetValue(codigo, out q) && q >= 2 ? q : 0;
        }

        // Nome guardado no cadastro (pode ser vazio se nunca foi informado).
        public static string Nome(int codigo)
        {
            string n;
            return _nomes.TryGetValue(codigo, out n) ? n : "";
        }

        // Todos os cadastros, ordenados por nome (sem nome vai para o fim, por código).
        public static List<EmbalagemItem> Listar()
        {
            var res = new List<EmbalagemItem>();
            foreach (var kv in _mapa)
            {
                var it = new EmbalagemItem();
                it.Codigo = kv.Key; it.Qtd = kv.Value;
                string nm; it.Nome = _nomes.TryGetValue(kv.Key, out nm) ? nm : "";
                res.Add(it);
            }
            res.Sort(delegate(EmbalagemItem a, EmbalagemItem b)
            {
                bool an = a.Nome.Length > 0, bn = b.Nome.Length > 0;
                if (an != bn) return an ? -1 : 1;
                if (an)
                {
                    int c = string.Compare(a.Nome, b.Nome, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
                return a.Codigo.CompareTo(b.Codigo);
            });
            return res;
        }

        // Define a quantidade da embalagem (>= 2). Mantém/atualiza o nome quando informado.
        public static void Definir(int codigo, int qtd, string nome)
        {
            if (codigo <= 0 || qtd < 2) return;
            _mapa[codigo] = qtd;
            if (!string.IsNullOrEmpty(nome)) _nomes[codigo] = nome;
            Salvar();
        }

        // Limpa a quantidade mas MANTÉM o produto listado (aguardando preenchimento).
        public static void LimparQtd(int codigo)
        {
            if (!_mapa.ContainsKey(codigo)) return;
            _mapa[codigo] = 0;
            Salvar();
        }

        // Tira o produto da lista (semente removida vira tombstone no arquivo).
        public static void Remover(int codigo)
        {
            if (!_mapa.Remove(codigo)) return;
            _nomes.Remove(codigo);
            Salvar();
        }

        public static bool Existe(int codigo)
        {
            return _mapa.ContainsKey(codigo);
        }

        // Lista um código novo sem quantidade (a heurística ignora até preencher).
        public static void AdicionarSemQtd(int codigo, string nome)
        {
            if (codigo <= 0 || _mapa.ContainsKey(codigo)) return;
            _mapa[codigo] = 0;
            if (!string.IsNullOrEmpty(nome)) _nomes[codigo] = nome;
            Salvar();
        }

        // Atualiza só o nome de exibição (ex.: resolvido ao vivo no MV depois do cadastro).
        public static void AtualizarNome(int codigo, string nome)
        {
            if (!_mapa.ContainsKey(codigo) || string.IsNullOrEmpty(nome)) return;
            _nomes[codigo] = nome;
            Salvar();
        }

        // POPULAÇÃO DA LISTA: adiciona (sem quantidade) os produtos que ainda não estão
        // listados — usado com os movimentados do R_MOVI_ESTOQ para o farmacêutico só
        // preencher as quantidades. Devolve quantos entraram de novo.
        public static int AdicionarListados(List<MovProduto> produtos)
        {
            if (produtos == null || produtos.Count == 0) return 0;
            int novos = 0;
            foreach (var p in produtos)
            {
                if (p.Codigo <= 0 || _mapa.ContainsKey(p.Codigo)) continue;
                _mapa[p.Codigo] = 0;
                if (!string.IsNullOrEmpty(p.Nome)) _nomes[p.Codigo] = p.Nome;
                novos++;
            }
            if (novos > 0) Salvar();
            return novos;
        }

        private static void Salvar()
        {
            if (_caminho == null) return;   // Init não rodou (ex.: harness de preview)
            try
            {
                var cods = new List<int>(_mapa.Keys);
                cods.Sort();
                var linhas = new List<string>();
                linhas.Add("# Embalagem fechada por produto (codigo=qtd). Editado pela tela Pedido CAF.");
                linhas.Add("# qtd 0 = listado sem quantidade; codigo=-1 = semente removida pelo usuario.");
                foreach (int c in cods)
                {
                    string nome; _nomes.TryGetValue(c, out nome);
                    linhas.Add(c + "=" + _mapa[c] + (string.IsNullOrEmpty(nome) ? "" : "  # " + nome));
                }
                // tombstones: semente que o usuário removeu precisa ficar registrada
                foreach (var kv in _sementeQtd)
                    if (!_mapa.ContainsKey(kv.Key))
                    {
                        string nm; _sementeNome.TryGetValue(kv.Key, out nm);
                        linhas.Add(kv.Key + "=-1" + (string.IsNullOrEmpty(nm) ? "" : "  # " + nm + " (removido)"));
                    }
                File.WriteAllLines(_caminho, linhas.ToArray());
            }
            catch (Exception ex) { Logger.Log("Embalagens: falha ao salvar: " + ex.Message); }
        }
    }
}
