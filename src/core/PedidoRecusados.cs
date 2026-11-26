using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Produtos que o MV RECUSA no pedido à CAF por não serem padronizados — lembrados em
    // dados\pedido-recusados.txt para saírem da SUGESTÃO do pré-pedido.
    //
    // Por que existe: o pré-pedido sugere pelo consumo, e um não padronizado que teve saída
    // voltava a ser sugerido toda vez; o farmacêutico só descobria depois de enviar, no aviso
    // de "produtos recusados" (log de 21/08 a 25/11/2026: 16559 e 13645, duas vezes cada).
    //
    // O que NÃO entra aqui: recusa por outro motivo. No mesmo log a agulha 40x12 (4992) e a
    // fita de glicemia (5738) foram recusadas UMA vez, com mensagem de configuração de
    // estoque, e seguem sendo pedidas normalmente — lembrar qualquer recusa tiraria da
    // sugestão dois dos itens de maior giro. Só a mensagem de "não ... Padronizado" é
    // permanente o bastante para virar regra.
    //
    // O produto continua NA GRADE (sugestão 0): quem digitar a quantidade manda assim mesmo,
    // e se o MV aceitar (o cadastro mudou) ele sai da lista sozinho — ver Liberar.
    public static class PedidoRecusados
    {
        private static readonly object _lock = new object();
        private static string _caminho;
        // código -> o que vai depois do '#' na linha (nome e data), para reescrever igual
        private static readonly Dictionary<int, string> _itens = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> _datas = new Dictionary<int, string>();

        public static void Init(string baseDir)
        {
            lock (_lock)
            {
                _itens.Clear(); _datas.Clear();
                try
                {
                    string dir = Path.Combine(baseDir, "dados");
                    Directory.CreateDirectory(dir);
                    _caminho = Path.Combine(dir, "pedido-recusados.txt");
                    if (!File.Exists(_caminho)) return;   // nasce na primeira recusa
                    foreach (var linha in File.ReadAllLines(_caminho, Encoding.UTF8))
                    {
                        string s = (linha ?? "").Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        string nota = "";
                        int cerq = s.IndexOf('#');
                        if (cerq >= 0) { nota = s.Substring(cerq + 1).Trim(); s = s.Substring(0, cerq).Trim(); }
                        int cod;
                        if (!int.TryParse(s, out cod) || cod <= 0) continue;
                        _itens[cod] = nota;
                        _datas[cod] = DataDaNota(nota);
                    }
                    Logger.Log("Pedido CAF: " + _itens.Count + " produto(s) não padronizado(s) fora da sugestão (" + _caminho + ").");
                }
                catch (Exception ex) { Logger.Log("Pedido CAF: falha ao ler os recusados: " + ex.Message); }
            }
        }

        // O MV já recusou este produto por não ser padronizado?
        public static bool Recusado(int codigo)
        {
            lock (_lock) return _itens.ContainsKey(codigo);
        }

        // Data da recusa ("dd/MM/yyyy"), ou "" quando a linha foi escrita à mão sem data.
        public static string Quando(int codigo)
        {
            lock (_lock)
            {
                string d;
                return _datas.TryGetValue(codigo, out d) ? d : "";
            }
        }

        // Só a recusa por NÃO PADRONIZADO é lembrada (ver o comentário da classe).
        public static bool EhNaoPadronizado(string motivo)
        {
            if (string.IsNullOrEmpty(motivo)) return false;
            return SemAcento(motivo).IndexOf("PADRONIZADO", StringComparison.Ordinal) >= 0;
        }

        // Guarda os recusados por não padronização. nomes: código -> nome (pode ser null).
        // Devolve quantos entraram de novo na lista.
        public static int Aprender(List<PedidoRejeitado> rejeitados, Dictionary<int, string> nomes)
        {
            if (rejeitados == null || rejeitados.Count == 0) return 0;
            int novos = 0;
            lock (_lock)
            {
                foreach (var r in rejeitados)
                {
                    if (r == null || r.Codigo <= 0 || !EhNaoPadronizado(r.Motivo)) continue;
                    if (_itens.ContainsKey(r.Codigo)) continue;
                    string nome = null;
                    if (nomes != null) nomes.TryGetValue(r.Codigo, out nome);
                    string data = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                    _itens[r.Codigo] = (string.IsNullOrEmpty(nome) ? "" : Limpo(nome) + " - ") + "recusado em " + data;
                    _datas[r.Codigo] = data;
                    novos++;
                }
                if (novos > 0) Salvar();
            }
            if (novos > 0) Logger.Log("Pedido CAF: " + novos + " produto(s) não padronizado(s) guardado(s) — saem da sugestão.");
            return novos;
        }

        // Produtos que o MV ACEITOU num pedido: se algum estava na lista, o cadastro mudou
        // (virou padronizado) e ele volta a ser sugerido. Devolve quantos saíram.
        public static int Liberar(IEnumerable<int> aceitos)
        {
            if (aceitos == null) return 0;
            int saiu = 0;
            lock (_lock)
            {
                foreach (int cod in aceitos)
                    if (_itens.Remove(cod)) { _datas.Remove(cod); saiu++; }
                if (saiu > 0) Salvar();
            }
            if (saiu > 0) Logger.Log("Pedido CAF: " + saiu + " produto(s) aceito(s) pelo MV saíram da lista de recusados.");
            return saiu;
        }

        private static void Salvar()
        {
            if (_caminho == null) return;   // Init não rodou (harness de teste)
            try
            {
                var cods = new List<int>(_itens.Keys);
                cods.Sort();
                var sb = new StringBuilder();
                // Sem acentos, como os demais arquivos de dados do app.
                sb.AppendLine("# Produtos que o MV recusou no pedido a CAF por NAO serem padronizados.");
                sb.AppendLine("# Eles ficam com sugestao 0 na Previsao de Consumo (continuam na grade).");
                sb.AppendLine("#");
                sb.AppendLine("# O app escreve aqui sozinho quando o MV recusa, e tira a linha quando o MV");
                sb.AppendLine("# aceita o produto num pedido. Para voltar a sugerir um produto, apague a linha.");
                sb.AppendLine();
                foreach (int c in cods)
                {
                    string nota = _itens[c];
                    sb.AppendLine(c + (string.IsNullOrEmpty(nota) ? "" : "  # " + nota));
                }
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Pedido CAF: falha ao salvar os recusados: " + ex.Message); }
        }

        // "... recusado em 25/11/2026" -> "25/11/2026"
        private static string DataDaNota(string nota)
        {
            if (string.IsNullOrEmpty(nota)) return "";
            int i = nota.LastIndexOf("recusado em ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string d = nota.Substring(i + "recusado em ".Length).Trim();
            return d.Length >= 10 ? d.Substring(0, 10) : "";
        }

        // O nome vai num comentário de UMA linha: sem quebra e sem '#'.
        private static string Limpo(string s)
        {
            return s.Replace('\r', ' ').Replace('\n', ' ').Replace('#', ' ').Trim();
        }

        private static string SemAcento(string s)
        {
            string d = s.ToUpperInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return sb.ToString();
        }
    }
}
