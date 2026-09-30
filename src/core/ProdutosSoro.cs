using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SoulmvKit.Core
{
    // Quem é SORO no pré-pedido.
    //
    // No MV os soros ficam na MESMA espécie dos medicamentos (1) — não há parâmetro de
    // relatório que os separe, e o R_MOVI_ESTOQ não devolve a classe de cada produto. Então a
    // separação é feita aqui, no cliente, por NOME (o cadastro do MV é consistente: "SORO
    // FISIOLOGICO ...", "SORO GLICOSADO ...", "RINGER COM LACTATO ...").
    //
    // Conferido contra o cadastro real (228 medicamentos movimentados em 30 dias, 17/08/2026):
    // a regra pega os 8 soros + ringer + água destilada, e NÃO pega as ampolas que são
    // medicamento de verdade — CLORETO DE SODIO 0,9% AMP 10ML, GLICOSE 50% AMP 10ML,
    // BICARBONATO DE SODIO 8,4% AMP 10ML.
    //
    // O que a regra não adivinha (manitol em bolsa, soluções de hemodiálise, um soro com nome
    // fora do padrão) vai no arquivo dados/soros.txt, que o farmacêutico ajusta:
    //   9026567     -> força tratar como soro
    //   -13498      -> força NÃO tratar como soro
    public static class ProdutosSoro
    {
        private static string _caminho;
        private static readonly HashSet<int> _incluir = new HashSet<int>();
        private static readonly HashSet<int> _excluir = new HashSet<int>();

        // Trechos que identificam soro no nome do produto (comparação sem acento/caixa).
        private static readonly string[] Padroes = { "SORO ", "RINGER", "AGUA DESTILADA" };

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "soros.txt");
            _incluir.Clear(); _excluir.Clear();

            try
            {
                if (!File.Exists(_caminho)) { CriarModelo(); return; }
                foreach (var linha in File.ReadAllLines(_caminho))
                {
                    string s = linha.Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int cerq = s.IndexOf('#');
                    if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                    if (s.Length == 0) continue;
                    bool tirar = s.StartsWith("-");
                    if (tirar) s = s.Substring(1).Trim();
                    int cod;
                    if (!int.TryParse(s, out cod) || cod <= 0) continue;
                    if (tirar) { _excluir.Add(cod); _incluir.Remove(cod); }
                    else { _incluir.Add(cod); _excluir.Remove(cod); }
                }
                Logger.Log("Soros: " + _incluir.Count + " forçados e " + _excluir.Count + " excluídos por " + _caminho);
            }
            catch (Exception ex) { Logger.Log("Soros: falha ao ler " + _caminho + ": " + ex.Message); }
        }

        // O arquivo nasce só com as instruções: a regra por nome já resolve o dia a dia.
        private static void CriarModelo()
        {
            try
            {
                // Sem acentos, como os demais arquivos de dados do app: evita depender da
                // codificacao que o editor de texto do usuario resolver usar.
                var sb = new StringBuilder();
                sb.AppendLine("# Quais produtos entram no pedido de SOROS (tipo \"Soros\" na Previsao de Consumo).");
                sb.AppendLine("#");
                sb.AppendLine("# O app ja reconhece pelo NOME: SORO FISIOLOGICO, SORO GLICOSADO, RINGER, AGUA DESTILADA.");
                sb.AppendLine("# Use este arquivo so para as excecoes, uma por linha:");
                sb.AppendLine("#");
                sb.AppendLine("#   9026567    -> trata como soro (ex.: MANITOL 20% BOLSA 250ML)");
                sb.AppendLine("#   -13498     -> NAO trata como soro (volta para o pedido de medicamentos)");
                sb.AppendLine("#");
                sb.AppendLine("# Depois de editar, feche e abra o programa.");
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Soros: não consegui criar " + _caminho + ": " + ex.Message); }
        }

        public static bool EhSoro(int codigo, string nome)
        {
            if (_excluir.Contains(codigo)) return false;
            if (_incluir.Contains(codigo)) return true;
            if (string.IsNullOrEmpty(nome)) return false;
            string n = Normalizar(nome);
            foreach (var p in Padroes) if (n.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Sem acento e em maiúsculas: o cadastro do MV mistura "AGUA"/"ÁGUA".
        private static string Normalizar(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToUpperInvariant())
            {
                switch (c)
                {
                    case 'Á': case 'À': case 'Â': case 'Ã': sb.Append('A'); break;
                    case 'É': case 'Ê': sb.Append('E'); break;
                    case 'Í': sb.Append('I'); break;
                    case 'Ó': case 'Ô': case 'Õ': sb.Append('O'); break;
                    case 'Ú': sb.Append('U'); break;
                    case 'Ç': sb.Append('C'); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
